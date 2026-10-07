// 作者：xxx
namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【工具目录】将工具文件路径固定到所属会话目录</summary>
internal static class CodingAgentToolPaths
{
    /// <summary>在创建会话或工具时捕获绝对工作目录</summary>
    /// <param name="workingDirectory">指定目录；为空时使用当前进程目录</param>
    /// <returns>规范化的绝对目录</returns>
    public static string CaptureWorkingDirectory(string? workingDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory);

    /// <summary>按会话目录解析相对路径，绝对路径保持其目标位置</summary>
    /// <param name="path">工具参数路径；为空时使用会话目录</param>
    /// <param name="workingDirectory">已捕获的绝对工作目录</param>
    /// <returns>规范化的绝对路径</returns>
    public static string Resolve(string? path, string workingDirectory) =>
        Path.GetFullPath(string.IsNullOrEmpty(path) ? "." : path, workingDirectory);
}
