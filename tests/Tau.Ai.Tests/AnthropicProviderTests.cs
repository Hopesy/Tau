using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.Anthropic;

namespace Tau.Ai.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task Stream_AddsAnthropicProviderSpecificOptions()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")], Tools = [BuildTool()] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = true,
                ThinkingBudgetTokens = 1234,
                ThinkingDisplay = "omitted",
                InterleavedThinking = true,
                ToolChoice = AnthropicToolChoice.Tool("read_file"),
                Temperature = 0.8f,
                Metadata = new Dictionary<string, object>
                {
                    ["user_id"] = "user-123",
                    ["ignored"] = "ignored"
                }
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("temperature", out _));

        var thinking = root.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(1234, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal("omitted", thinking.GetProperty("display").GetString());

        var toolChoice = root.GetProperty("tool_choice");
        Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
        Assert.Equal("read_file", toolChoice.GetProperty("name").GetString());
        Assert.Equal("user-123", root.GetProperty("metadata").GetProperty("user_id").GetString());
        Assert.True(handler.Requests[0].Headers.TryGetValues("anthropic-beta", out var betaValues));
        Assert.Equal("interleaved-thinking-2025-05-14", Assert.Single(betaValues));
    }

    [Fact]
    public async Task Stream_AddsAdaptiveThinkingEffortForSupportedModel()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel("claude-opus-4-7-20260101", reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = true,
                Effort = "xhigh",
                ThinkingDisplay = "summarized",
                InterleavedThinking = true
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        var thinking = root.GetProperty("thinking");
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
        Assert.Equal("summarized", thinking.GetProperty("display").GetString());
        Assert.Equal("xhigh", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(handler.Requests[0].Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task Stream_AddsDisabledThinkingAndAllowsTemperature()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("no think")] },
            new AnthropicOptions
            {
                ApiKey = "anthropic-key",
                ThinkingEnabled = false,
                Temperature = 0.8f,
                ToolChoice = "auto"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(0.8f, root.GetProperty("temperature").GetSingle());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamSimple_UsesConfiguredThinkingBudget()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "anthropic-key",
                Reasoning = ThinkingLevel.High,
                ThinkingBudgets = new ThinkingBudgets { High = 2222 }
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var thinking = doc.RootElement.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(2222, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal("summarized", thinking.GetProperty("display").GetString());
    }

    private static Model BuildModel(string id = "claude-sonnet-4-5-20250929", bool reasoning = false) => new()
    {
        Id = id,
        Name = "Claude",
        Api = "anthropic-messages",
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        Reasoning = reasoning,
        MaxOutputTokens = 4096
    };

    private static Tool BuildTool() => new(
        "read_file",
        "Read a file.",
        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement);
}
