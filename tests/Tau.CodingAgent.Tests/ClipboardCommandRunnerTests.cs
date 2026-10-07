// 作者：xxx
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class ClipboardCommandRunnerTests
{
    /// <summary>【CodingAgent】【空输出成功】读取命令收到 stdin EOF，空输出是成功结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Reader_ClosesStdinAndAcceptsEmptyOutput()
    {
        var result = await RunNode("process.stdin.resume();process.stdin.on('end',()=>process.exit(0));");
        Assert.True(result.Ok); Assert.Empty(result.Stdout);
    }

    /// <summary>【CodingAgent】【原始字节】读取结果保留 UTF-8 字节和末尾换行。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Reader_ReturnsExactBytes()
    {
        var result = await RunNode("process.stdout.write('中文\\n');");
        Assert.True(result.Ok); Assert.Equal(Encoding.UTF8.GetBytes("中文\n"), result.Stdout);
    }

    /// <summary>【CodingAgent】【退出失败】非零退出或程序不存在返回失败。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task FailedCommands_ReturnFailure()
    {
        Assert.False((await RunNode("process.exit(7)")).Ok);
        Assert.False((await new SystemCodingAgentClipboardCommandRunner().RunAsync("tau-missing-" + Guid.NewGuid().ToString("N"), [], null, 1000, 1024, default)).Ok);
    }

    /// <summary>【CodingAgent】【输出边界】达到上限仍成功，超限时中止持续运行的生产者。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task OutputLimit_StopsProducerBeforeItExits()
    {
        Assert.True((await RunNode("process.stdout.write('1234')", limit: 4)).Ok);
        var result = await RunNode("process.stdout.write('12345');setInterval(()=>{},1000);", limit: 4);
        Assert.False(result.Ok); Assert.Equal("output exceeded max buffer", result.Stderr); Assert.Empty(result.Stdout);
    }

    /// <summary>【CodingAgent】【写入管道超时】子进程不读取大量输入时也受统一超时限制。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Timeout_IncludesBlockedStdinWrite()
    {
        var result = await RunNode("setInterval(()=>{},1000);", new byte[8 * 1024 * 1024], timeout: 400);
        Assert.False(result.Ok); Assert.Equal("command timed out", result.Stderr);
    }

    /// <summary>【CodingAgent】【提前关闭输入】写入器可以在读取全部输入前成功退出，不把 broken pipe 当成失败。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Writer_EarlySuccessfulExitToleratesBrokenPipe()
    {
        var result = await RunNode("process.exit(0);", new byte[8 * 1024 * 1024]);
        Assert.True(result.Ok); Assert.Empty(result.Stdout);
    }

    /// <summary>【CodingAgent】【双向管道】大量 stdout/stderr 与 stdin 同时传输不会互相阻塞，写入模式丢弃输出。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Writer_DrainsOutputWhileWritingInput()
    {
        var result = await RunNode("process.stdout.write(Buffer.alloc(131072));process.stderr.write(Buffer.alloc(131072));process.stdin.resume();process.stdin.on('end',()=>process.exit(0));", new byte[131072], limit: 1);
        Assert.True(result.Ok); Assert.Empty(result.Stdout);
    }

    /// <summary>【CodingAgent】【后台剪贴板所有权】写入工具派生的后台进程继续持有输出管道时，不等待其退出。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Writer_DoesNotWaitForDaemonOutputPipes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-clipboard-daemon-{Guid.NewGuid():N}.txt");
        var script = "const child=require('child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:['ignore',1,2],windowsHide:true});require('fs').writeFileSync(" +
            JsonValue.Create(path)!.ToJsonString() + ",String(child.pid));child.unref();process.exit(0);";
        try
        {
            var result = await RunNode(script, []).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Ok); Assert.Empty(result.Stdout);
        }
        finally
        {
            if (File.Exists(path))
            {
                var pid = int.Parse(await File.ReadAllTextAsync(path));
                try
                {
                    using var daemon = Process.GetProcessById(pid);
                    if (!daemon.HasExited) daemon.Kill(entireProcessTree: true);
                    await daemon.WaitForExitAsync();
                }
                catch (ArgumentException) { }
                File.Delete(path);
            }
        }
    }

    /// <summary>【CodingAgent】【调用者取消】取消传播给调用者并终止本次进程，不转成普通超时结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CallerCancellation_PropagatesAndTerminatesProcess()
    {
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = Path.Combine(Path.GetTempPath(), $"tau-clipboard-pid-{Guid.NewGuid():N}.txt");
        var script = "require('fs').writeFileSync(" + JsonValue.Create(path)!.ToJsonString() + ",String(process.pid));setInterval(()=>{},1000);";
        var pending = RunNode(script, token: caller.Token);
        try
        {
            while (!File.Exists(path)) await Task.Delay(10, deadline.Token);
            var pid = int.Parse(await File.ReadAllTextAsync(path, deadline.Token));
            await caller.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException) { }
        }
        finally { caller.Cancel(); File.Delete(path); }
    }

    /// <summary>【CodingAgent】【预取消】调用前取消不得启动命令或创建其输出文件。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task PrecancelledCall_DoesNotStartProcess()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunNode("process.exit(0)", token: caller.Token));
    }

    /// <summary>【CodingAgent】【命令夹具】通过真实 Node 子进程验证管道和进程生命周期，不访问系统剪贴板。</summary>
    /// <param name="script">受控脚本。</param><param name="input">输入字节。</param><param name="limit">读取输出上限。</param>
    /// <param name="timeout">超时毫秒。</param><param name="token">取消信号。</param><returns>命令结果。</returns>
    private static Task<CodingAgentClipboardCommandResult> RunNode(string script, byte[]? input = null,
        int limit = 1024, int timeout = 5000, CancellationToken token = default) =>
        new SystemCodingAgentClipboardCommandRunner().RunAsync("node", ["-e", script], input, timeout, limit, token);
}
