// 作者：xxx
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【宿主命令】创建使用本会话目录和动态状态的 RPC Bash 执行器。</summary>
    /// <returns>独立命令执行器。</returns>
    internal SystemCodingAgentShellRunner CreateShellRunner() => new(_workingDirectory, GetShellOptions, GetShellEnvironment);

    /// <summary>【CodingAgent】【命令绑定】将内置 Bash 和 PowerShell 绑定到本会话的动态设置与模型状态。</summary>
    private void BindShellTools()
    {
        foreach (var tool in _registeredTools.OfType<ShellTool>()) tool.BindSession(GetShellOptions, GetShellEnvironment);
    }

    /// <summary>【CodingAgent】【命令设置】实时读取 Bash 路径和命令前缀，配置重载后立即生效。</summary>
    /// <returns>本次命令执行的设置。</returns>
    internal CodingAgentShellToolOptions GetShellOptions()
    {
        var settings = _sessionSettings?.Load();
        return new() { ShellPath = settings?.ShellPath, CommandPrefix = settings?.ShellCommandPrefix };
    }

    /// <summary>【CodingAgent】【命令环境】读取当前会话和模型的非敏感标识，供本会话的子进程使用。</summary>
    /// <returns>需要覆盖的 PI_* 环境变量。</returns>
    internal IReadOnlyDictionary<string, string?> GetShellEnvironment() => new Dictionary<string, string?>
    {
        ["PI_SESSION_ID"] = SessionId, ["PI_SESSION_FILE"] = CurrentTreeSessionController?.Path,
        ["PI_PROVIDER"] = Model.Provider, ["PI_MODEL"] = Model.Id,
        ["PI_REASONING_LEVEL"] = CodingAgentThinkingLevels.Format(ThinkingLevel)
    };
}
