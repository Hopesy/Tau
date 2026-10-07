// 作者：xxx
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Providers.PiMessages;

namespace Tau.Ai.Tests;

/// <summary>【Radius】【配置协议测试】验证网关根路径、目录清洗及模型元数据保真。</summary>
public sealed class RadiusGatewayConfigTests
{
    private const string ValidModel = """{"id":"chat","name":"Chat","reasoning":true,"input":["text","image"],"cost":{"input":1.5,"output":2.5},"contextWindow":1000,"maxTokens":100}""";

    /// <summary>【Radius】【根路径测试】配置路径相对网关 origin，认证头只在有密钥时发送。</summary>
    /// <param name="key">可选合成密钥。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("fixture-key")]
    public async Task Request_UsesOriginRootAndOptionalAuthorization(string? key)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("https://gateway.invalid:8443/v1/config", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Headers.Accept.ToString());
            Assert.Equal(key is null ? null : "Bearer " + key, request.Headers.Authorization?.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent(Config(ValidModel)) };
        });
        using var client = new HttpClient(handler);
        var result = await RadiusGatewayConfigLoader.LoadAsync("https://gateway.invalid:8443/proxy/sub?tenant=x", key, client);
        Assert.Single(result.Models);
    }

    /// <summary>【Radius】【顶层清洗】非对象、错误 baseUrl 或错误 models 字段不构成有效配置。</summary>
    /// <param name="json">无效配置。</param>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"baseUrl\":42,\"models\":[]}")]
    [InlineData("{\"baseUrl\":null,\"models\":[]}")]
    [InlineData("{\"baseUrl\":\"https://x.invalid\",\"models\":{}}")]
    public void InvalidRoot_ReturnsNoConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(RadiusGatewayConfigLoader.Parse(document.RootElement));
    }

    /// <summary>【Radius】【条目清洗】缺少必需字段或类型错误的条目被过滤，不影响后续有效模型。</summary>
    /// <param name="field">修改字段。</param><param name="replacement">替换 JSON，空引用表示删除。</param>
    [Theory]
    [InlineData("input", null)]
    [InlineData("input", "\"text\"")]
    [InlineData("cost", null)]
    [InlineData("cost", "[]")]
    [InlineData("cost", "null")]
    [InlineData("id", "42")]
    [InlineData("name", "false")]
    [InlineData("reasoning", "\"true\"")]
    [InlineData("contextWindow", "\"1000\"")]
    [InlineData("maxTokens", "null")]
    public void InvalidModelFields_AreSkipped(string field, string? replacement)
    {
        var invalid = JsonNode.Parse(ValidModel)!.AsObject();
        if (replacement is null) invalid.Remove(field); else invalid[field] = JsonNode.Parse(replacement);
        using var document = JsonDocument.Parse(Config("null,[],42," + invalid.ToJsonString() + "," + ValidModel));
        var result = RadiusGatewayConfigLoader.Parse(document.RootElement)!;
        Assert.Equal("chat", Assert.Single(result.Models).Id);
    }

    /// <summary>【Radius】【元数据保真】推理等级空值、分层费率、缓存价格及整数指数表示都保留。</summary>
    [Fact]
    public void Mapping_PreservesThinkingCostTiersAndIntegerNumericForms()
    {
        using var document = JsonDocument.Parse(Config("""
            {"id":"rich","name":"Rich","reasoning":true,"thinkingLevelMap":{"off":null,"high":"deep"},
             "input":["text","image"],"contextWindow":1e3,"maxTokens":100.0,
             "cost":{"input":1.5,"output":2.5,"cacheRead":0.1,"cacheWrite":0.2,
               "tiers":[{"inputTokensAbove":1000.5,"input":4.5,"output":5.5,"cacheRead":0.3}]}}
            """));
        var config = RadiusGatewayConfigLoader.Parse(document.RootElement)!;
        var model = Assert.Single(RadiusGatewayConfigLoader.GetModelsFromConfig("custom-radius", config));
        Assert.Equal("custom-radius", model.Provider); Assert.Equal("pi-messages", model.Api);
        Assert.Equal("https://stream.invalid", model.BaseUrl); Assert.Null(model.ThinkingLevelMap!["off"]); Assert.Equal("deep", model.ThinkingLevelMap["high"]);
        Assert.Equal(1000, model.ContextWindow); Assert.Equal(100, model.MaxOutputTokens); Assert.Equal(new[] { "text", "image" }, model.InputModalities);
        Assert.Equal(0.1m, model.Cost!.Value.CacheReadPerMillion); Assert.Equal(0.2m, model.Cost.Value.CacheWritePerMillion);
        var tier = Assert.Single(model.Cost.Value.Tiers!); Assert.Equal(1000.5, tier.InputTokensAbove);
        Assert.Equal(4.5m, tier.InputPerMillion); Assert.Equal(5.5m, tier.OutputPerMillion); Assert.Equal(0.3m, tier.CacheReadPerMillion); Assert.Null(tier.CacheWritePerMillion);
    }

    /// <summary>【Radius】【错误正文】HTTP 失败正文去掉首尾空白，并按上游用单个省略号截断。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task HttpFailure_TruncatesBodyWithUpstreamEllipsis()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent(" \n" + new string('x', 513) + " \n") });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RadiusGatewayConfigLoader.LoadAsync("https://gateway.invalid", null, client));
        Assert.EndsWith(": 403: " + new string('x', 512) + "…", error.Message);
    }

    /// <summary>【Radius】【无效响应】HTTP 成功但顶层格式错误时报告协议错误。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task InvalidHttpConfiguration_ReportsProtocolError()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RadiusGatewayConfigLoader.LoadAsync("https://gateway.invalid", null, client));
        Assert.Equal("Invalid Radius config from https://gateway.invalid", error.Message);
    }

    /// <summary>【Radius】【配置夹具】把模型 JSON 嵌入最小配置。</summary><param name="models">逗号分隔模型。</param><returns>配置文本。</returns>
    private static string Config(string models) => "{\"baseUrl\":\"https://stream.invalid\",\"models\":[" + models + "]}";

    /// <summary>【Radius】【合成 HTTP】返回受控响应，不连接外部服务。</summary><param name="send">响应工厂。</param>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        /// <summary>检查请求并生成响应。</summary><param name="request">请求。</param><param name="cancellationToken">信号。</param><returns>响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
