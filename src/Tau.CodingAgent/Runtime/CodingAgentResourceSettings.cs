// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentPackageManager
{
    /// <summary>【CodingAgent】【资源设置】读取项目和全局的独立路径列表，各层路径按自身资源根解析。</summary>
    /// <param name="extensions">扩展路径。</param><param name="skills">技能路径。</param><param name="prompts">提示路径。</param>
    /// <param name="themes">主题路径。</param><param name="sources">资源来源表。</param>
    private void AddSettingsResources(List<string> extensions, List<string> skills, List<string> prompts, List<string> themes,
        Dictionary<string, CodingAgentSourceInfo> sources)
    {
        foreach (var (scope, settingsPath, root) in GetResourceSettingsLayers())
        {
            var settings = new CodingAgentSettingsStore(settingsPath).Load();
            foreach (var (kind, target) in new[] { ("extensions", extensions), ("skills", skills), ("prompts", prompts), ("themes", themes) })
            {
                var entries = ReadResourceEntries(settings, kind);
                var files = entries.Where(entry => !IsPatternEntry(entry)).SelectMany(entry => CollectResourceFilesFromPath(ResolvePath(entry, root), kind))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                foreach (var path in ApplyResourcePatterns(files, entries.Where(IsPatternEntry).ToArray(), root))
                {
                    target.Add(path);
                    sources.TryAdd(path, new(path, "local", scope, BaseDir: root));
                }
            }
        }
    }

    /// <summary>【CodingAgent】【自动加载筛选】一次读取配置，为整次资源发现提供独立过滤快照；显式路径不受自动加载排除影响。</summary>
    /// <param name="kind">extensions、skills、prompts 或 themes。</param><returns>按文件路径和加载作用域过滤的谓词。</returns>
    internal Func<string, string, bool> CreateAutomaticResourceFilter(string kind)
    {
        var layers = GetResourceSettingsLayers().ToDictionary(layer => layer.Scope,
            layer => (layer.Root, Patterns: GetOverridePatterns(ReadResourceEntries(new CodingAgentSettingsStore(layer.SettingsPath).Load(), kind))), StringComparer.Ordinal);
        return (path, scope) => !layers.TryGetValue(scope, out var layer) || ApplyResourcePatterns([path], layer.Patterns, layer.Root).Count != 0;
    }

    /// <summary>【CodingAgent】【资源设置层】项目先于全局，未信任项目不读取任何项目资源配置。</summary>
    /// <returns>作用域、设置文件和相对路径根目录。</returns>
    private IEnumerable<(string Scope, string SettingsPath, string Root)> GetResourceSettingsLayers()
    {
        if (IsProjectTrusted && !PathsEqual(UserSettingsPath, ProjectSettingsPath))
            yield return ("project", ProjectSettingsPath, Path.Combine(_cwd, ProjectConfigDirectoryName));
        yield return ("user", UserSettingsPath, UserInstallDirectory);
    }

    /// <summary>【CodingAgent】【资源字段】从保留的原生设置中提取非空字符串条目。</summary>
    /// <param name="settings">独立作用域设置。</param><param name="kind">资源类型。</param><returns>路径和过滤规则列表。</returns>
    private static IReadOnlyList<string> ReadResourceEntries(CodingAgentSettingsSnapshot settings, string kind) =>
        settings.AdditionalSettings?.TryGetValue(kind, out var value) == true && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() : [];
}

public sealed partial class CodingAgentExtensionCommandStore
{
    internal Func<Func<string, string, bool>>? AutoResourceFilterProvider { get; set; }
    internal Action? RefreshResourcePaths { get; set; }
}
