// 作者：xxx
using System.Diagnostics;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentMcpProtocolTests
{
    /// <summary>【MCP】【客户端重复关闭】并发与关闭回调重入共享同一传输清理，清理失败也传给所有等待者。</summary>
    /// <param name="fail">底层清理是否抛出异常。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpClientCloseWaitsForSharedCleanupIncludingReentrantCall(bool fail)
    {
        var transport = new ProtocolTransport(); var client = new CodingAgentMcpClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = 0;
        transport.OnDispose = async () =>
        {
            Interlocked.Increment(ref closes); started.TrySetResult(); await release.Task;
            if (fail) throw new IOException("fixture close failure");
        };
        await client.ConnectAsync(transport);
        Task? reentrant = null; client.Closed += () => reentrant = client.DisposeAsync().AsTask();
        var first = client.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = client.DisposeAsync().AsTask();
        try
        {
            Assert.Same(first, second); Assert.Same(first, reentrant);
            Assert.False(first.IsCompleted); Assert.False(client.IsConnected);
            Assert.Equal(1, closes);
        }
        finally
        {
            release.TrySetResult();
            if (fail)
            {
                Assert.Equal("fixture close failure", (await Assert.ThrowsAsync<IOException>(() => first)).Message);
                await Assert.ThrowsAsync<IOException>(() => second);
            }
            else await Task.WhenAll(first, second);
        }
        Assert.Equal(1, closes);
    }
}

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【MCP】【管道退出竞争】真实子进程停止读取 stdin 后，关闭终止阻塞写入与排队写入，所有关闭调用等待同一个进程退出。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpStdioCloseDrainsBlockedWritesAndJoinsRepeatedClose()
    {
        using var temp = TempDirectory.Create(); var script = Path.Combine(temp.Path, "blocked-stdin.cjs");
        File.WriteAllText(script, """
            // 作者：xxx
            const rl=require('node:readline').createInterface({input:process.stdin});
            setInterval(()=>{},1000);
            rl.on('line',line=>{
              const m=JSON.parse(line); if(m.id===undefined)return;
              const result=m.method==='initialize'
                ?{protocolVersion:'2025-11-25',capabilities:{},serverInfo:{name:'fixture',version:'1'}}
                :{pid:process.pid};
              process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:m.id,result})+'\n');
              if(m.method==='pause')process.stdin.pause();
            });
            """);
        await using var transport = new CodingAgentMcpStdioTransport("node", [script]);
        await using var client = new CodingAgentMcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(transport, deadline.Token);
        using var process = Process.GetProcessById((await client.RequestAsync("pause", token: deadline.Token)).GetProperty("pid").GetInt32());
        var payload = ParseMcpSessionJson(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notification", ["params"] = new string('x', 1024 * 1024) }.ToJsonString());
        var writes = Enumerable.Range(0, 4).Select(_ => transport.SendAsync(payload, deadline.Token)).ToArray();
        Assert.Contains(writes, task => !task.IsCompleted);
        var first = transport.DisposeAsync().AsTask(); var second = transport.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        foreach (var write in writes)
        {
            try { await write.WaitAsync(deadline.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            Assert.True(write.IsCompleted);
        }
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(payload));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartAsync());
    }
}
