// 作者：xxx
using System.Threading.Channels;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【MCP】【宿主通知】供终端、RPC 或 SDK 呈现的服务器提示，级别使用 info 或 error。</summary>
/// <param name="Message">提示正文。</param><param name="Level">呈现级别。</param>
public sealed record CodingAgentMcpNotification(string Message, string Level = "info");

public sealed partial class CodingAgentMcpService
{
    private readonly Channel<CodingAgentMcpNotification> _notifications = Channel.CreateBounded<CodingAgentMcpNotification>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private readonly HashSet<Task> _notificationReports = [];
    private HashSet<string> _reportedConfigurationErrors = new(StringComparer.Ordinal);

    /// <summary>【MCP】【通知消费】依次读取启动及后续服务器提示，服务关闭结束流；每个服务由一个呈现宿主消费。</summary>
    /// <param name="token">只取消当前消费者，不关闭 MCP 服务。</param><returns>包含尚未消费通知的异步序列。</returns>
    public IAsyncEnumerable<CodingAgentMcpNotification> ReadNotificationsAsync(CancellationToken token = default) =>
        _notifications.Reader.ReadAllAsync(token);

    /// <summary>【MCP】【通知呈现】串行向宿主投递缓存及新通知，取消后不继续更新已经退出的界面。</summary>
    /// <param name="present">宿主异步呈现动作。</param><param name="token">宿主生命周期。</param><returns>呈现循环任务。</returns>
    public async Task PresentNotificationsAsync(Func<CodingAgentMcpNotification, CancellationToken, Task> present, CancellationToken token = default)
    {
        try
        {
            await foreach (var notice in ReadNotificationsAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                await present(notice, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>【MCP】【启动报告登记】后台等待本批新增服务器，宿主退出时同时排空报告任务。</summary>
    /// <param name="connections">本批连接。</param><param name="configurationErrors">本轮配置错误快照。</param>
    private void QueueProblemReport(IReadOnlyList<Connection> connections, IReadOnlyList<string> configurationErrors)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var report = ReportProblemsAfterAsync(connections, configurationErrors);
            _notificationReports.Add(report);
            _ = report.ContinueWith(completed => { lock (_gate) _notificationReports.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>【MCP】【启动问题汇总】每批只报告仍属于当前配置的失败服务器，未变化的配置错误不重复提示。</summary>
    /// <param name="connections">本批连接。</param><param name="configurationErrors">配置快照。</param><returns>报告任务。</returns>
    private async Task ReportProblemsAfterAsync(IReadOnlyList<Connection> connections, IReadOnlyList<string> configurationErrors)
    {
        await Task.Yield();
        await Task.WhenAll(connections.Select(connection => connection.Initialization)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            var lines = new List<string>();
            // 1. 【MCP】【配置诊断】过期配置快照不得重放旧错误，已修复后再次出现的错误可以重新提示
            if (_errors.SequenceEqual(configurationErrors))
            {
                lines.AddRange(configurationErrors.Where(error => !_reportedConfigurationErrors.Contains(error)).Select(error => "config: " + error));
                _reportedConfigurationErrors = configurationErrors.ToHashSet(StringComparer.Ordinal);
            }
            foreach (var connection in connections)
            {
                if (!connection.Enabled || !ReferenceEquals(_connections.GetValueOrDefault(connection.Entry.Name), connection)) continue;
                if (connection.State == "needs-auth") lines.Add(connection.Entry.Name + ": needs sign-in");
                else if (connection.State == "failed")
                    lines.Add(connection.Entry.Name + ": failed: " + (connection.Error ?? "unknown error").Split(['\r', '\n'])[0]);
            }
            if (lines.Count > 0)
                _notifications.Writer.TryWrite(new("MCP servers need attention:\n" + string.Join("\n", lines.Select(line => "  " + line)) + "\nRun /mcp to fix.", "error"));
        }
    }

    /// <summary>【MCP】【启动等待提示】首次请求不再阻塞时说明后台连接仍会继续。</summary>
    private void NotifyStillConnecting()
    {
        lock (_gate)
            if (!_disposed) _notifications.Writer.TryWrite(new("MCP servers are still connecting; their tools become available once connected."));
    }
}
