using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentProviderAttributionTests
{
    private static Model CreateModel(string provider, string? baseUrl = null) =>
        new()
        {
            Id = "test-model",
            Name = "Test Model",
            Api = "openai-chat-completions",
            Provider = provider,
            BaseUrl = baseUrl
        };

    [Fact]
    public void OpenRouterProvider_AddsOpenRouterAttributionHeaders()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("openrouter"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("https://pi.dev", headers!["HTTP-Referer"]);
        Assert.Equal("pi", headers["X-OpenRouter-Title"]);
        Assert.Equal("cli-agent", headers["X-OpenRouter-Categories"]);
    }

    [Fact]
    public void OpenRouterBaseUrl_AddsOpenRouterAttributionHeaders()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("custom", "https://openrouter.ai/api/v1"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("pi", headers!["X-OpenRouter-Title"]);
    }

    [Fact]
    public void NvidiaProvider_AddsBillingOriginHeader()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("nvidia"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("Pi", headers!["X-BILLING-INVOKE-ORIGIN"]);
    }

    [Fact]
    public void NvidiaNimHost_AddsBillingOriginHeader()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("custom", "https://integrate.api.nvidia.com/v1"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("Pi", headers!["X-BILLING-INVOKE-ORIGIN"]);
    }

    [Theory]
    [InlineData("cloudflare-workers-ai", null)]
    [InlineData("cloudflare-ai-gateway", null)]
    [InlineData("custom", "https://api.cloudflare.com/client/v4/accounts/x/ai")]
    [InlineData("custom", "https://gateway.ai.cloudflare.com/v1/x/y/openai")]
    public void CloudflareModels_AddPiCodingAgentUserAgent(string provider, string? baseUrl)
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel(provider, baseUrl),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("pi-coding-agent", headers!["User-Agent"]);
    }

    [Fact]
    public void VercelGatewayProvider_AddsRefererAndTitle()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("vercel-ai-gateway"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("https://pi.dev", headers!["http-referer"]);
        Assert.Equal("pi", headers["x-title"]);
    }

    [Fact]
    public void VercelGatewayHost_AddsRefererAndTitle()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("custom", "https://ai-gateway.vercel.sh/v1"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.NotNull(headers);
        Assert.Equal("pi", headers!["x-title"]);
    }

    [Fact]
    public void TelemetryDisabled_SuppressesAttributionHeaders()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("openrouter"),
            installTelemetryEnabled: false,
            sessionId: null);

        Assert.Null(headers);
    }

    [Fact]
    public void UnknownProvider_WithoutSession_ReturnsNull()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("anthropic"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.Null(headers);
    }

    [Theory]
    [InlineData("opencode", null)]
    [InlineData("opencode-go", null)]
    [InlineData("custom", "https://opencode.ai/v1")]
    public void OpenCodeModels_WithSession_AddSessionHeaders(string provider, string? baseUrl)
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel(provider, baseUrl),
            installTelemetryEnabled: true,
            sessionId: "session-123");

        Assert.NotNull(headers);
        Assert.Equal("session-123", headers!["x-opencode-session"]);
        Assert.Equal("pi", headers["x-opencode-client"]);
    }

    [Fact]
    public void OpenCodeModel_WithoutSession_OmitsSessionHeaders()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("opencode"),
            installTelemetryEnabled: true,
            sessionId: null);

        Assert.Null(headers);
    }

    [Fact]
    public void SessionId_OnNonOpenCodeProvider_OmitsSessionHeaders()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("anthropic"),
            installTelemetryEnabled: true,
            sessionId: "session-123");

        Assert.Null(headers);
    }

    [Fact]
    public void SessionHeadersSurviveEvenWhenTelemetryDisabled()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("opencode"),
            installTelemetryEnabled: false,
            sessionId: "session-123");

        Assert.NotNull(headers);
        Assert.Equal("session-123", headers!["x-opencode-session"]);
        Assert.Equal("pi", headers["x-opencode-client"]);
    }

    [Fact]
    public void ExplicitHeaderSources_OverrideAttributionBase()
    {
        var explicitHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-OpenRouter-Title"] = "custom-title"
        };

        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("openrouter"),
            installTelemetryEnabled: true,
            sessionId: null,
            explicitHeaders);

        Assert.NotNull(headers);
        Assert.Equal("custom-title", headers!["X-OpenRouter-Title"]);
        // 未被覆盖的归因请求头必须保留
        Assert.Equal("https://pi.dev", headers["HTTP-Referer"]);
    }

    [Fact]
    public void LaterHeaderSources_OverrideEarlierSources()
    {
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom"] = "first"
        };
        var second = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom"] = "second"
        };

        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("anthropic"),
            installTelemetryEnabled: true,
            sessionId: null,
            first,
            second);

        Assert.NotNull(headers);
        Assert.Equal("second", headers!["X-Custom"]);
    }

    [Fact]
    public void NoApplicableHeaders_ReturnsNull()
    {
        var headers = CodingAgentProviderAttribution.MergeAttributionHeaders(
            CreateModel("anthropic"),
            installTelemetryEnabled: true,
            sessionId: null,
            null,
            null);

        Assert.Null(headers);
    }
}
