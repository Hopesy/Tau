// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionCommandStore
{
    private CodingAgentExtensionResources _discoveredResources = new([], [], []);
    public event Action<CodingAgentExtensionResources>? ResourcesChanged;

    /// <summary>【CodingAgent】【发现重置】卸载模块后清除其发现资源，通知宿主切换到新模块的静态资源。</summary>
    internal void ResetDiscoveredResources()
    {
        _discoveredResources = new([], [], []);
        ResourcesChanged?.Invoke(LoadResources());
    }

    /// <summary>【CodingAgent】【扩展资源】session_start 完成后收集所有处理器返回的技能、提示和主题路径。</summary>
    /// <param name="reason">startup 或 reload。</param><param name="token">取消信号。</param>
    /// <returns>独立的处理器和路径解析错误。</returns>
    private async Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> DiscoverResourcesAsync(string reason, CancellationToken token)
    {
        var generation = _javaScriptRuntime.ResetGeneration;
        var modules = LoadStatus().EventHandlers.Where(handler => handler.EventType == "resources_discover").DistinctBy(handler => handler.FilePath).ToArray();
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var skills = new List<string>(); var prompts = new List<string>(); var themes = new List<string>();
        var sources = new Dictionary<string, CodingAgentSourceInfo>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var payload = JsonSerializer.SerializeToElement(new { type = "resources_discover", cwd = _cwd, reason });
        foreach (var module in modules)
        {
            token.ThrowIfCancellationRequested();
            if (generation != _javaScriptRuntime.ResetGeneration) return errors;
            var result = await _javaScriptRuntime.EmitEventAsync(module.FilePath, payload, token, generation).ConfigureAwait(false);
            if (!result.Success) errors.Add(new(module.FilePath, module.Scope, module.Runtime, "resources_discover", result.Error ?? "Resource discovery failed."));
            foreach (var error in result.HandlerErrors) errors.Add(new(module.FilePath, module.Scope, module.Runtime, "resources_discover", error));
            if (result.TransformedEvent is not { } evt) continue;
            try
            {
                AddDiscoveredPaths(evt, "skillPaths", module.FilePath, skills, sources);
                AddDiscoveredPaths(evt, "promptPaths", module.FilePath, prompts, sources);
                AddDiscoveredPaths(evt, "themePaths", module.FilePath, themes, sources);
            }
            catch (Exception error) when (error is ArgumentException or IOException)
            { errors.Add(new(module.FilePath, module.Scope, module.Runtime, "resources_discover", error.Message)); }
        }
        if (generation != _javaScriptRuntime.ResetGeneration) return errors;
        _discoveredResources = new(skills.Distinct(sources.Comparer).ToArray(), prompts.Distinct(sources.Comparer).ToArray(), themes.Distinct(sources.Comparer).ToArray())
            { SourceInfos = sources };
        ResourcesChanged?.Invoke(LoadResources());
        return errors;
    }

    /// <summary>【CodingAgent】【资源来源】相对路径以会话目录解析，保留提供扩展及其文件目录作为来源。</summary>
    /// <param name="evt">发现结果。</param><param name="property">路径集合字段。</param><param name="extensionPath">提供扩展。</param>
    /// <param name="paths">路径累加器。</param><param name="sources">按路径索引的来源。</param>
    private void AddDiscoveredPaths(JsonElement evt, string property, string extensionPath, List<string> paths,
        Dictionary<string, CodingAgentSourceInfo> sources)
    {
        if (!evt.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())) continue;
            var path = entry.GetString()!;
            if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            path = Path.GetFullPath(path, _cwd);
            paths.Add(path);
            sources.TryAdd(path, new(path, "extension:" + Path.GetFileNameWithoutExtension(extensionPath), BaseDir: Path.GetDirectoryName(extensionPath)));
        }
    }
}
