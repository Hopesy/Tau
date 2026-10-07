// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【资源来源】记录资源路径、来源、作用域、加载方式及可选根目录。</summary>
public sealed record CodingAgentSourceInfo(string Path, string Source, string Scope = "temporary", string Origin = "top-level", string? BaseDir = null)
{
    /// <summary>【CodingAgent】【本地资源】为直接加载的文件建立本地来源，保留原始用户或项目作用域。</summary>
    /// <param name="path">文件路径。</param><param name="scope">加载作用域。</param><param name="baseDir">资源基准目录。</param><returns>默认文件来源。</returns>
    internal static CodingAgentSourceInfo ForResource(string path, string scope, string? baseDir) =>
        new(path, "local", scope is "project" or "user" ? scope : "temporary", BaseDir: baseDir);

    /// <summary>【CodingAgent】【来源解析】优先使用最具体的包或扩展路径，并把来源路径更新为实际资源文件。</summary>
    /// <param name="fallback">资源默认来源。</param><param name="sources">外部来源表。</param><returns>完整资源来源。</returns>
    internal static CodingAgentSourceInfo ResolveResource(CodingAgentSourceInfo fallback, IReadOnlyDictionary<string, CodingAgentSourceInfo>? sources)
    {
        if (GetSyntheticPathSource(fallback.Path) is not null) return fallback;
        var match = sources?.Where(pair => IsWithin(fallback.Path, pair.Key)).OrderByDescending(pair => pair.Key.Length).FirstOrDefault();
        return match is { Value: { } source } ? source with { Path = fallback.Path } : fallback;
    }

    /// <summary>【CodingAgent】【来源合并】按来源表顺序合并元数据，后加入的同路径来源优先。</summary>
    /// <param name="sources">包、扩展和其他来源表。</param><returns>独立合并表。</returns>
    public static IReadOnlyDictionary<string, CodingAgentSourceInfo> Combine(params IReadOnlyDictionary<string, CodingAgentSourceInfo>[] sources)
    {
        var result = new Dictionary<string, CodingAgentSourceInfo>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var source in sources) foreach (var pair in source) result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>【CodingAgent】【合成来源】识别内置工具和内联资源的来源标识。</summary>
    /// <param name="path">资源标识或文件路径。</param><returns>合成来源，普通文件路径为空。</returns>
    public static string? GetSyntheticPathSource(string path)
    {
        if (path.StartsWith("builtin:", StringComparison.Ordinal)) return "builtin";
        if (!path.StartsWith('<') || !path.EndsWith('>')) return null;
        var source = path[1..^1].Split(':')[0];
        return source.Length == 0 ? "temporary" : source;
    }

    /// <summary>【CodingAgent】【路径范围】按目录边界判断资源归属，避免相同前缀的兄弟目录被错误归类。</summary>
    /// <param name="file">资源完整路径。</param><param name="root">来源文件或根目录。</param>
    /// <returns>是否位于该路径内。</returns>
    internal static bool IsWithin(string file, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = System.IO.Path.GetFullPath(file);
        var parent = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return full.Equals(parent, comparison) || full.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>【CodingAgent】【来源协议】写入上游字段命名的来源对象。</summary>
    /// <param name="writer">JSON 写入器。</param>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("path", Path); writer.WriteString("source", Source);
        writer.WriteString("scope", Scope); writer.WriteString("origin", Origin);
        if (BaseDir is not null) writer.WriteString("baseDir", BaseDir);
        writer.WriteEndObject();
    }

    /// <summary>【CodingAgent】【工具来源】优先采用定义中的来源，否则识别内置工具与 SDK 工具。</summary>
    /// <param name="tool">已注册工具。</param><returns>完整来源信息。</returns>
    internal static CodingAgentSourceInfo ForTool(IAgentTool tool) => (tool as ICodingAgentToolDefinition)?.SourceInfo
        ?? (tool.GetType().Namespace == "Tau.CodingAgent.Tools" ? new("builtin:" + tool.Name, "builtin") : new("<sdk:" + tool.Name + ">", "sdk"));
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    private IReadOnlyDictionary<string, CodingAgentSourceInfo> _extensionSources = new Dictionary<string, CodingAgentSourceInfo>();

    /// <summary>【CodingAgent】【扩展来源】替换本次资源发现的来源目录，供运行中注册工具复用。</summary>
    /// <param name="sources">以扩展完整路径索引的来源元数据。</param>
    internal void SetExtensionSources(IReadOnlyDictionary<string, CodingAgentSourceInfo> sources) => _extensionSources = sources;

    /// <summary>【CodingAgent】【扩展来源】读取所属扩展来源，独立运行时退回临时本地文件。</summary>
    /// <param name="filePath">扩展路径。</param><returns>来源信息。</returns>
    internal CodingAgentSourceInfo GetExtensionSource(string filePath) => _extensionSources.TryGetValue(System.IO.Path.GetFullPath(filePath), out var source)
        ? source : new(filePath, "local", BaseDir: System.IO.Path.GetDirectoryName(filePath));
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【扩展来源】匹配包元数据、自动发现目录或显式路径。</summary>
    /// <param name="file">扩展文件路径。</param><param name="scope">资源发现时的作用域。</param>
    /// <returns>扩展及其工具共享的来源信息。</returns>
    private CodingAgentSourceInfo ResolveExtensionSource(string file, string scope)
    {
        var path = System.IO.Path.GetFullPath(file);
        var metadata = _sourceInfosProvider?.Invoke();
        var match = metadata?.Where(pair => CodingAgentSourceInfo.IsWithin(path, pair.Key)).OrderByDescending(pair => pair.Key.Length).FirstOrDefault();
        if (match is { Value: { } source }) return source with { Path = path };
        if (scope is "user" or "project") return new(path, "auto", scope, BaseDir:
            scope == "user" ? _userExtensionsDirectory : System.IO.Path.Combine(_cwd, ".tau", "extensions"));
        return new(path, "cli");
    }
}
