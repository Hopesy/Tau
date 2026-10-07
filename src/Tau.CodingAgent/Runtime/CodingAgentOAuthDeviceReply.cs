// 作者：xxx
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【同步登录选项】通过单次原子结果文件让 Node 同步读取宿主惰性设备 ID。</summary>
internal sealed class CodingAgentOAuthDeviceReply : IDisposable
{
    private readonly Func<string> _getDeviceId;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _started;

    /// <summary>【CodingAgent】【同步登录选项】仅分配随机结果路径，不调用身份委托或创建设置。</summary>
    /// <param name="getDeviceId">安装身份委托。</param>
    internal CodingAgentOAuthDeviceReply(Func<string> getDeviceId)
    {
        _getDeviceId = getDeviceId;
        ReplyPath = Path.Combine(Path.GetTempPath(), "tau-oauth-device-" + Guid.NewGuid().ToString("N") + ".json");
    }

    /// <summary>本次登录独占的结果文件路径。</summary>
    internal string ReplyPath { get; }

    /// <summary>【CodingAgent】【同步登录选项】先执行宿主身份读取，再原子公布结果；已结束的登录禁止迟到写入。</summary>
    /// <param name="token">宿主调用取消信号。</param>
    internal void Publish(CancellationToken token)
    {
        // 1. 【CodingAgent】【同步登录选项】已结束或重复的请求不得再次调用身份委托
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }
        JsonObject response;
        try
        {
            token.ThrowIfCancellationRequested();
            var value = _getDeviceId();
            token.ThrowIfCancellationRequested();
            response = new JsonObject { ["ok"] = true, ["value"] = value };
        }
        catch (Exception error) { response = new JsonObject { ["ok"] = false, ["error"] = error.Message }; }
        // 2. 【CodingAgent】【同步登录选项】身份委托在锁外运行，取消清理无需等待其结束
        lock (_gate)
        {
            if (_disposed) return;
            CodingAgentSettingsStore.WriteSettingsFile(ReplyPath, response);
        }
    }

    /// <summary>【CodingAgent】【同步登录选项】清理本次登录结果文件，并禁止之后发布。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            File.Delete(ReplyPath);
        }
    }
}
