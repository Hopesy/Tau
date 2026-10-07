// 作者：xxx
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Registry;
using Generator = Tau.ClassifierCatalogGenerator.Program;

namespace Tau.Ai.Tests;

/// <summary>【AI】【分类目录回归】离线验证目录过滤、价格、来源及失败时的文件保护。</summary>
public sealed class ClassifierCatalogGeneratorTests
{
    /// <summary>仓库快照可以完全离线重建，并与程序集内置条目逐项一致。</summary>
    [Fact]
    public void Generate_ReproducesCheckedInAndEmbeddedCatalogs()
    {
        var source = Source();
        var generated = Generator.Generate(source);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogFixtures", "generated-classifier-models.seed.json")), generated);
        Assert.Equal(generated, Generator.Generate(source));
        using var document = JsonDocument.Parse(generated);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), document.RootElement.GetProperty("sourceSha256").GetString());
        var entries = document.RootElement.GetProperty("models").EnumerateArray().ToArray();
        using var sourceDocument = JsonDocument.Parse(source);
        Assert.Equal(sourceDocument.RootElement.GetProperty("staticModels").GetArrayLength()
            + sourceDocument.RootElement.GetProperty("vercel").GetProperty("data").EnumerateArray().Count(item => item.GetProperty("type").GetString() == "evaluation"), entries.Length);
        Assert.Equal(entries.Length, GeneratedBuiltInClassifierModels.Models.Count);
        Assert.Equal(entries.Select(item => item.GetProperty("provider").GetString() + "/" + item.GetProperty("id").GetString()),
            GeneratedBuiltInClassifierModels.Models.Select(model => model.Provider + "/" + model.Id));
        foreach (var item in entries)
        {
            var model = Assert.Single(GeneratedBuiltInClassifierModels.Models, model => model.Provider == item.GetProperty("provider").GetString() && model.Id == item.GetProperty("id").GetString());
            Assert.Equal(item.GetProperty("api").GetString(), model.Api);
            Assert.Equal(ModelTypes.Classifier, model.Type);
            Assert.Equal(item.GetProperty("baseUrl").GetString(), model.BaseUrl);
            Assert.Equal(item.GetProperty("contextWindow").GetInt32(), model.ContextWindow);
            Assert.Equal(item.GetProperty("cost").GetProperty("inputPerMillion").GetDecimal(), model.Cost!.Value.InputPerMillion);
        }
    }

    /// <summary>公开来源只导入 evaluation 类型，默认上下文及每百万 token 价格保持上游规则。</summary>
    [Fact]
    public void Refresh_FiltersEvaluationAndConvertsPrices()
    {
        const string response = """
            {"data":[
              {"id":"chat","type":"language","pricing":{"input":"1","output":"1"}},
              {"id":"image","type":"image"},
              {"id":"z-eval","type":"evaluation","description":"discard","pricing":{"input":"0.000000042","output":0.000002}},
              {"id":"a-eval","name":"Named","type":"evaluation","context_window":8192,"pricing":{"input":"0","output":"0"}}
            ]}
            """;
        var timestamp = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        var updated = Generator.RefreshVercel(Source(), response, timestamp);
        using var source = JsonDocument.Parse(updated);
        Assert.Equal(2, source.RootElement.GetProperty("vercel").GetProperty("data").GetArrayLength());
        Assert.Equal(JsonNode.Parse(Source())!["staticModels"]!.ToJsonString(), JsonNode.Parse(updated)!["staticModels"]!.ToJsonString());
        Assert.DoesNotContain("discard", updated);
        var provenance = source.RootElement.GetProperty("sources").GetProperty("vercel");
        Assert.Equal(timestamp, provenance.GetProperty("retrievedAt").GetDateTimeOffset());
        Assert.Equal(Generator.VercelCatalogUrl, provenance.GetProperty("url").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(response))), provenance.GetProperty("responseSha256").GetString());
        using var result = JsonDocument.Parse(Generator.Generate(updated));
        var entries = result.RootElement.GetProperty("models").EnumerateArray().Where(item => item.GetProperty("provider").GetString() == "vercel-ai-gateway").ToArray();
        Assert.Equal(new[] { "a-eval", "z-eval" }, entries.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("z-eval", entries[1].GetProperty("name").GetString());
        Assert.Equal(4096, entries[1].GetProperty("contextWindow").GetInt32());
        Assert.Equal(0.042m, entries[1].GetProperty("cost").GetProperty("inputPerMillion").GetDecimal());
        Assert.Equal(2m, entries[1].GetProperty("cost").GetProperty("outputPerMillion").GetDecimal());
    }

    /// <summary>无有效目录或错误价格不能覆盖已有来源快照。</summary>
    /// <param name="response">损坏或空的远端目录。</param>
    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"chat\",\"type\":\"language\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"broken\",\"type\":\"evaluation\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"broken\",\"type\":\"evaluation\",\"pricing\":{\"input\":\"NaN\",\"output\":\"0\"}}]}")]
    public void Refresh_RejectsUnusableResponses(string response) =>
        Assert.ThrowsAny<JsonException>(() => Generator.RefreshVercel(Source(), response, DateTimeOffset.UtcNow));

    /// <summary>静态来源必须满足协议、能力、唯一性和有效数值约束。</summary>
    /// <param name="field">需要破坏的字段。</param>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("api")]
    [InlineData("contextWindow")]
    [InlineData("baseUrl")]
    [InlineData("cost")]
    [InlineData("inputModalities")]
    [InlineData("schemaVersion")]
    public void Generate_RejectsInvalidStaticModels(string field)
    {
        var source = JsonNode.Parse(Source())!.AsObject();
        var model = source["staticModels"]![0]!.AsObject();
        switch (field)
        {
            case "duplicate":
                var duplicate = model.DeepClone().AsObject();
                duplicate["id"] = "JEV-LATEST";
                source["staticModels"]!.AsArray().Add((JsonNode)duplicate);
                break;
            case "type": model["type"] = "chat"; break;
            case "api": model["api"] = "unknown"; break;
            case "contextWindow": model["contextWindow"] = 0; break;
            case "baseUrl": model["baseUrl"] = "relative"; break;
            case "cost": model["cost"]!["inputPerMillion"] = -1; break;
            case "inputModalities": model["inputModalities"] = new JsonArray("audio"); break;
            case "schemaVersion": source["schemaVersion"] = 2; break;
        }
        Assert.ThrowsAny<JsonException>(() => Generator.Generate(source.ToJsonString()));
    }

    /// <summary>生成命令的检查模式拒绝过期结果，输入损坏时保留既有输出。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Main_ChecksStalenessAndPreservesOutputOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-classifier-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "sources.json");
            var outputPath = Path.Combine(directory, "seed.json");
            var source = Source();
            await File.WriteAllTextAsync(sourcePath, source);
            Assert.Equal(1, await Generator.Main([sourcePath, outputPath, "--check"]));
            Assert.False(File.Exists(outputPath));
            Assert.Equal(0, await Generator.Main([sourcePath, outputPath]));
            var output = await File.ReadAllTextAsync(outputPath);
            Assert.Equal(Generator.Generate(source), output);
            Assert.Equal(0, await Generator.Main([sourcePath, outputPath, "--check"]));
            await File.WriteAllTextAsync(sourcePath, source + " ");
            Assert.Equal(1, await Generator.Main([sourcePath, outputPath, "--check"]));
            Assert.Equal(output, await File.ReadAllTextAsync(outputPath));
            await File.WriteAllTextAsync(sourcePath, "broken");
            Assert.Equal(1, await Generator.Main([sourcePath, outputPath]));
            Assert.Equal(output, await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, await Generator.Main([sourcePath, sourcePath]));
            Assert.Equal("broken", await File.ReadAllTextAsync(sourcePath));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>读取复制到测试输出中的固定来源，不访问远端服务。</summary>
    /// <returns>来源 JSON。</returns>
    private static string Source() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogFixtures", "classifier-models.sources.json"));
}
