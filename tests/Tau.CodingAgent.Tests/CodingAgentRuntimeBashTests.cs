// 作者：xxx
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【命令事件背压】慢订阅完成后才继续读取后续输出，事件按序发布并在最终结果之前排空。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExecuteBash_WaitsForOrderedSessionOutputSubscribers()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = fixture.CreateRunner(new InterruptProvider());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>(); var secondProduced = false;
        runner.BackgroundEvent += async (evt, _) =>
        {
            if (evt is not CodingAgentBashExecutionUpdateEvent update) return;
            Assert.Equal("request", update.Id); seen.Add(update.Delta);
            if (update.Delta == "first") { entered.TrySetResult(); await release.Task.WaitAsync(deadline.Token); }
        };
        var operations = new BashTestOperations(async (_, output, _) =>
        { await output("first"); secondProduced = true; await output("second"); return 0; });
        var running = runner.ExecuteBashAsync("command", options: new(Operations: operations, Id: "request"), cancellationToken: deadline.Token);
        await entered.Task.WaitAsync(deadline.Token); Assert.False(secondProduced); Assert.False(running.IsCompleted);
        release.TrySetResult(); var result = await running.WaitAsync(deadline.Token);
        Assert.Equal(["first", "second"], seen); Assert.Equal("firstsecond", result.Output); Assert.False(runner.IsBashRunning);
    }

    /// <summary>【CodingAgent】【直接命令上下文】使用会话目录、动态前缀和模型环境，原始命令及结果持久化，!! 结果排除模型上下文。</summary>
    /// <param name="exclude">是否排除模型上下文。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteBash_UsesSessionSettingsAndPersistsResult(bool exclude)
    {
        using var fixture = new StreamingSessionFixture();
        var runner = fixture.CreateRunner(new InterruptProvider()); var store = fixture.CreateStore("bash");
        runner.EnsureSessionContext(new(store), null); runner.ConfigureSessionSettings(fixture.Settings);
        fixture.Settings.Save(fixture.Settings.Load() with { ShellCommandPrefix = "prefix" });
        var updates = new List<string>();
        var operations = new BashTestOperations(async (context, output, _) =>
        {
            Assert.True(runner.IsBashRunning); Assert.Equal("prefix\ncommand", context.Command);
            Assert.Equal(store.GetSessionHeader().Cwd, context.Cwd); Assert.Equal(runner.SessionId, context.Environment["PI_SESSION_ID"]);
            Assert.Equal("model", context.Environment["PI_MODEL"]); await output("first\n"); await output("中文"); return 7;
        });
        var result = await runner.ExecuteBashAsync("command", new BashTestProgress(update => updates.Add(update.Text)), new(exclude, operations));
        Assert.Equal(7, result.ExitCode); Assert.Equal("first\n中文", result.Output); Assert.Equal(["first\n", "中文"], updates);
        Assert.False(runner.IsBashRunning); Assert.False(runner.HasPendingBashMessages);
        var saved = Assert.Single(store.LoadCurrentBranchSnapshot().Messages.OfType<AgentBashExecutionMessage>());
        Assert.Equal("command", saved.Command); Assert.Equal(result.Output, saved.Output); Assert.Equal(exclude, saved.ExcludeFromContext); Assert.NotNull(saved.Timestamp);
        var projected = AgentHarnessMessages.ConvertToLlm([saved]);
        Assert.Equal(exclude ? 0 : 1, projected.Count);
    }

    /// <summary>【CodingAgent】【延迟命令记录】生成中完成的命令先暂存，模型取消后在中止回答之后写入同一会话。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task RecordBashResult_DefersUntilModelBoundaryAndPersistsAfterAbort()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider); var store = fixture.CreateStore("deferred");
        runner.EnsureSessionContext(new(store), null);
        var model = DrainBashTestRunAsync(runner, deadline.Token); await provider.Started.Task.WaitAsync(deadline.Token);
        runner.RecordBashResult("direct", new("output", 0, false, false));
        Assert.True(runner.HasPendingBashMessages); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
        Assert.Empty(store.LoadCurrentBranchSnapshot().Messages.OfType<AgentBashExecutionMessage>());
        runner.Abort(); await model.WaitAsync(deadline.Token);
        Assert.False(runner.HasPendingBashMessages); Assert.True(provider.Cancelled);
        var messages = store.LoadCurrentBranchSnapshot().Messages.ToList();
        var assistant = messages.FindIndex(message => message is AssistantMessage { StopReason: StopReason.Aborted });
        var bash = messages.FindIndex(message => message is AgentBashExecutionMessage);
        Assert.True(assistant >= 0 && bash > assistant); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【多个命令取消】取消直接命令结束所有执行器并保存已收到的输出，不取消模型运行。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task AbortBash_CancelsAllCommandsAndKeepsModelRunning()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var model = DrainBashTestRunAsync(runner, deadline.Token); await provider.Started.Task.WaitAsync(deadline.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var operations = new BashTestOperations(async (_, output, token) =>
        { await output("partial"); if (Interlocked.Increment(ref count) == 2) started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; });
        var first = runner.ExecuteBashAsync("first", options: new(Operations: operations), cancellationToken: deadline.Token);
        var second = runner.ExecuteBashAsync("second", options: new(Operations: operations), cancellationToken: deadline.Token);
        await started.Task.WaitAsync(deadline.Token); runner.AbortBash();
        var results = await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        Assert.All(results, result => { Assert.True(result.Cancelled); Assert.Equal("partial", result.Output); Assert.Null(result.ExitCode); });
        Assert.False(runner.IsBashRunning); Assert.True(runner.IsStreaming); Assert.False(provider.Cancelled); Assert.True(runner.HasPendingBashMessages);
        runner.Abort(); await model.WaitAsync(deadline.Token);
        Assert.Equal(2, runner.Messages.OfType<AgentBashExecutionMessage>().Count()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【旧结果失效】会话重置取消尚在执行的命令，忽略不及时响应取消的旧后端结果。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ResetSession_DiscardsLateCommandResult()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = fixture.CreateRunner(new InterruptProvider());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken shellToken = default;
        var operations = new BashTestOperations(async (_, output, token) =>
        { shellToken = token; await output("old output"); started.TrySetResult(); await finish.Task.WaitAsync(deadline.Token); return 0; });
        var command = runner.ExecuteBashAsync("old command", options: new(Operations: operations)); await started.Task.WaitAsync(deadline.Token);
        runner.ResetSession(); Assert.True(shellToken.IsCancellationRequested); finish.TrySetResult();
        Assert.True((await command.WaitAsync(deadline.Token)).Cancelled); Assert.False(runner.IsBashRunning);
        Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.False(runner.HasPendingBashMessages);
    }

    /// <summary>【CodingAgent】【命令失败释放】后端异常释放运行状态，不生成伪造的成功记录，随后命令仍可执行。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ExecuteBash_FailureReleasesStateWithoutRecordingResult()
    {
        using var fixture = new StreamingSessionFixture(); var runner = fixture.CreateRunner(new InterruptProvider());
        var failing = new BashTestOperations((_, _, _) => throw new IOException("backend failure"));
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteBashAsync("bad", options: new(Operations: failing)));
        Assert.False(runner.IsBashRunning); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
        var succeeding = new BashTestOperations((_, _, _) => Task.FromResult<int?>(0));
        await runner.ExecuteBashAsync("next", options: new(Operations: succeeding));
        Assert.Equal("next", Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Command);
    }

    /// <summary>【CodingAgent】【预取消命令】取消已到达时不调用执行后端，也不创建命令记录。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ExecuteBash_PreCancellationSkipsBackend()
    {
        using var fixture = new StreamingSessionFixture(); var runner = fixture.CreateRunner(new InterruptProvider()); var calls = 0;
        var backend = new BashTestOperations((_, _, _) => { calls++; return Task.FromResult<int?>(0); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteBashAsync("never", options: new(Operations: backend), cancellationToken: new(true)));
        Assert.Equal(0, calls); Assert.False(runner.IsBashRunning); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
    }

    /// <summary>【CodingAgent】【模型消费夹具】消费可中断模型，保留原生运行器的取消终态。</summary>
    /// <param name="runner">模型运行器。</param><param name="token">测试超时。</param><returns>运行结束任务。</returns>
    private static async Task DrainBashTestRunAsync(RuntimeCodingAgentRunner runner, CancellationToken token)
    {
        try { await foreach (var _ in runner.RunAsync("model input", token)) { } }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    /// <summary>【CodingAgent】【直接命令夹具】通过委托控制执行、增量输出和取消。</summary>
    /// <param name="handler">后端处理器。</param>
    private sealed class BashTestOperations(Func<CodingAgentShellSpawnContext, Func<string, Task>, CancellationToken, Task<int?>> handler) : ICodingAgentShellOperations
    {
        /// <summary>【CodingAgent】【命令调用】转交本次命令上下文。</summary>
        /// <param name="context">命令及环境。</param><param name="onData">输出回调。</param><param name="token">取消信号。</param><returns>退出码。</returns>
        public Task<int?> ExecuteAsync(CodingAgentShellSpawnContext context, Func<string, Task> onData, CancellationToken token) => handler(context, onData, token);
    }

    /// <summary>【CodingAgent】【同步进度夹具】同步记录增量，避免测试依赖线程池回调时序。</summary><param name="handler">输出处理器。</param>
    private sealed class BashTestProgress(Action<CodingAgentShellEvent> handler) : IProgress<CodingAgentShellEvent>
    {
        /// <summary>【CodingAgent】【增量记录】立即调用处理器。</summary><param name="value">增量事件。</param>
        public void Report(CodingAgentShellEvent value) => handler(value);
    }
}
