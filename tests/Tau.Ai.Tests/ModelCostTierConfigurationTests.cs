// 作者：xxx
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed partial class ModelConfigurationValidationTests
{
    /// <summary>【AI】【阈值读取】指数、小数和大范围阈值在文件、会话复制及快照往返后仍能正确计费。</summary>
    /// <param name="json">阈值 JSON 数字。</param><param name="expected">期望阈值。</param>
    /// <param name="tierApplies">1001 个输入 token 是否命中该层。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("1e3", 1000d, true)]
    [InlineData("1000.0", 1000d, true)]
    [InlineData("1000.5", 1000.5d, true)]
    [InlineData("1001.0", 1001d, false)]
    [InlineData("1e100", 1e100, false)]
    [InlineData("-0.5", -0.5d, true)]
    public async Task FractionalTierThresholdsSurviveCostPipeline(string json, double expected, bool tierApplies)
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"models":[{"id":"model","cost":{"input":1,"output":1,"cacheRead":1,"cacheWrite":1,
              "tiers":[{"inputTokensAbove":
            """ + json + """
              ,"input":4,"output":4,"cacheRead":4,"cacheWrite":4}]}}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Null(fixture.Store.Error);
        var model = catalog.CreateSessionCopy().GetModel("local", "model");
        var store = new InMemoryModelsStore();
        await store.WriteAsync("local", new([model]));
        var restored = Assert.Single((await store.ReadAsync("local"))!.Models);
        foreach (var snapshot in new[] { model, restored })
        {
            Assert.Equal(expected, Assert.Single(snapshot.Cost!.Value.Tiers!).InputTokensAbove);
            var cost = ModelCatalog.CalculateCost(snapshot, new Usage(1001, 1));
            Assert.Equal(tierApplies ? 0.004004m : 0.001001m, cost.Input);
            Assert.Equal(tierApplies ? 0.000004m : 0.000001m, cost.Output);
        }
    }

    /// <summary>【AI】【阈值选择】乱序和同阈值层保留原生最高匹配、严格大于和首次命中规则。</summary>
    [Fact]
    public void TierSelectionUsesHighestStrictlyExceededThreshold()
    {
        var model = new Model
        {
            Id = "test", Name = "Test", Api = "test", Provider = "test",
            Cost = new(1, 1, 1, 1, [
                new(8, 8, 8, 8, 1000.5), new(4, 4, 4, 4, 500.5),
                new(99, 99, 99, 99, 1000.5), new(99, 99, 99, 99, -2)])
        };
        Assert.Equal(0.008008m, ModelCatalog.CalculateCost(model, new Usage(1001, 0)).Input);
        Assert.Equal(0.004m, ModelCatalog.CalculateCost(model, new Usage(1000, 0)).Input);
        Assert.Equal(0.0005m, ModelCatalog.CalculateCost(model, new Usage(500, 0)).Input);
    }

    /// <summary>【AI】【层扩展】单层中的 tiers 是未知扩展，不应被递归解释为价格结构。</summary>
    [Fact]
    public void UnknownNestedTierFieldIsIgnored()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"models":[{"id":"model","cost":{"input":1,"output":1,"cacheRead":1,"cacheWrite":1,
              "tiers":[{"inputTokensAbove":1000.5,"input":2,"output":2,"cacheRead":2,"cacheWrite":2,"tiers":"future-extension"}]}}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Null(fixture.Store.Error);
        Assert.Equal(1000.5, Assert.Single(catalog.GetModel("local", "model").Cost!.Value.Tiers!).InputTokensAbove);
    }

    /// <summary>【AI】【费率范围】超出 decimal 范围的文件费率必须报错，不能回退或下溢为零价格。</summary>
    /// <param name="rate">不受支持的费率数字。</param><param name="tier">是否放在分层费率内。</param>
    [Theory]
    [InlineData("1e100", false)]
    [InlineData("-1e100", false)]
    [InlineData("1e-29", false)]
    [InlineData("-1e-29", true)]
    [InlineData("1e100", true)]
    public void UnrepresentableRatesRejectWholeFile(string rate, bool tier)
    {
        var rates = """{"input":""" + rate + ""","output":1,"cacheRead":1,"cacheWrite":1""";
        var cost = tier
            ? """{"input":1,"output":1,"cacheRead":1,"cacheWrite":1,"tiers":[""" + rates + ""","inputTokensAbove":1000}]}"""
            : rates + "}";
        using var fixture = new ConfigurationFixture("""{"providers":{"local":{"models":[{"id":"model","cost":""" + cost + """}]}}}""");
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Empty(catalog.GetModels("local"));
        Assert.Contains(tier ? "cost.tiers.0.input" : "cost.input", fixture.Store.Error);
        Assert.Contains("decimal rate range", fixture.Store.Error);
    }
}
