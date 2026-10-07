// 作者：xxx
using System.Net;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【传输配置】验证版本基址和共享请求头覆盖的真实 HTTP 请求。</summary>
public sealed class GoogleTransportTests
{
    /// <summary>【Google】【地址回归】官方根地址、版本地址和代理前缀不会重复追加版本。</summary>
    /// <param name="baseUrl">配置地址。</param><param name="modelId">模型资源。</param><param name="expectedPath">预期路径。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(null, "gemini-test", "/v1beta/models/gemini-test:streamGenerateContent")]
    [InlineData("https://google.example", "gemini-test", "/v1beta/models/gemini-test:streamGenerateContent")]
    [InlineData("https://google.example/v1beta/", "gemini-test", "/v1beta/models/gemini-test:streamGenerateContent")]
    [InlineData("https://google.example/v1", "gemini-test", "/v1/models/gemini-test:streamGenerateContent")]
    [InlineData("https://google.example/proxy/google/v1beta?ignored=1#anchor", "gemini-test", "/proxy/google/v1beta/models/gemini-test:streamGenerateContent")]
    [InlineData("https://google.example/custom", "models/gemini-test", "/custom/models/gemini-test:streamGenerateContent")]
    public async Task GoogleEndpointPreservesConfiguredVersionPath(string? baseUrl, string modelId, string expectedPath)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") });
        using var client = new HttpClient(handler);
        var model = new Model { Id = modelId, Name = "fixture", Api = "google-generative-language", Provider = "google", BaseUrl = baseUrl };
        await new GoogleProvider(client).Stream(model, new() { Messages = [new UserMessage("hi")] }, new() { ApiKey = "synthetic" }).ResultAsync;
        Assert.Equal(expectedPath, handler.RequestUri!.AbsolutePath);
        Assert.Equal("?alt=sse", handler.RequestUri.Query);
        Assert.Empty(handler.RequestUri.Fragment);
    }

    /// <summary>【AI】【头部矩阵】默认客户端标识、认证及内容头支持模型覆盖、请求覆盖和最终删除。</summary>
    /// <param name="api">协议。</param><param name="mode">默认、覆盖或删除场景。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", "default")]
    [InlineData("google-generative-language", "override")]
    [InlineData("google-generative-language", "delete")]
    [InlineData("google-vertex", "default")]
    [InlineData("google-vertex", "override")]
    [InlineData("google-vertex", "delete")]
    [InlineData("google-gemini-cli", "default")]
    [InlineData("google-gemini-cli", "override")]
    [InlineData("google-gemini-cli", "delete")]
    [InlineData("mistral-conversations", "default")]
    [InlineData("mistral-conversations", "override")]
    [InlineData("mistral-conversations", "delete")]
    public async Task RequestHeadersHaveConsistentOverrideOrder(string api, string mode)
    {
        Dictionary<string, string>? captured = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            captured = request.Headers.Concat(request.Content!.Headers).ToDictionary(header => header.Key,
                header => string.Join(header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ",", header.Value), StringComparer.OrdinalIgnoreCase);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") };
        });
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch { "google-vertex" => new GoogleVertexProvider(client), "google-gemini-cli" => new GoogleGeminiCliProvider(client),
            "mistral-conversations" => new MistralProvider(client), _ => new GoogleProvider(client) };
        var authHeader = api is "google-generative-language" or "google-vertex" ? "x-goog-api-key" : "authorization";
        var model = new Model { Id = "fixture", Name = "fixture", Api = api, Provider = api, BaseUrl = "https://provider.example/v1" };
        var options = new StreamOptions { ApiKey = api == "google-gemini-cli" ? """{"token":"synthetic","projectId":"fixture"}""" : "synthetic" };
        if (mode != "default")
        {
            model = model with { Headers = new Dictionary<string, string> { ["Content-Type"] = "model/content", ["User-Agent"] = "model-agent", [authHeader] = "model-auth" } };
            options = options with { Headers = new Dictionary<string, string> { ["content-TYPE"] = mode == "delete" ? null! : "request/content", ["user-AGENT"] = mode == "delete" ? null! : "request-agent", [authHeader.ToUpperInvariant()] = mode == "delete" ? null! : "request-auth" } };
        }
        await provider.Stream(model, new() { Messages = [new UserMessage("hi")] }, options).ResultAsync;
        Assert.NotNull(captured);
        if (mode == "delete")
        {
            Assert.False(captured!.ContainsKey("content-type"));
            Assert.False(captured.ContainsKey("user-agent"));
            Assert.False(captured.ContainsKey(authHeader));
        }
        else if (mode == "override")
        {
            Assert.Equal("request/content", captured!["content-type"]);
            Assert.Equal("request-agent", captured["user-agent"]);
            Assert.Equal("request-auth", captured[authHeader]);
        }
        else
        {
            Assert.StartsWith(api == "google-gemini-cli" ? "google-cloud-sdk " : "pi (", captured!["user-agent"]);
            Assert.Contains("application/json", captured["content-type"]);
            Assert.Contains("synthetic", captured[authHeader]);
        }
    }

    /// <summary>【Google】【内置目录】内置普通模型基址与主线包含版本的声明保持一致。</summary>
    [Fact]
    public void BuiltInGoogleModelsUseVersionedBaseUrls()
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        Assert.All(catalog.GetModels("google"), model => Assert.Equal("https://generativelanguage.googleapis.com/v1beta", model.BaseUrl));
    }
}
