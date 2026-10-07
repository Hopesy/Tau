// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【信任记录】指定目录的已保存信任决定。</summary>
public sealed record CodingAgentProjectTrustEntry(string Path, bool Decision);
/// <summary>【CodingAgent】【信任更新】空决定表示删除本目录的覆盖记录。</summary>
public sealed record CodingAgentProjectTrustUpdate(string Path, bool? Decision);
/// <summary>【CodingAgent】【信任选项】用户选择及其对应的持久化修改。</summary>
public sealed record CodingAgentProjectTrustOption(string Label, bool Trusted, IReadOnlyList<CodingAgentProjectTrustUpdate> Updates, string? SavedPath = null);
/// <summary>【CodingAgent】【扩展决定】yes / no 为明确决定，undecided 继续后续规则。</summary>
public sealed record CodingAgentProjectTrustDecision(string Trusted, bool Remember = false);
/// <summary>【CodingAgent】【信任请求】项目启动信任决策的参数，交互和扩展接入均可由宿主提供。</summary>
public sealed record CodingAgentProjectTrustRequest(string Cwd, CodingAgentProjectTrustStore Store)
{
    public bool? Override { get; init; }
    public string DefaultPolicy { get; init; } = "ask";
    public Func<CancellationToken, Task<CodingAgentProjectTrustDecision?>>? ExtensionDecision { get; init; }
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? Select { get; init; }
}

/// <summary>【CodingAgent】【信任存储】持久化规范目录的信任覆盖，最近祖先记录优先。</summary>
public sealed class CodingAgentProjectTrustStore
{
    private readonly string _path;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>【CodingAgent】【信任存储】选择独立 Agent 目录中的 trust.json。</summary>
    /// <param name="agentDirectory">全局 Agent 配置目录。</param>
    public CodingAgentProjectTrustStore(string agentDirectory) => _path = System.IO.Path.Combine(NormalizePath(agentDirectory), "trust.json");

    /// <summary>【CodingAgent】【信任查询】读取本目录或最近祖先的有效决定。</summary>
    /// <param name="cwd">项目目录。</param><returns>信任、拒绝或尚未决定。</returns>
    public bool? Get(string cwd) => GetEntry(cwd)?.Decision;

    /// <summary>【CodingAgent】【信任来源】返回生效的最近目录记录。</summary>
    /// <param name="cwd">项目目录。</param><returns>有效记录，没有记录时为空。</returns>
    public CodingAgentProjectTrustEntry? GetEntry(string cwd)
    {
        using var fileLock = CodingAgentSettingsFileLock.Acquire(_path);
        var entries = ReadEntries();
        for (var current = NormalizePath(cwd); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if (entries.TryGetValue(current, out var decision) && decision is { } value) return new(current, value);
        return null;
    }

    /// <summary>【CodingAgent】【信任保存】设置或删除单个目录的信任记录。</summary>
    /// <param name="cwd">项目目录。</param><param name="decision">新决定，空值删除本目录覆盖。</param>
    public void Set(string cwd, bool? decision) => SetMany([new(cwd, decision)]);

    /// <summary>【CodingAgent】【信任事务】一次修改多个目录，锁内读取最新文件并按路径排序保存。</summary>
    /// <param name="updates">依序执行的目录更新。</param>
    public void SetMany(IEnumerable<CodingAgentProjectTrustUpdate> updates)
    {
        using var fileLock = CodingAgentSettingsFileLock.Acquire(_path);
        var entries = ReadEntries();
        foreach (var update in updates)
        {
            var path = NormalizePath(update.Path);
            if (update.Decision is null) entries.Remove(path);
            else entries[path] = update.Decision;
        }
        var document = new JsonObject();
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)) document[pair.Key] = pair.Value;
        CodingAgentSettingsStore.WriteSettingsFile(_path, document);
    }

    /// <summary>【CodingAgent】【信任校验】仅接受布尔值或 null，损坏记录不能静默降级或被覆盖。</summary>
    /// <returns>文件中的全部记录。</returns>
    private Dictionary<string, bool?> ReadEntries()
    {
        var result = new Dictionary<string, bool?>(PathComparer);
        if (!File.Exists(_path)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
            foreach (var pair in document.RootElement.EnumerateObject())
                result[pair.Name] = pair.Value.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
                    _ => throw new InvalidDataException($"Value for {pair.Name} must be true, false, or null.")
                };
            return result;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        { throw new InvalidDataException($"Failed to read trust store {_path}: {error.Message}", error); }
    }

    /// <summary>【CodingAgent】【规范目录】展开用户目录并解析真实目录及祖先符号链接，不存在时保留绝对路径。</summary>
    /// <param name="path">输入目录。</param><returns>用于持久化和比较的规范目录。</returns>
    internal static string NormalizePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") path = home;
        else if (path.StartsWith("~/") || path.StartsWith("~\\")) path = System.IO.Path.Combine(home, path[2..]);
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(full)) return full;
        try
        {
            var root = System.IO.Path.GetPathRoot(full)!;
            var current = root;
            foreach (var part in full[root.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(System.IO.Path.Combine(current, part));
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }
            return System.IO.Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return full; }
    }
}

/// <summary>【CodingAgent】【项目信任】检测需要授权的项目资源并按主线顺序解析决定。</summary>
public static class CodingAgentProjectTrust
{
    private static readonly string[] ProjectResources = ["coding-agent-settings.json", "settings.json", "mcp.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md"];

    /// <summary>【CodingAgent】【资源检查】检查项目配置与祖先共享技能，用户主目录的 .agents/skills 始终作为全局资源。</summary>
    /// <param name="cwd">项目目录。</param><param name="homeDirectory">测试或宿主覆盖的用户目录。</param><returns>是否存在需要信任的项目资源。</returns>
    public static bool HasTrustRequiringResources(string cwd, string? homeDirectory = null)
    {
        var home = CodingAgentProjectTrustStore.NormalizePath(homeDirectory ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var current = CodingAgentProjectTrustStore.NormalizePath(cwd);
        if (ProjectResources.Any(name => Exists(System.IO.Path.Combine(current, ".tau", name)))) return true;
        for (; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if (!current.Equals(home, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Exists(System.IO.Path.Combine(current, ".agents", "skills"))) return true;
        return false;
    }

    /// <summary>【CodingAgent】【资源存在】统一检查文件和目录。</summary><param name="path">待检查路径。</param><returns>是否存在。</returns>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>【CodingAgent】【信任选项】生成当前目录、父目录和仅本次会话的信任选项。</summary>
    /// <param name="cwd">项目目录。</param><param name="includeSessionOnly">是否提供临时选择。</param><returns>固定顺序的选项。</returns>
    public static IReadOnlyList<CodingAgentProjectTrustOption> GetOptions(string cwd, bool includeSessionOnly = false)
    {
        var path = CodingAgentProjectTrustStore.NormalizePath(cwd);
        var options = new List<CodingAgentProjectTrustOption> { new("Trust", true, [new(path, true)], path) };
        if (System.IO.Path.GetDirectoryName(path) is { } parent)
            options.Add(new($"Trust parent folder ({parent})", true, [new(parent, true), new(path, null)], parent));
        if (includeSessionOnly) options.Add(new("Trust (this session only)", true, []));
        options.Add(new("Do not trust", false, [new(path, false)], path));
        if (includeSessionOnly) options.Add(new("Do not trust (this session only)", false, []));
        return options;
    }

    /// <summary>【CodingAgent】【决策顺序】依次使用显式参数、资源检查、全局扩展、持久化记录、默认策略和交互选择。</summary>
    /// <param name="request">信任请求。</param><param name="token">取消信号。</param><returns>本次是否加载项目资源。</returns>
    public static async Task<bool> ResolveAsync(CodingAgentProjectTrustRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.Override is { } explicitDecision) return explicitDecision;
        if (!HasTrustRequiringResources(request.Cwd)) return true;
        // 1. 【CodingAgent】【扩展信任】只由启动前加载的全局扩展参与，项目扩展尚未运行
        var result = request.ExtensionDecision is null ? null : await request.ExtensionDecision(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result?.Trusted is "yes" or "no")
        {
            var trusted = result.Trusted == "yes";
            if (result.Remember) request.Store.Set(request.Cwd, trusted);
            return trusted;
        }
        if (request.Store.Get(request.Cwd) is { } remembered) return remembered;
        if (request.DefaultPolicy is "always") return true;
        if (request.DefaultPolicy is "never" || request.Select is null) return false;
        // 2. 【CodingAgent】【交互信任】取消选择按不信任处理，仅持久化选项携带的更新
        var options = GetOptions(request.Cwd, includeSessionOnly: true);
        var selected = await request.Select($"Trust project folder?\n{request.Cwd}\n\nThis allows Tau to load project settings and resources, install project packages, and execute project extensions.",
            options.Select(option => option.Label).ToArray(), token).ConfigureAwait(false);
        var choice = options.FirstOrDefault(option => option.Label == selected);
        if (choice is null) return false;
        if (choice.Updates.Count > 0) request.Store.SetMany(choice.Updates);
        return choice.Trusted;
    }
}
