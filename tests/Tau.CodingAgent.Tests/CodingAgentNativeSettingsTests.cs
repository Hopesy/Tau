// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>原生压缩与重试设置逐字段覆盖，保存不丢失模型覆盖、提供方设置和未知字段。</summary>
    [Fact]
    public void NativeSettings_ResolveModelOverridesAndPreserveUnknownFields()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var path = Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "settings.json");
        File.WriteAllText(path, """
            {"compaction":{"enabled":false,"reserveTokens":1000,"keepRecentTokens":500,
              "modelOverrides":{"synthetic/test":{"reserveTokens":256,"keepRecentTokens":0},"synthetic/other":{"keepRecentTokens":20}}},
             "retry":{"enabled":true,"maxRetries":7,"baseDelayMs":25,"maxAgentDelayMs":0,"provider":{"timeoutMs":123,"maxRetries":2}},
             "branchSummary":{"reserveTokens":96,"skipPrompt":true},"futureFeature":{"key":"preserved"}}
            """);
        var store = new CodingAgentSettingsStore(path);
        var settings = store.Load();
        var model = new Model { Provider = "synthetic", Id = "test", Name = "Test", Api = "test" };
        Assert.Equal(new Tau.AgentCore.Harness.AgentCompactionSettings(false, 256, 0), settings.GetCompactionSettings(model));
        Assert.Equal(new Tau.AgentCore.Harness.AgentCompactionSettings(false, 1000, 20), settings.GetCompactionSettings(model with { Id = "other" }));
        Assert.Equal(500, settings.GetCompactionSettings(model with { Provider = "SYNTHETIC" }).KeepRecentTokens);
        Assert.Equal(96, settings.GetBranchSummaryReserveTokens());
        Assert.True(settings.GetBranchSummarySkipPrompt());
        var retry = CodingAgentRetryOptions.FromSettingsOrEnvironment(settings);
        Assert.Equal(new(7, 25, 0), retry);
        Assert.Equal(TimeSpan.Zero, retry.GetDelay(100));
        store.Save(settings with { DefaultModel = "changed", AutoCompactionEnabled = true, RetryMaxAttempts = 0 });
        var reloaded = store.Load();
        Assert.True(reloaded.GetCompactionSettings(model).Enabled);
        Assert.False(CodingAgentRetryOptions.FromSettingsOrEnvironment(reloaded).IsEnabled);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("preserved", json.RootElement.GetProperty("futureFeature").GetProperty("key").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("retry").GetProperty("maxRetries").GetInt32());
        Assert.Equal(123, json.RootElement.GetProperty("retry").GetProperty("provider").GetProperty("timeoutMs").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("compaction").GetProperty("modelOverrides").GetProperty("synthetic/test").GetProperty("keepRecentTokens").GetInt32());
    }

    /// <summary>非法预算或模型覆盖明确报错，禁止无声回退默认并继续压缩。</summary>
    /// <param name="compaction">原生压缩对象。</param>
    [Theory]
    [InlineData("{\"reserveTokens\":-1}")]
    [InlineData("{\"reserveTokens\":1.5}")]
    [InlineData("{\"keepRecentTokens\":\"100\"}")]
    [InlineData("{\"modelOverrides\":{\"synthetic/test\":false}}")]
    [InlineData("{\"modelOverrides\":{\"synthetic/test\":{\"keepRecentTokens\":-1}}}")]
    public void NativeSettings_RejectInvalidCompactionValues(string compaction)
    {
        using var document = JsonDocument.Parse(compaction);
        var settings = new CodingAgentSettingsSnapshot(null, null) { Compaction = document.RootElement.Clone() };
        var error = Assert.Throws<InvalidOperationException>(() => settings.GetCompactionSettings(new() { Provider = "synthetic", Id = "test", Name = "Test", Api = "test" }));
        Assert.Contains("Invalid compaction", error.Message);
    }

    /// <summary>持久树使用当前模型覆盖预算，提供方选项进入普通请求和摘要，文件更新在下次压缩读取。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NativeSettings_ReachProviderAndPersistentCompaction()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var store = new CodingAgentSettingsStore(Path.Combine(root, "settings.json"));
        File.WriteAllText(store.Path, """
            {"compaction":{"reserveTokens":96,"keepRecentTokens":500,"modelOverrides":{"synthetic/test":{"keepRecentTokens":0}}},
             "retry":{"enabled":false,"provider":{"timeoutMs":1234,"maxRetries":2,"maxRetryDelayMs":456}}}
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.ConfigureSessionSettings(store);
        fixture.Runtime.BindSession(runner, new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "history.jsonl"))));
        var requests = new List<StreamOptions>();
        provider.StreamFactory = options =>
        {
            requests.Add(options);
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new([new TextContent("answer")]) { StopReason = StopReason.EndTurn }));
            return stream;
        };
        await foreach (var ignored in runner.RunAsync("original")) { }
        var result = await runner.CompactAsync();
        Assert.NotNull(result.StoredEntryId);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, options =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(1234), options.Timeout);
            Assert.Equal(2, options.MaxRetries);
            Assert.Equal(TimeSpan.FromMilliseconds(456), options.MaxRetryDelay);
        });
        Assert.Equal(48, requests[1].MaxTokens);
        File.WriteAllText(store.Path, """{"compaction":{"keepRecentTokens":999,"reserveTokens":128}}""");
        Assert.Equal(999, runner.CompactionSettings.KeepRecentTokens);
        Assert.Equal(128, runner.CompactionSettings.ReserveTokens);
    }

    /// <summary>RPC 原生设置更新进入真实存储和运行器，显式重试空值清除嵌套策略。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NativeSettings_RpcUpdatesAndClearsNestedRetry()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var store = new CodingAgentSettingsStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "settings.json"));
        var (runner, _) = fixture.CreateRunner();
        runner.ConfigureSessionSettings(store);
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(runner, new StringReader("""
            {"id":"set","type":"update_settings","settings":{"compaction":{"reserveTokens":256,"keepRecentTokens":0},"retry":{"maxRetries":4,"baseDelayMs":3,"maxAgentDelayMs":9,"provider":{"maxRetries":2}},"branchSummary":{"skipPrompt":true}}}
            {"id":"clear","type":"update_settings","settings":{"retry":null}}
            """), output, settingsStore: store);
        Assert.Equal(0, await host.RunAsync());
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var first = Assert.Single(lines, line => line.RootElement.TryGetProperty("id", out var id) && id.GetString() == "set").RootElement;
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.Equal(9, first.GetProperty("data").GetProperty("retry").GetProperty("maxAgentDelayMs").GetInt32());
            Assert.Equal(4, first.GetProperty("data").GetProperty("retry").GetProperty("maxRetries").GetInt32());
        }
        finally { foreach (var line in lines) line.Dispose(); }
        var saved = store.Load();
        Assert.Null(saved.Retry);
        Assert.Null(saved.RetryMaxAttempts);
        Assert.Equal(0, runner.CompactionSettings.KeepRecentTokens);
        Assert.True(saved.GetBranchSummarySkipPrompt());
    }
}
