// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ModelMetadataValidationTests
{
    /// <summary>【AI】【元数据校验】交换目录拒绝无效结构、范围及数字类型，并指出具体字段。</summary>
    /// <param name="metadata">无效模型字段。</param><param name="path">预期错误路径。</param>
    [Theory]
    [InlineData("\"inputLimits\":null", "inputLimits")]
    [InlineData("\"inputLimits\":[]", "inputLimits")]
    [InlineData("\"inputLimits\":{\"maxRequestBytes\":0}", "inputLimits.maxRequestBytes")]
    [InlineData("\"inputLimits\":{\"maxRequestBytes\":9223372036854775808}", "inputLimits.maxRequestBytes")]
    [InlineData("\"inputLimits\":{\"images\":false}", "inputLimits.images")]
    [InlineData("\"inputLimits\":{\"images\":{\"maxPerMessage\":-1}}", "inputLimits.images.maxPerMessage")]
    [InlineData("\"inputLimits\":{\"images\":{\"maxPerRequest\":1.5}}", "inputLimits.images.maxPerRequest")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":null}}", "inputLimits.images.resize")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":{\"maxWidth\":0}}}", "inputLimits.images.resize.maxWidth")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":{\"maxHeight\":\"1\"}}}", "inputLimits.images.resize.maxHeight")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":{\"maxBytes\":true}}}", "inputLimits.images.resize.maxBytes")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":{\"jpegQuality\":101}}}", "inputLimits.images.resize.jpegQuality")]
    [InlineData("\"inputLimits\":{\"images\":{\"resize\":{\"jpegQuality\":0}}}", "inputLimits.images.resize.jpegQuality")]
    [InlineData("\"promptCache\":null", "promptCache")]
    [InlineData("\"promptCache\":\"short\"", "promptCache")]
    [InlineData("\"promptCache\":{\"short\":0}", "promptCache.short")]
    [InlineData("\"promptCache\":{\"short\":null}", "promptCache.short")]
    [InlineData("\"promptCache\":{\"long\":-1}", "promptCache.long")]
    [InlineData("\"promptCache\":{\"long\":1e999}", "promptCache.long")]
    [InlineData("\"promptCache\":{\"long\":\"60\"}", "promptCache.long")]
    public void DirectoryExchangeRejectsInvalidMetadata(string metadata, string path)
    {
        using var document = JsonDocument.Parse("[{\"id\":\"model\",\"api\":\"custom\",\"baseUrl\":\"https://metadata.invalid\"," + metadata + "}]");
        var error = Assert.Throws<JsonException>(() => ModelConfigurationStore.ReadProviderModels("metadata", document.RootElement));
        Assert.Contains(path, error.Message);
    }

    /// <summary>【AI】【注册回滚】后续能力或未匹配覆盖校验失败时保留原目录、请求环境与可复制状态。</summary>
    /// <param name="type">模型能力。</param><param name="useOverride">是否注入未匹配覆盖。</param>
    [Theory]
    [InlineData("chat", false)]
    [InlineData("image", false)]
    [InlineData("classifier", false)]
    [InlineData("chat", true)]
    public void FailedRegistrationPreservesCatalogAndConfiguration(string type, bool useOverride)
    {
        var store = new ModelConfigurationStore([]);
        var catalog = new ModelCatalog(configurationStore: store);
        using var original = JsonDocument.Parse("""{"api":"custom","baseUrl":"https://original.invalid","options":{"env":{"MARKER":"original"}},"models":[{"id":"keep","promptCache":{"short":60}}]}""");
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["metadata"] = original.RootElement });
        var candidate = useOverride
            ? """{"modelOverrides":{"unknown":{"inputLimits":{"images":{"resize":{"jpegQuality":101}}}}}}"""
            : "{\"api\":\"custom\",\"baseUrl\":\"https://replacement.invalid\",\"models\":[{\"id\":\"new\"},{\"id\":\"bad\",\"api\":\"custom\",\"type\":\"" + type + "\",\"promptCache\":{\"long\":0}}]}";
        using var invalid = JsonDocument.Parse(candidate);
        Assert.Throws<JsonException>(() => catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["metadata"] = invalid.RootElement }));
        var retained = Assert.Single(catalog.GetAllModels("metadata"));
        Assert.Equal("keep", retained.Id);
        Assert.Equal("original", store.GetRequestEnvironment(retained)!["MARKER"]);
        Assert.Equal(60, retained.PromptCache!.Short);
        Assert.Equal("keep", Assert.Single(catalog.CreateSessionCopy().GetAllModels("metadata")).Id);
        Assert.Null(store.Error);
    }

    /// <summary>【AI】【配置恢复】无效文件整份拒绝，修复为含注释配置后重载恢复且清除诊断。</summary>
    [Fact]
    public void FileValidationIsAtomicAndRecoversWithCommentsAndNumericNotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "models.json");
            File.WriteAllText(file, """{"providers":{"metadata":{"models":[{"id":"ignored"}],"modelOverrides":{"unknown":{"promptCache":{"short":0}}}}}}""");
            var store = new ModelConfigurationStore([file]);
            var catalog = new ModelCatalog(configurationStore: store);
            Assert.Empty(catalog.GetAllModels("metadata"));
            Assert.NotEmpty(catalog.GetModels("openai"));
            Assert.Contains("promptCache.short", store.Error);
            File.WriteAllText(file, """
                { // 注释与整数的小数、指数表示均合法
                  "providers":{"metadata":{"api":"custom","baseUrl":"https://metadata.invalid","models":[{
                    "id":"restored", "unknown":true,
                    "inputLimits":{"maxRequestBytes":1e3,"images":{"maxPerMessage":1.0,"maxPerRequest":2,
                      "resize":{"maxWidth":1e2,"maxHeight":1,"maxBytes":1024,"jpegQuality":100.0}}},
                    "promptCache":{"short":0.5,"long":3600}
                  }]}}
                }
                """);
            catalog.ReloadConfiguration();
            var model = Assert.Single(catalog.GetAllModels("metadata"));
            Assert.Equal(1000, model.InputLimits!.MaxRequestBytes);
            Assert.Equal(1, model.InputLimits.Images!.MaxPerMessage);
            Assert.Equal(100, model.InputLimits.Images.Resize!.MaxWidth);
            Assert.Equal(100, model.InputLimits.Images.Resize.JpegQuality);
            Assert.Equal(0.5, model.PromptCache!.Short);
            Assert.Null(store.Error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
