// 作者：xxx
namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【PowerShell】使用 Windows 原生 PowerShell 和共享命令输出契约。</summary>
public sealed class PowerShellTool : ShellTool
{
    /// <summary>【CodingAgent】【PowerShell】创建绑定目录的 PowerShell 工具。</summary>
    /// <param name="workingDirectory">会话目录。</param><param name="options">执行后端和启动钩子。</param>
    public PowerShellTool(string? workingDirectory = null, CodingAgentShellToolOptions? options = null) : base(true, workingDirectory, options) { }
}
