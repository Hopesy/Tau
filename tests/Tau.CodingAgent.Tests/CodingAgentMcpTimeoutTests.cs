// 作者：xxx
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentMcpProtocolTests
{
    /// <summary>【MCP】【长时限请求】超过系统定时器上限的请求及进度续期不抛异常，仍可被正常响应完成。</summary>
    /// <param name="maximum">是否使用 TimeSpan 最大值。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpTimeoutAcceptsLongDurationsAndProgressRenewal(bool maximum)
    {
        await using var transport = new ProtocolTransport(); await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport); var progress = 0;
        var pending = client.RequestAsync("long", onProgress: _ => progress++, timeout: maximum ? TimeSpan.MaxValue : TimeSpan.FromDays(60));
        var request = await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted);
        var id = request.GetProperty("id").GetRawText();
        transport.Emit("""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":IDe0,"progress":1}}""".Replace("ID", id, StringComparison.Ordinal));
        Assert.Equal(1, progress); Assert.False(pending.IsCompleted);
        transport.Emit("""{"jsonrpc":"2.0","id":ID,"result":{"ok":true}}""".Replace("ID", id, StringComparison.Ordinal));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("ok").GetBoolean());
    }

    /// <summary>【MCP】【超时发送回收】请求超时会取消尚未完成的发送，同时发送独立取消通知，后续请求不受影响。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpTimeoutCancelsBlockedSendAndKeepsClientUsable()
    {
        await using var transport = new ProtocolTransport(); await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnSend = async (request, token) =>
        {
            if (request.GetProperty("method").GetString() != "blocked") return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
        };
        var pending = client.RequestAsync("blocked", timeout: TimeSpan.FromMilliseconds(200));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var sent = await transport.Sent.Reader.ReadAsync();
        var cancel = await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("notifications/cancelled", cancel.GetProperty("method").GetString());
        Assert.Equal(sent.GetProperty("id").GetInt64(), cancel.GetProperty("params").GetProperty("requestId").GetInt64());
        transport.OnSend = (request, _) =>
        {
            transport.Emit(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt64(), ["result"] = new JsonObject { ["ok"] = true } }.ToJsonString());
            return Task.CompletedTask;
        };
        Assert.True((await client.RequestAsync("after")).GetProperty("ok").GetBoolean());
    }
}
