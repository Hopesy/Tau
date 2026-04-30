using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class AzureOpenAiResponsesProviderTests
{
    [Fact]
    public void RegisterAll_UsesDedicatedAzureResponsesProvider()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.IsType<AzureOpenAiResponsesProvider>(registry.Get("azure-openai-responses"));
    }

    [Fact]
    public async Task Stream_PostsResponsesInputToAzureBaseUrlWithApiKeyHeader()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            Assert.True(request.Headers.TryGetValues("api-key", out var apiKeys));
            Assert.Equal("azure-key", Assert.Single(apiKeys));
            Assert.Null(request.Headers.Authorization);
            return OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.output_item.added","item":{"id":"msg_1","type":"message"}}

                data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"azure"}

                data: {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"output_text","text":"azure","annotations":[]}]}}

                data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":6}}}

                """);
        });
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAiResponsesProvider(client);
        var model = BuildAzureModel() with { BaseUrl = "https://example.openai.azure.com/openai/v1" };

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new AzureOpenAiResponsesOptions
            {
                ApiKey = "azure-key",
                AzureApiVersion = "2025-04-01-preview",
                AzureDeploymentName = "deployment-gpt-4o-mini",
                SessionId = "session-1"
            }));

        Assert.Equal("/openai/v1/responses", handler.RequestUri!.AbsolutePath);
        Assert.Equal("?api-version=2025-04-01-preview", handler.RequestUri.Query);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        Assert.Equal("deployment-gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.TryGetProperty("input", out _));
        Assert.False(root.TryGetProperty("messages", out _));
        Assert.Equal("session-1", root.GetProperty("prompt_cache_key").GetString());
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal(new Usage(5, 6), done.Message.Usage);
        Assert.Equal("azure", Assert.IsType<TextContent>(Assert.Single(done.Message.Content)).Text);
    }

    [Fact]
    public async Task Stream_ResolvesResourceNameAndDeploymentNameMap()
    {
        using var env = new EnvironmentOverride(new Dictionary<string, string?>
        {
            ["AZURE_OPENAI_BASE_URL"] = null,
            ["AZURE_OPENAI_RESOURCE_NAME"] = null,
            ["AZURE_OPENAI_API_VERSION"] = null,
            ["AZURE_OPENAI_DEPLOYMENT_NAME_MAP"] = "gpt-4o-mini=my-mini,gpt-5.2=my-gpt52"
        });
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAiResponsesProvider(client);

        _ = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildAzureModel() with { BaseUrl = string.Empty },
            new LlmContext { Messages = [new UserMessage("hi")] },
            new AzureOpenAiResponsesOptions
            {
                ApiKey = "azure-key",
                AzureResourceName = "tau-resource"
            }));

        Assert.Equal("tau-resource.openai.azure.com", handler.RequestUri!.Host);
        Assert.Equal("/openai/v1/responses", handler.RequestUri.AbsolutePath);
        Assert.Equal("?api-version=v1", handler.RequestUri.Query);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal("my-mini", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StreamSimple_AddsReasoningEffortAndEncryptedReasoningInclude()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAiResponsesProvider(client);

        _ = await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildAzureModel() with
            {
                BaseUrl = "https://example.openai.azure.com/openai/v1",
                Reasoning = true
            },
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "azure-key",
                Reasoning = ThinkingLevel.High
            }));

        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
        Assert.Contains(
            root.GetProperty("include").EnumerateArray(),
            item => item.GetString() == "reasoning.encrypted_content");
    }

    [Fact]
    public async Task Stream_ReportsMissingBaseUrl()
    {
        using var env = new EnvironmentOverride(new Dictionary<string, string?>
        {
            ["AZURE_OPENAI_BASE_URL"] = null,
            ["AZURE_OPENAI_RESOURCE_NAME"] = null
        });
        var provider = new AzureOpenAiResponsesProvider(new HttpClient(new OpenAiResponsesProviderTests.StubHandler(
            _ => throw new InvalidOperationException("request should not be sent"))));

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildAzureModel() with { BaseUrl = string.Empty },
            new LlmContext { Messages = [new UserMessage("hi")] },
            new AzureOpenAiResponsesOptions { ApiKey = "azure-key" }));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("Azure OpenAI base URL is required", error.Error, StringComparison.Ordinal);
    }

    private static Model BuildAzureModel() => new()
    {
        Id = "gpt-4o-mini",
        Name = "GPT-4o mini (Azure)",
        Api = "azure-openai-responses",
        Provider = "azure-openai-responses",
        BaseUrl = "https://example.openai.azure.com/openai/v1",
        Reasoning = false,
        MaxOutputTokens = 16_384
    };

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvironmentOverride(IDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
