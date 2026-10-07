// 作者：xxx
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【RPC 会话配置】生成期间模型或思考等级变化更新会话历史，但不取消当前请求或修改默认设置文件。</summary>
    /// <param name="command">RPC 操作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("set_model")] [InlineData("cycle_model")]
    [InlineData("set_thinking_level")] [InlineData("cycle_thinking_level")]
    public async Task RpcModelMutation_ChangesActiveSessionWithoutRewritingDefaults(string command)
    {
        using var fixture = new Fixture("export default ()=>{};")
        {
            ConfigureModelCatalog = catalog =>
            {
                var original = catalog.GetModel("synthetic", "test") with { Reasoning = true };
                catalog.RegisterModel(original); catalog.RegisterModel(original with { Id = "other", Name = "Other" });
            }
        };
        var (runner, provider) = fixture.CreateRunner(); runner.ThinkingLevel = ThinkingLevel.Low;
        var root = Path.GetDirectoryName(fixture.Files[0])!; var settingsPath = Path.Combine(root, "settings.json");
        var settings = new CodingAgentSettingsStore(settingsPath);
        settings.Save(new("synthetic", "test", DefaultThinkingLevel: "high", EnabledModels: ["synthetic/test", "synthetic/other"]));
        var originalSettings = File.ReadAllText(settingsPath);
        var tree = new CodingAgentTreeSessionController(new(Path.Combine(root, "tree.jsonl"), root));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var modelCancelled = false;
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => { modelCancelled = true; stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })); });
            started.TrySetResult(); return stream;
        };
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output, settingsStore: settings, treeSessionController: tree).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"prompt","type":"prompt","message":"hold"}""");
        await started.Task.WaitAsync(deadline.Token);
        input.Lines.Writer.TryWrite(new JsonObject { ["id"] = "change", ["type"] = command, ["provider"] = "synthetic", ["modelId"] = "other", ["level"] = "high" }.ToJsonString());
        var response = await output.WaitAsync(line => line.GetProperty("type").GetString() == "response" && line.GetProperty("id").GetString() == "change", deadline.Token);
        Assert.True(response.GetProperty("success").GetBoolean()); Assert.True(runner.IsStreaming); Assert.False(modelCancelled);
        var modelChange = command is "set_model" or "cycle_model";
        Assert.Equal(modelChange ? "other" : "test", runner.Model.Id);
        var expectedThinking = modelChange ? ThinkingLevel.Low : command == "set_thinking_level" ? ThinkingLevel.High : ThinkingLevel.Medium;
        Assert.Equal(expectedThinking, runner.ThinkingLevel);
        Assert.Equal(runner.Model.Id, tree.LoadSnapshot().Model); Assert.Equal(CodingAgentThinkingLevels.Format(expectedThinking), tree.LoadSnapshot().ThinkingLevel);
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort"}""");
        await output.WaitAsync(line => line.GetProperty("type").GetString() == "agent_settled", deadline.Token);
        input.Lines.Writer.TryComplete(); await running.WaitAsync(deadline.Token); Assert.True(modelCancelled);
    }

    /// <summary>【CodingAgent】【可用思考等级】原生 RPC 按当前模型返回等级表，非推理模型循环时显式返回 null。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RpcThinkingLevels_NonReasoningModelReturnsOffAndNoCycle()
    {
        using var fixture = new Fixture("export default ()=>{};"); var (runner, _) = fixture.CreateRunner();
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new CodingAgentRpcHost(runner, input, output).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"levels","type":"get_available_thinking_levels"}""");
        var levels = await output.WaitAsync(line => line.GetProperty("type").GetString() == "response", deadline.Token);
        Assert.True(levels.GetProperty("success").GetBoolean()); Assert.Equal("off", Assert.Single(levels.GetProperty("data").GetProperty("levels").EnumerateArray()).GetString());
        input.Lines.Writer.TryWrite("""{"id":"cycle","type":"cycle_thinking_level"}""");
        var cycle = await output.WaitAsync(line => line.GetProperty("type").GetString() == "response", deadline.Token);
        Assert.True(cycle.GetProperty("success").GetBoolean()); Assert.Equal(System.Text.Json.JsonValueKind.Null, cycle.GetProperty("data").ValueKind);
        input.Lines.Writer.TryComplete(); await running.WaitAsync(deadline.Token);
    }
}
