// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【MCP】【问题汇总】启动合并配置错误及失败服务器，后续注册只提示新增故障，禁用服务器不提示。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpNotificationsGroupStartupProblemsAndDoNotReplayUnchangedFailures()
    {
        using var temp = TempDirectory.Create(); var registry = new CodingAgentMcpServerRegistry();
        foreach (var name in new[] { "broken", "login", "disabled" })
            registry.Register(name, new() { ["url"] = "https://fixture.test/" + name, ["enabled"] = name != "disabled" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new()
        {
            ConfigurationLoader = () => new([], null, ["fixture config error"], null),
            TransportFactory = (entry, _, _) => Task.FromException<ICodingAgentMcpTransport>(entry.Name == "login"
                ? new CodingAgentMcpHttpException(HttpStatusCode.Unauthorized, "authentication required")
                : new InvalidOperationException("fixture failure\nprivate details"))
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var notifications = service.ReadNotificationsAsync(deadline.Token).GetAsyncEnumerator();
        await service.ReloadAsync(deadline.Token); await service.WaitForServersAsync(deadline.Token);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal("error", notifications.Current.Level);
        Assert.Contains("config: fixture config error", notifications.Current.Message);
        Assert.Contains("broken: failed: fixture failure", notifications.Current.Message);
        Assert.Contains("login: needs sign-in", notifications.Current.Message);
        Assert.DoesNotContain("disabled", notifications.Current.Message);
        Assert.DoesNotContain("private details", notifications.Current.Message);
        await service.ReloadAsync(deadline.Token);
        registry.Register("late", new() { ["url"] = "https://fixture.test/late" }, "fixture");
        await service.WaitForServersAsync(deadline.Token);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Contains("late: failed: fixture failure", notifications.Current.Message);
        Assert.DoesNotContain("config:", notifications.Current.Message);
        Assert.DoesNotContain("broken:", notifications.Current.Message);
        await service.DisposeAsync();
        Assert.False(await notifications.MoveNextAsync());
    }

    /// <summary>【MCP】【慢启动通知】首次等待超时只提示一次，服务关闭取消待连接服务器并结束通知流，不发布过期失败。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpNotificationsReportSlowStartupOnceAndDrainOnClose()
    {
        using var temp = TempDirectory.Create(); var registry = new CodingAgentMcpServerRegistry();
        registry.Register("slow", new() { ["url"] = "https://fixture.test/", ["exposure"] = "direct" }, "fixture");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new()
        {
            StartupWait = TimeSpan.Zero, TransportFactory = async (_, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { stopped.TrySetResult(); }
                throw new InvalidOperationException("unreachable");
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var notifications = service.ReadNotificationsAsync(deadline.Token).GetAsyncEnumerator();
        await service.ReloadAsync(deadline.Token); await started.Task.WaitAsync(deadline.Token);
        await service.WaitForDirectServersAsync(deadline.Token);
        Assert.True(await notifications.MoveNextAsync());
        Assert.Equal("info", notifications.Current.Level); Assert.Contains("still connecting", notifications.Current.Message);
        await service.WaitForDirectServersAsync(deadline.Token);
        await service.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        Assert.True(stopped.Task.IsCompleted); Assert.False(await notifications.MoveNextAsync());
    }

    /// <summary>【MCP】【实际宿主通知】SDK 启动诊断通过终端和 RPC 通知接口展示，退出停止消费且不触发模型请求。</summary>
    /// <param name="rpc">是否验证 RPC 宿主。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpNotificationsReachInteractiveAndRpcHosts(bool rpc)
    {
        using var temp = TempDirectory.Create();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true,
            IncludeExtensions = false, EnableMcp = true, ModelCatalog = CreateModelCatalog(),
            McpOptions = new() { ConfigurationLoader = () => new([], null, ["fixture configuration error"], null) }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var visible = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new NoticeInput(finish.Task);
        using var output = new NoticeOutput(visible);
        var task = rpc
            ? new CodingAgentRpcHost(session.Runner, input, output).RunAsync(deadline.Token)
            : new CodingAgentHost(new InteractiveConsoleSession(new NoticeTerminal(visible, finish.Task)), session.Runner).RunAsync(deadline.Token);
        try
        {
            var text = await visible.Task.WaitAsync(deadline.Token);
            if (rpc)
            {
                var notice = ParseMcpSessionJson(text);
                Assert.Equal("notify", notice.GetProperty("method").GetString());
                Assert.Equal("error", notice.GetProperty("notifyType").GetString());
                text = notice.GetProperty("message").GetString()!;
            }
            Assert.Contains("MCP servers need attention", text);
            Assert.Contains("Run /mcp to fix.", text);
        }
        finally { finish.TrySetResult(); await task; }
        Assert.Empty(session.Messages.OfType<Tau.Ai.UserMessage>());
    }

    /// <summary>【MCP】【终端观察夹具】保持输入存活直到诊断展示，不依赖固定等待时间。</summary>
    /// <param name="visible">诊断出现信号。</param><param name="finish">结束输入信号。</param>
    private sealed class NoticeTerminal(TaskCompletionSource<string> visible, Task finish) : ITerminal
    {
        /// <summary>【MCP】【测试输入】收到结束信号后退出。</summary><param name="prompt">提示。</param><param name="color">颜色。</param><param name="cancellationToken">取消。</param><returns>退出命令。</returns>
        public async Task<string?> PromptAsync(string prompt, ConsoleColor? color = null, CancellationToken cancellationToken = default)
        { await finish.WaitAsync(cancellationToken); return "exit"; }
        /// <summary>【MCP】【测试输出】捕获诊断。</summary><param name="text">内容。</param><param name="color">颜色。</param>
        public void Write(string text, ConsoleColor? color = null) { if (text.Contains("fixture configuration error")) visible.TrySetResult(text); }
        /// <summary>【MCP】【测试行输出】复用内容捕获。</summary><param name="text">内容。</param><param name="color">颜色。</param>
        public void WriteLine(string? text = null, ConsoleColor? color = null) => Write(text ?? "", color);
    }

    /// <summary>【MCP】【RPC 输入夹具】等待通知验证完成后关闭输入。</summary><param name="finish">结束信号。</param>
    private sealed class NoticeInput(Task finish) : TextReader
    {
        /// <summary>【MCP】【RPC 测试读取】等待输入关闭。</summary><param name="cancellationToken">取消。</param><returns>输入结束标记。</returns>
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        { await finish.WaitAsync(cancellationToken); return null; }
    }

    /// <summary>【MCP】【RPC 输出夹具】按完整协议行捕获通知。</summary><param name="visible">通知信号。</param>
    private sealed class NoticeOutput(TaskCompletionSource<string> visible) : StringWriter
    {
        /// <summary>【MCP】【RPC 测试写入】保留输出并通知测试。</summary><param name="value">完整 JSON 行。</param><returns>完成任务。</returns>
        public override Task WriteLineAsync(string? value)
        {
            if (value?.Contains("fixture configuration error") == true) visible.TrySetResult(value);
            return base.WriteLineAsync(value);
        }
    }
}
