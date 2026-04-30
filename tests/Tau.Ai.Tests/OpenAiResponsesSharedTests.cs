using System.Text;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiResponsesSharedTests
{
    [Fact]
    public void NormalizeToolCallId_RewritesForeignResponsesItemId()
    {
        var target = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://api.openai.com/v1"
        };
        var source = new AssistantMessage([new ToolCallContent("call-1|very-long-foreign-item-id", "read_file", "{}")])
        {
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude"
        };

        var normalized = OpenAiResponsesShared.NormalizeToolCallId("call-1|very-long-foreign-item-id", target, source);

        Assert.StartsWith("call-1|fc_", normalized, StringComparison.Ordinal);
        Assert.True(OpenAiResponsesShared.SplitToolCallId(normalized).ItemId!.Length <= 64);
    }

    [Fact]
    public void ConvertResponsesMessages_InsertsSyntheticToolResultForOrphanedToolCall()
    {
        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://api.openai.com/v1"
        };
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call_1|fc_1", "read_file", "{}")])
                {
                    Api = "openai-responses",
                    Provider = "openai",
                    Model = "gpt-5.4"
                },
                new UserMessage("continue")
            ]
        };

        var messages = OpenAiResponsesShared.ConvertResponsesMessages(model, context);

        Assert.Contains(messages, item => item is Dictionary<string, object> dict &&
            dict.TryGetValue("type", out var type) &&
            Equals(type, "function_call_output") &&
            dict.TryGetValue("call_id", out var callId) &&
            Equals(callId, "call_1"));
    }

    [Fact]
    public void ExtractAccountIdFromJwt_ReadsBase64UrlPayload()
    {
        var payload = """{"https://api.openai.com/auth":{"chatgpt_account_id":"acc_123"}}""";
        var jwt = $"stub.{Base64Url(payload)}.sig";

        var accountId = OpenAiResponsesShared.ExtractAccountIdFromJwt(jwt);

        Assert.Equal("acc_123", accountId);
    }

    public static string BuildFakeJwt(string accountId)
    {
        var payload = "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + accountId + "\"}}";
        return $"stub.{Base64Url(payload)}.sig";
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
