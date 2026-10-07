// 作者：xxx
namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【提示文件】一轮资源发现的提示内容及来源。</summary>
public sealed record CodingAgentSystemPromptFilesSnapshot(string? SystemPrompt, string? AppendSystemPrompt,
    string? SystemPromptPath, IReadOnlyList<string> AppendSystemPromptPaths);

/// <summary>【CodingAgent】【提示发现】显式输入优先，否则按项目信任状态发现项目或全局提示文件。</summary>
public sealed class CodingAgentSystemPromptFiles
{
    private readonly string _cwd;
    private readonly string _agentDirectory;
    private readonly Func<bool> _isProjectTrusted;
    private readonly string? _systemPrompt;
    private readonly IReadOnlyList<string>? _appendSystemPrompt;

    /// <summary>【CodingAgent】【提示来源】绑定工作目录、全局目录和会话输入，重载时重新读取文件。</summary>
    /// <param name="cwd">项目目录。</param><param name="agentDirectory">全局 Agent 目录。</param>
    /// <param name="isProjectTrusted">当前信任状态。</param><param name="systemPrompt">显式内容或文件路径。</param>
    /// <param name="appendSystemPrompt">显式追加内容或路径，空集合禁用自动追加。</param>
    public CodingAgentSystemPromptFiles(string cwd, string agentDirectory, Func<bool> isProjectTrusted,
        string? systemPrompt = null, IReadOnlyList<string>? appendSystemPrompt = null)
    {
        _cwd = Path.GetFullPath(cwd);
        _agentDirectory = Path.GetFullPath(agentDirectory);
        _isProjectTrusted = isProjectTrusted;
        _systemPrompt = systemPrompt;
        _appendSystemPrompt = appendSystemPrompt;
    }

    /// <summary>【CodingAgent】【提示加载】项目文件覆盖全局同名文件，读取失败向调用方报告而不注入路径文字。</summary>
    /// <returns>独立提示快照。</returns>
    public CodingAgentSystemPromptFilesSnapshot Load()
    {
        var system = Resolve(_systemPrompt ?? Discover("SYSTEM.md"));
        var discoveredAppend = _appendSystemPrompt is null ? Discover("APPEND_SYSTEM.md") : null;
        var append = (_appendSystemPrompt ?? (discoveredAppend is null ? [] : [discoveredAppend])).Select(Resolve).ToArray();
        var texts = append.Where(item => item.Text is not null).Select(item => item.Text!).ToArray();
        return new(system.Text, texts.Length == 0 ? null : string.Join("\n\n", texts), system.Path,
            append.Where(item => item.Path is not null).Select(item => item.Path!).ToArray());
    }

    /// <summary>【CodingAgent】【自动发现】只读取受信任项目文件，不存在时回退全局目录。</summary>
    /// <param name="name">提示文件名。</param><returns>路径或空值。</returns>
    private string? Discover(string name)
    {
        var project = Path.Combine(_cwd, ".tau", name);
        if (_isProjectTrusted() && File.Exists(project)) return project;
        var global = Path.Combine(_agentDirectory, name);
        return File.Exists(global) ? global : null;
    }

    /// <summary>【CodingAgent】【提示输入】存在的文件按 UTF-8 读取，其他输入作为原始提示文本。</summary>
    /// <param name="input">显式文本或发现的文件路径。</param><returns>内容与可选文件来源。</returns>
    private (string? Text, string? Path) Resolve(string? input)
    {
        if (input is null) return (null, null);
        string path;
        try { path = Path.GetFullPath(input, _cwd); }
        catch (ArgumentException) { return (input, null); }
        return File.Exists(path) ? (File.ReadAllText(path), path) : (input, null);
    }
}
