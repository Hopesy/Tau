// 作者：xxx
using System.Text.RegularExpressions;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【提示配置】可由扩展逐轮修改的完整提示构建输入。</summary>
public sealed record CodingAgentSystemPromptOptions
{
    public string? CustomPrompt { get; init; }
    public string? ForceSystemPrompt { get; init; }
    public List<string> SelectedTools { get; init; } = ["read", "bash", "edit", "write"];
    public Dictionary<string, string> ToolSnippets { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ToolGuidelines { get; init; } = new(StringComparer.Ordinal);
    public List<string> PromptGuidelines { get; init; } = [];
    public string AppendSystemPrompt { get; init; } = "";
    public Dictionary<string, string> Sections { get; init; } = new(StringComparer.Ordinal);
    public required string Cwd { get; init; }
    public List<CodingAgentPromptContextFile> ContextFiles { get; init; } = [];
    public List<CodingAgentSkill> Skills { get; init; } = [];
}

/// <summary>提示词中可按文件路径识别的项目指令。</summary>
/// <param name="Path">项目指令文件路径。</param>
/// <param name="Content">原始指令内容。</param>
public sealed record CodingAgentPromptContextFile(string Path, string Content);

/// <summary>【CodingAgent】【结构化提示】构建可独立更新的命名段落，并生成增量更新。</summary>
public static partial class CodingAgentSystemPrompt
{
    public const string DefaultPreamble = "You are Tau, an expert coding assistant operating inside a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";

    /// <summary>【CodingAgent】【提示配置】复制所有可变集合，保证扩展修改不会泄漏到下一轮基线。</summary>
    /// <param name="input">原始选项。</param>
    /// <returns>集合互相独立的完整选项。</returns>
    public static CodingAgentSystemPromptOptions Normalize(CodingAgentSystemPromptOptions input) => input with
    {
        SelectedTools = [.. input.SelectedTools],
        ToolSnippets = new(input.ToolSnippets, StringComparer.Ordinal),
        ToolGuidelines = input.ToolGuidelines.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value), StringComparer.Ordinal),
        PromptGuidelines = [.. input.PromptGuidelines],
        Sections = new(input.Sections, StringComparer.Ordinal),
        ContextFiles = [.. input.ContextFiles],
        Skills = [.. input.Skills]
    };

    /// <summary>【CodingAgent】【结构化提示】生成有序段落；自定义前言仍保留附加指令、项目文件及技能。</summary>
    /// <param name="input">提示构建选项；强制提示不影响结构化段落。</param>
    /// <returns>前言为普通文本，其余段落已经包裹同名 XML 标签。</returns>
    public static Dictionary<string, string> BuildSections(CodingAgentSystemPromptOptions input)
    {
        var options = Normalize(input);
        foreach (var name in options.Sections.Keys)
            if (!SectionName().IsMatch(name) || name == "preamble") throw new ArgumentException($"Invalid system prompt section name: {name}");

        // 1. 【CodingAgent】【结构化提示】默认前言、工具和规则按稳定顺序生成
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(options.CustomPrompt)) sections["preamble"] = options.CustomPrompt;
        else
        {
            sections["preamble"] = DefaultPreamble;
            var tools = options.SelectedTools.Where(name => options.ToolSnippets.TryGetValue(name, out var snippet) && !string.IsNullOrEmpty(snippet))
                .Select(name => $"- {name}: {options.ToolSnippets[name]}").ToArray();
            sections["tools"] = (tools.Length == 0 ? "(none)" : string.Join("\n", tools)) +
                "\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.";
            sections["rules"] = BuildRules(options);
            sections["docs"] = BuildDocumentation();
        }

        // 2. 【CodingAgent】【结构化提示】配置与项目资源独立成段，自定义段落可覆盖除前言外的默认段落
        if (options.AppendSystemPrompt.Length > 0) sections["addendum"] = options.AppendSystemPrompt;
        if (options.ContextFiles.Count > 0)
            sections["project_context"] = "Project-specific instructions and guidelines:\n\n" + string.Join("\n\n",
                options.ContextFiles.Select(file => $"<project_instructions path=\"{file.Path.Replace('\\', '/')}\">\n{file.Content}\n</project_instructions>"));
        var readTool = new[] { "read", "read_file", "bash", "shell" }.FirstOrDefault(options.SelectedTools.Contains);
        if (readTool is not null && options.Skills.Count > 0)
        {
            var skills = CodingAgentSkillStore.FormatForSystemPrompt(options.Skills, readTool).Trim();
            if (skills.Length > 0) sections["skills"] = skills;
        }
        sections["cwd"] = options.Cwd.Replace('\\', '/');
        foreach (var (name, content) in options.Sections)
            if (!string.IsNullOrEmpty(content)) sections[name] = content;
        return sections.ToDictionary(pair => pair.Key,
            pair => pair.Key == "preamble" ? pair.Value : $"<{pair.Key}>\n{pair.Value}\n</{pair.Key}>", StringComparer.Ordinal);
    }

    /// <summary>【CodingAgent】【提示状态】强制提示原样作为文本，否则由命名段落构成提示。</summary>
    /// <param name="options">完整提示选项。</param>
    /// <returns>不包含工具声明的系统消息。</returns>
    public static SystemMessage BuildState(CodingAgentSystemPromptOptions options) => options.ForceSystemPrompt is { } forced
        ? new SystemMessage(forced)
        : new SystemMessage("") { Sections = BuildSections(options).ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal) };

    /// <summary>【CodingAgent】【提示渲染】使用与会话重放相同的顺序渲染完整提示。</summary>
    /// <param name="options">构建选项。</param>
    /// <returns>模型和扩展看到的完整提示文本。</returns>
    public static string Build(CodingAgentSystemPromptOptions options) => Transcript.GetCurrentSystemPrompt([BuildState(options)]);

    /// <summary>【CodingAgent】【提示增量】只保留新增和变更段落，对删除段落输出 null。</summary>
    /// <param name="previous">历史重放得到的段落。</param>
    /// <param name="current">当前所需的完整段落。</param>
    /// <returns>没有变更时为空；否则为可持久化的段落更新。</returns>
    public static Dictionary<string, string?>? DiffSections(IReadOnlyDictionary<string, string?> previous, IReadOnlyDictionary<string, string> current)
    {
        var patch = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, text) in current)
            if (!previous.TryGetValue(name, out var old) || old != text) patch[name] = text;
        foreach (var name in previous.Keys)
            if (!current.ContainsKey(name)) patch[name] = null;
        return patch.Count == 0 ? null : patch;
    }

    /// <summary>【CodingAgent】【工具规则】按启用工具顺序收集规则，并在裁剪空白后去重。</summary>
    /// <param name="options">工具和附加规则选项。</param>
    /// <returns>逐行排列的规则列表。</returns>
    private static string BuildRules(CodingAgentSystemPromptOptions options)
    {
        var rules = new List<string>();
        var bash = options.SelectedTools.Contains("bash") || options.SelectedTools.Contains("shell");
        var powerShell = options.SelectedTools.Contains("powershell");
        if ((bash || powerShell) && !options.SelectedTools.Any(name => name is "grep" or "find" or "glob" or "ls"))
            rules.Add(bash && powerShell ? "Use bash or PowerShell for file operations like listing, searching, and finding files" :
                powerShell ? "Use PowerShell for file operations like listing, searching, and finding files" : "Use bash for file operations like ls, rg, find");
        foreach (var name in options.SelectedTools)
            if (options.ToolGuidelines.TryGetValue(name, out var toolRules)) rules.AddRange(toolRules);
        rules.AddRange(options.PromptGuidelines);
        rules.Add("Be concise in your responses");
        rules.Add("Show file paths clearly when working with files");
        return string.Join("\n", rules.Select(rule => rule.Trim()).Where(rule => rule.Length > 0).Distinct(StringComparer.Ordinal).Select(rule => "- " + rule));
    }

    /// <summary>【CodingAgent】【文档路径】提供相对于安装目录的文档入口，避免误用当前项目路径。</summary>
    /// <returns>文档段落的原始文本。</returns>
    internal static string BuildDocumentation()
    {
        var root = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        return $"Tau documentation (read only when the user asks about Tau itself, its SDK, extensions, themes, skills, or TUI):\n" +
            $"- Main documentation: {root}/README.md\n- Additional docs: {root}/docs\n- Examples: {root}/examples (extensions, custom tools, SDK)\n" +
            "- Resolve docs/... under Additional docs and examples/... under Examples, not the current working directory\n" +
            "- Read documentation files completely and follow links to related docs before implementing";
    }

    /// <summary>只允许可用作 XML 标签的受限段落名。</summary>
    /// <returns>段落名称验证表达式。</returns>
    [GeneratedRegex("^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionName();
}
