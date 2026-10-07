// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>【CodingAgent】【优雅退出】扩展是否请求在当前操作完成后退出宿主。</summary>
    public bool IsShutdownRequested => _shutdownRequested.Task.IsCompleted;

    /// <summary>【CodingAgent】【退出通知】宿主可等待此任务停止接收新输入，不会同步重入扩展进程。</summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    /// <summary>【CodingAgent】【请求退出】幂等记录退出请求，当前生成和已提交历史由宿主按正常生命周期收尾。</summary>
    public void RequestShutdown() => _shutdownRequested.TrySetResult();
}
