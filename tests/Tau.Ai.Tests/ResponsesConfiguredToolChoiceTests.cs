// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 配置回归】工具选择通过配置入口和两类流式入口后保持有效。</summary>
public sealed class ResponsesConfiguredToolChoiceTests
{
    /// <summary>生成协议、入口与配置形态的组合。</summary>
    /// <returns>测试组合。</returns>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var azure in new[] { false, true })
            foreach (var simple in new[] { false, true })
                foreach (var choice in new[] { "auto", "none", "named", "legacy-named", "explicit" })
                    yield return [azure, simple, choice];
    }

    /// <summary>配置加载及供应商专用选项转换不得丢弃工具选择，显式值优先。</summary>
    /// <param name="azure">是否使用 Azure。</param>
    /// <param name="simple">是否使用简化入口。</param>
    /// <param name="choice">工具选择形态。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Request_RetainsConfiguredOrExplicitToolChoice(bool azure, bool simple, string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "tau-tool-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "models.json");
            var choiceJson = choice switch
            {
                "named" => """{"type":"function","name":"lookup"}""",
                "legacy-named" => """{"type":"function","function":{"name":"lookup"}}""",
                "auto" => "\"auto\"", _ => "\"none\""
            };
            await File.WriteAllTextAsync(path, "{\"providers\":{\"test\":{\"apiKey\":\"config-key\",\"options\":{\"serviceTier\":\"default\",\"toolChoice\":" + choiceJson + "}}}}");
            var store = new ModelConfigurationStore([path]);
            var auth = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: store);
            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"));
            using var client = new HttpClient(handler);
            IStreamProvider provider = azure ? new AzureOpenAiResponsesProvider(client) : new OpenAiResponsesProvider(client);
            var registry = new ProviderRegistry();
            registry.Register(provider.Api, provider);
            var model = new Model { Id = "configured", Name = "Configured", Provider = "test", Api = provider.Api, BaseUrl = "https://example.invalid/v1" };
            var context = new LlmContext(null, [new UserMessage("synthetic")], null);
            object? explicitChoice = choice == "explicit" ? new Dictionary<string, object> { ["type"] = "function", ["name"] = "override" } : null;
            var stream = simple ? StreamFunctions.StreamSimple(registry, model, context, new() { ToolChoice = explicitChoice }, store, auth)
                : StreamFunctions.Stream(registry, model, context, azure ? new AzureOpenAiResponsesOptions { ToolChoice = explicitChoice }
                    : new OpenAiResponsesOptions { ToolChoice = explicitChoice }, store, auth);
            await OpenAiResponsesProviderTests.CollectAsync(stream);
            Assert.Equal(StopReason.EndTurn, (await stream.ResultAsync).StopReason);
            using var document = JsonDocument.Parse(handler.CapturedBody);
            var selected = document.RootElement.GetProperty("tool_choice");
            if (choice is "named" or "legacy-named" or "explicit")
            {
                Assert.Equal("function", selected.GetProperty("type").GetString());
                Assert.Equal(choice == "explicit" ? "override" : "lookup", selected.GetProperty("name").GetString());
                Assert.False(selected.TryGetProperty("function", out _));
            }
            else Assert.Equal(choice, selected.GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
