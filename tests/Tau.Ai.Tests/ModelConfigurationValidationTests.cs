// 作者：xxx
using System.Text;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed partial class ModelConfigurationValidationTests
{
    /// <summary>【AI】【根结构】错误根节点或提供方容器必须可诊断，内置目录保持可用。</summary>
    /// <param name="json">配置文本。</param><param name="field">预期路径。</param>
    [Theory]
    [InlineData("null", "root")]
    [InlineData("[]", "root")]
    [InlineData("{}", "providers")]
    [InlineData("""{"providers":null}""", "providers")]
    [InlineData("""{"providers":[]}""", "providers")]
    [InlineData("""{"providers":{"bad":false}}""", "providers.bad")]
    public void InvalidRootHasDiagnostic(string json, string field)
    {
        using var fixture = new ConfigurationFixture(json);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.NotEmpty(catalog.GetModels("openai"));
        Assert.Contains(field, fixture.Store.Error);
    }

    /// <summary>【AI】【整份拒绝】任意提供方的无效基础字段使整份文件失效，包括尚未匹配模型的覆盖。</summary>
    /// <param name="provider">无效提供方配置。</param><param name="field">预期错误路径。</param>
    [Theory]
    [InlineData("""{"apiKey":42}""", "apiKey")]
    [InlineData("""{"apiKey":""}""", "apiKey")]
    [InlineData("""{"baseUrl":null}""", "baseUrl")]
    [InlineData("""{"api":false}""", "api")]
    [InlineData("""{"name":""}""", "name")]
    [InlineData("""{"authHeader":"true"}""", "authHeader")]
    [InlineData("""{"headers":[]}""", "headers")]
    [InlineData("""{"headers":{"Authorization":123}}""", "headers.Authorization")]
    [InlineData("""{"compat":null}""", "compat")]
    [InlineData("""{"oauth":"unknown"}""", "oauth")]
    [InlineData("""{"models":null}""", "models")]
    [InlineData("""{"models":[42]}""", "models.0")]
    [InlineData("""{"models":[{}]}""", "models.0.id")]
    [InlineData("""{"models":[{"id":""}]}""", "models.0.id")]
    [InlineData("""{"models":[{"id":"m","reasoning":"false"}]}""", "models.0.reasoning")]
    [InlineData("""{"models":[{"id":"m","input":["audio"]}]}""", "models.0.input")]
    [InlineData("""{"models":[{"id":"m","input":"text"}]}""", "models.0.input")]
    [InlineData("""{"models":[{"id":"m","contextWindow":"1000"}]}""", "models.0.contextWindow")]
    [InlineData("""{"models":[{"id":"m","maxTokens":1.5}]}""", "models.0.maxTokens")]
    [InlineData("""{"models":[{"id":"m","maxTokens":2147483648}]}""", "models.0.maxTokens")]
    [InlineData("""{"models":[{"id":"m","thinkingLevelMap":{"max":false}}]}""", "models.0.thinkingLevelMap.max")]
    [InlineData("""{"models":[{"id":"m","samplingParams":[]}]}""", "models.0.samplingParams")]
    [InlineData("""{"models":[{"id":"m","cost":{"input":1}}]}""", "models.0.cost.output")]
    [InlineData("""{"models":[{"id":"m","cost":{"input":"1","output":2,"cacheRead":0,"cacheWrite":0}}]}""", "models.0.cost.input")]
    [InlineData("""{"modelOverrides":[]}""", "modelOverrides")]
    [InlineData("""{"modelOverrides":{"missing":null}}""", "modelOverrides.missing")]
    [InlineData("""{"modelOverrides":{"missing":{"headers":{"X":null}}}}""", "modelOverrides.missing.headers.X")]
    [InlineData("""{"modelOverrides":{"missing":{"cost":{"tiers":[{"inputTokensAbove":10,"input":1}]}}}}""", "modelOverrides.missing.cost.tiers.0.output")]
    [InlineData("""{"modelOverrides":{"missing":{"cost":{"input":1e999}}}}""", "modelOverrides.missing.cost.input")]
    public void InvalidProviderRejectsWholeFile(string provider, string field)
    {
        using var fixture = new ConfigurationFixture("""{"providers":{"valid":{"models":[{"id":"kept-only-if-valid"}]},"bad":""" + provider + "}}");
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Empty(catalog.GetModels("valid"));
        Assert.Empty(catalog.GetModels("bad"));
        Assert.Contains("providers.bad." + field, fixture.Store.Error);
        Assert.Contains(fixture.File, fixture.Store.Error);
    }

    /// <summary>【AI】【合法配置】BOM、注释、数字写法、空请求头、可空思考映射及局部覆盖均保留实际值。</summary>
    [Fact]
    public void ValidConfigRetainsNumericNotationAndPartialOverrides()
    {
        using var fixture = new ConfigurationFixture("""
            { // 输入内的 URL 不受注释解析影响
              "providers":{
                "local":{"baseUrl":"https://local.invalid/v1","headers":{"Empty":""},"future":{"anything":null},
                  "models":[{"id":"model","contextWindow":1e3,"maxTokens":100.0,
                    "thinkingLevelMap":{"off":null,"high":"high"},"samplingParams":{"nested":{"value":[null,1]}},
                    "cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,
                      "tiers":[{"inputTokensAbove":100,"input":3,"output":4,"cacheRead":0,"cacheWrite":0}]}}]},
                "openai":{"modelOverrides":{"gpt-5.4":{"cost":{"input":9},"headers":{"Empty":""}}}},
                "images":{"models":[{"id":"image","type":"image","cost":{"input":1,"output":2}}]}
              }
            }
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        var model = Assert.Single(catalog.GetModels("local"));
        Assert.Null(fixture.Store.Error);
        Assert.Equal(1000, model.ContextWindow);
        Assert.Equal(100, model.MaxOutputTokens);
        Assert.Equal("", model.Headers!["Empty"]);
        Assert.Equal("https://local.invalid/v1", model.BaseUrl);
        Assert.Equal(9m, catalog.GetModel("openai", "gpt-5.4")!.Cost?.InputPerMillion);
        Assert.Single(catalog.GetAllModels("images"));
    }

    /// <summary>【AI】【诊断保密】验证错误只包含路径和规则，不回显密钥表达式或其他配置值。</summary>
    [Fact]
    public void DiagnosticDoesNotIncludeConfigurationSecrets()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"bad":{"apiKey":"synthetic-secret-marker","models":[{"id":"m","headers":{"X":"synthetic-header-marker"},"reasoning":123}]}}}
            """);
        _ = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Contains("reasoning", fixture.Store.Error);
        Assert.DoesNotContain("synthetic-secret-marker", fixture.Store.Error);
        Assert.DoesNotContain("synthetic-header-marker", fixture.Store.Error);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tau-config-validation-" + Guid.NewGuid().ToString("N"));
        public string File { get; }
        public ModelConfigurationStore Store { get; }

        /// <summary>【AI】【测试配置】建立带 BOM 的隔离配置文件。</summary>
        /// <param name="json">文件正文。</param>
        public ConfigurationFixture(string json)
        {
            Directory.CreateDirectory(_directory);
            File = Path.Combine(_directory, "models.json");
            System.IO.File.WriteAllText(File, json, new UTF8Encoding(true));
            Store = new([File]);
        }

        /// <summary>【AI】【测试清理】核验临时根目录后清理本夹具创建的文件。</summary>
        public void Dispose()
        {
            var path = Path.GetFullPath(_directory);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("tau-config-validation-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture directory.");
            Directory.Delete(path, recursive: true);
        }
    }
}
