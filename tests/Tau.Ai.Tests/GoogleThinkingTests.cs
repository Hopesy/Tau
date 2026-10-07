// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Providers.Google;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed partial class GoogleHistoryReplayTests
{
    /// <summary>【Google】【思考映射】直接和配置入口先钳制再映射，关闭行为由模型可用等级决定。</summary>
    /// <param name="api">协议。</param><param name="configured">是否通过配置入口。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", false)]
    [InlineData("google-generative-language", true)]
    [InlineData("google-vertex", false)]
    [InlineData("google-vertex", true)]
    [InlineData("google-gemini-cli", false)]
    [InlineData("google-gemini-cli", true)]
    public async Task ThinkingMappingIsConsistentAcrossEntries(string api, bool configured)
    {
        var cases = new (Dictionary<string, string?>? Map, ThinkingLevel? Requested, string? Level, bool Included)[]
        {
            (null, ThinkingLevel.High, "HIGH", true),
            (null, ThinkingLevel.ExtraHigh, "HIGH", true),
            (null, null, null, false),
            (null, ThinkingLevel.Off, null, false),
            (new() { ["high"] = "medium", ["medium"] = "low" }, ThinkingLevel.High, "MEDIUM", true),
            (new() { ["low"] = null, ["medium"] = "HIGH" }, ThinkingLevel.Low, "HIGH", true),
            (new() { ["max"] = "medium" }, ThinkingLevel.Max, "MEDIUM", true),
            (new() { ["off"] = null, ["minimal"] = null, ["low"] = "low" }, null, "LOW", false),
            (new() { ["off"] = null, ["minimal"] = null, ["low"] = "low" }, ThinkingLevel.Off, "LOW", true)
        };
        using var fixture = new ThinkingConfiguration(api, configured);
        foreach (var item in cases)
        foreach (var id in new[] { "gemini-3.8-pro", "gemini-3-flash", "gemini-flash-latest", "gemini-flash-lite-latest", "gemma-4-31b", "gemma4-31b" })
        {
            var model = Model(api) with { Id = id, Reasoning = true, ThinkingLevelMap = item.Map };
            var body = await SendAsync(model, new() { Messages = [new UserMessage("think")] }, new SimpleStreamOptions { Reasoning = item.Requested }, configuration: fixture.Store);
            var thinking = body.GetProperty("generationConfig").GetProperty("thinkingConfig");
            Assert.Equal(item.Included, thinking.TryGetProperty("includeThoughts", out _));
            Assert.Equal(item.Level is not null, thinking.TryGetProperty("thinkingLevel", out var level));
            if (item.Level is not null)
            {
                Assert.Equal(item.Level, level.GetString());
                Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
            }
            else Assert.Equal(0, thinking.GetProperty("thinkingBudget").GetInt32());
        }
    }

    /// <summary>【Google】【预算映射】预算格式模型先映射等级，再选择各协议默认值和自定义预算。</summary>
    /// <param name="api">协议。</param><param name="configured">是否通过配置入口。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", false)]
    [InlineData("google-generative-language", true)]
    [InlineData("google-vertex", false)]
    [InlineData("google-vertex", true)]
    [InlineData("google-gemini-cli", false)]
    [InlineData("google-gemini-cli", true)]
    public async Task ThinkingBudgetsUseResolvedLevels(string api, bool configured)
    {
        using var fixture = new ThinkingConfiguration(api, configured);
        foreach (var (id, minimum, maximum) in new[] { ("gemini-2.5-pro", 128, 32768), ("gemini-2.5-flash", 128, 24576),
            ("gemini-2.5-flash-lite", api == "google-vertex" ? 128 : 512, 24576), ("gemini-30-pro", -1, -1), ("GEMINI-2.5-PRO", -1, -1) })
        foreach (var level in new[] { ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High })
        {
            var model = Model(api) with { Id = id, Reasoning = true };
            var body = await SendAsync(model, new() { Messages = [] }, new SimpleStreamOptions { Reasoning = level }, configuration: fixture.Store);
            var thinking = body.GetProperty("generationConfig").GetProperty("thinkingConfig");
            Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
            var expected = level switch { ThinkingLevel.Minimal => minimum, ThinkingLevel.High => maximum, ThinkingLevel.Low => minimum == -1 ? -1 : 2048, _ => minimum == -1 ? -1 : 8192 };
            if (api == "google-gemini-cli") expected = level switch { ThinkingLevel.Minimal => 1024, ThinkingLevel.High => 16384, ThinkingLevel.Low => 2048, _ => 8192 };
            Assert.Equal(expected, thinking.GetProperty("thinkingBudget").GetInt32());
        }
        var mappedModel = Model(api) with { Id = "gemini-2.5-pro", Reasoning = true, ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "low" } };
        var mapped = await SendAsync(mappedModel, new() { Messages = [] }, new SimpleStreamOptions
        { Reasoning = ThinkingLevel.High, ThinkingBudgets = new() { Low = 1234, High = 9999 } }, configuration: fixture.Store);
        Assert.Equal(1234, mapped.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
    }

    /// <summary>【Google】【原生优先】原生等级不经过模型映射，等级优先预算，未提供配置时保持缺省。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task NativeThinkingIsNotMappedAgain(string api)
    {
        var model = Model(api) with { Reasoning = true, ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "low" } };
        foreach (var level in new string?[] { null, "HIGH", "THINKING_LEVEL_UNSPECIFIED" })
        {
            var body = await SendAsync(model, new() { Messages = [] }, NativeThinkingOptions(api, new() { Enabled = true, Level = level, BudgetTokens = 42 }));
            var thinking = body.GetProperty("generationConfig").GetProperty("thinkingConfig");
            Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
            if (level is null) Assert.Equal(42, thinking.GetProperty("thinkingBudget").GetInt32());
            else
            {
                Assert.Equal(level, thinking.GetProperty("thinkingLevel").GetString());
                Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
            }
        }
        var absent = await SendAsync(model, new() { Messages = [] });
        Assert.False(absent.TryGetProperty("generationConfig", out _));
        var unsupported = await SendAsync(model with { Reasoning = false }, new() { Messages = [] }, new SimpleStreamOptions { Reasoning = ThinkingLevel.High });
        Assert.False(unsupported.TryGetProperty("generationConfig", out _));
    }

    /// <summary>【Google】【非法映射】错误的模型映射不得静默回退为动态预算或错误原生等级。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task InvalidThinkingMappingsFailBeforeSending(string api)
    {
        foreach (var mapped in new[] { "", "low ", "max" })
        {
            var model = Model(api) with { Reasoning = true, ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = mapped } };
            await SendAsync(model, new() { Messages = [] }, new SimpleStreamOptions { Reasoning = ThinkingLevel.High }, expectedError: "Unsupported Google thinking level mapping");
        }
    }

    /// <summary>【Google】【原生夹具】为各传输创建相同的思考配置。</summary>
    /// <param name="api">协议。</param><param name="thinking">思考选项。</param><returns>请求选项。</returns>
    private static StreamOptions NativeThinkingOptions(string api, GoogleThinkingOptions thinking) => api switch
    {
        "google-vertex" => new GoogleVertexOptions { Thinking = thinking }, "google-gemini-cli" => new GoogleGeminiCliOptions { Thinking = thinking },
        _ => new GoogleOptions { Thinking = thinking }
    };

    /// <summary>【Google】【配置夹具】以真实配置强制统一入口生成原生选项。</summary>
    private sealed class ThinkingConfiguration : IDisposable
    {
        private readonly string? _path;
        public ModelConfigurationStore? Store { get; }

        /// <summary>【Google】【配置创建】仅写入合成提供方配置，工具选择触发原生选项构建。</summary>
        /// <param name="api">协议及提供方。</param><param name="enabled">是否启用配置路径。</param>
        public ThinkingConfiguration(string api, bool enabled)
        {
            if (!enabled) return;
            _path = Path.Combine(Path.GetTempPath(), $"tau-google-thinking-{Guid.NewGuid():N}.json");
            var json = new JsonObject { ["providers"] = new JsonObject { [api] = new JsonObject { ["options"] = new JsonObject { ["toolChoice"] = "auto" } } } };
            File.WriteAllText(_path, json.ToJsonString());
            Store = new([_path]);
        }

        /// <summary>【Google】【配置清理】删除本夹具创建的单个临时配置文件。</summary>
        public void Dispose() { if (_path is not null) File.Delete(_path); }
    }
}
