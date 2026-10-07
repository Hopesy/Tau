// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【设置诊断】记录无法读取的设置层及错误，保留其他有效层。</summary>
public sealed record CodingAgentSettingsError(string Scope, string Path, string Message);

public sealed partial class CodingAgentSettingsStore
{
    private string? _projectPath;
    private readonly List<CodingAgentSettingsError> _loadErrors = [];
    public bool IsProjectTrusted { get; private set; } = true;
    public string? ProjectPath => _projectPath;
    public IReadOnlyList<CodingAgentSettingsError> LoadErrors => _loadErrors.ToArray();

    /// <summary>【CodingAgent】【设置分层】创建全局及项目设置管理器，交互修改保存到全局层。</summary>
    /// <param name="globalPath">全局设置路径。</param><param name="projectPath">项目设置路径。</param>
    /// <param name="projectTrusted">是否允许读取项目设置。</param><returns>分层设置存储。</returns>
    public static CodingAgentSettingsStore CreateLayered(string globalPath, string projectPath, bool projectTrusted = true)
    {
        var store = new CodingAgentSettingsStore(globalPath) { IsProjectTrusted = projectTrusted };
        var resolved = System.IO.Path.GetFullPath(projectPath);
        if (!resolved.Equals(store.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            store._projectPath = resolved;
        return store;
    }

    /// <summary>【CodingAgent】【项目信任】改变项目设置读取权限，后续加载立即使用新的信任状态。</summary>
    /// <param name="trusted">是否信任项目资源。</param>
    public void SetProjectTrusted(bool trusted) => IsProjectTrusted = trusted;

    /// <summary>【CodingAgent】【全局设置】读取不包含项目覆盖的全局快照。</summary>
    /// <returns>全局设置。</returns>
    public CodingAgentSettingsSnapshot LoadGlobal() => ReadSnapshot(JsonSerializer.SerializeToElement(ReadLayer(_path, "global")));

    /// <summary>【CodingAgent】【项目设置】读取可信项目的独立设置，未信任时返回空快照。</summary>
    /// <returns>项目设置。</returns>
    public CodingAgentSettingsSnapshot LoadProject() => ReadSnapshot(JsonSerializer.SerializeToElement(
        IsProjectTrusted && _projectPath is not null ? ReadLayer(_projectPath, "project") : new JsonObject()));

    /// <summary>【CodingAgent】【设置合并】独立读取各层，项目对象递归覆盖全局对象，数组替换并保留空集合。</summary>
    /// <returns>合并后的设置快照。</returns>
    private CodingAgentSettingsSnapshot LoadMergedSettings()
    {
        _loadErrors.Clear();
        var global = ReadLayer(_path, "global");
        var merged = IsProjectTrusted && _projectPath is not null ? MergeSettings(global, ReadLayer(_projectPath, "project")) : global;
        return ReadSnapshot(JsonSerializer.SerializeToElement(merged));
    }

    /// <summary>【CodingAgent】【设置层读取】解析并校验类型化字段，失败仅隔离当前层且记录诊断。</summary>
    /// <param name="path">文件路径。</param><param name="scope">global 或 project。</param><returns>独立设置对象。</returns>
    private JsonObject ReadLayer(string path, string scope)
    {
        try
        {
            var value = ReadSettingsFile(path);
            _ = ReadSnapshot(JsonSerializer.SerializeToElement(value));
            return value;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _loadErrors.Add(new(scope, path, error.Message));
            return new();
        }
    }

    /// <summary>【CodingAgent】【设置解析】读取 JSON 对象，不将损坏文件作为可覆盖的空设置。</summary>
    /// <param name="path">文件路径。</param><returns>文件内容或不存在时的空对象。</returns>
    internal static JsonObject ReadSettingsFile(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"Invalid settings file {path}: expected an object.");
    }

    /// <summary>【CodingAgent】【递归合并】合并对象，defaultTools 的纯增减列表追加到继承列表。</summary>
    /// <param name="basis">基础层。</param><param name="overrides">覆盖层。</param><param name="settingsRoot">是否为设置根对象。</param>
    /// <returns>不与输入共享节点的新对象。</returns>
    internal static JsonObject MergeSettings(JsonObject basis, JsonObject overrides, bool settingsRoot = true)
    {
        var result = (JsonObject)basis.DeepClone();
        foreach (var pair in overrides)
        {
            // 1. 【CodingAgent】【安装标识】安装身份仅属于全局设置，项目不能覆盖
            if (settingsRoot && pair.Key == "deviceId") continue;
            if (settingsRoot && pair.Key == "defaultTools" && result[pair.Key] is JsonArray inherited && pair.Value is JsonArray modifiers
                && modifiers.All(node => node is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWithAnyToolModifier()))
            {
                foreach (var node in modifiers) inherited.Add(node?.DeepClone());
            }
            else result[pair.Key] = result[pair.Key] is JsonObject current && pair.Value is JsonObject next
                ? MergeSettings(current, next, false) : pair.Value?.DeepClone();
        }
        return result;
    }

    /// <summary>【CodingAgent】【设置保存】只写入相对加载快照发生改变的字段，保留其他进程的修改及项目覆盖。</summary>
    /// <param name="snapshot">用户编辑后的快照。</param>
    private void SaveSettings(CodingAgentSettingsSnapshot snapshot)
    {
        using var fileLock = CodingAgentSettingsFileLock.Acquire(_path);
        var current = ReadSettingsFile(_path);
        // 1. 【CodingAgent】【损坏保护】当前目标文件无法投影时拒绝覆盖，避免抹掉可恢复的数据
        _ = ReadSnapshot(JsonSerializer.SerializeToElement(current));
        var next = ToSettingsObject(snapshot);
        if (snapshot.SourceDocument is { } source)
            ApplySettingsChanges(current, ToSettingsObject(ReadSnapshot(source)), next);
        else current = next;
        current["updatedAt"] = DateTimeOffset.UtcNow;
        WriteSettingsFile(_path, current);
    }

    /// <summary>【CodingAgent】【设置编码】使用源生成元数据序列化快照，更新时间不参与差量比较。</summary>
    /// <param name="snapshot">设置快照。</param><returns>规范化 JSON 对象。</returns>
    private static JsonObject ToSettingsObject(CodingAgentSettingsSnapshot snapshot)
    {
        var result = JsonSerializer.SerializeToNode(CreateDocument(snapshot), CodingAgentSettingsJsonContext.Default.CodingAgentSettingsDocument)!.AsObject();
        result.Remove("updatedAt");
        return result;
    }

    /// <summary>【CodingAgent】【差量写回】只替换或删除调用方修改的叶字段，未知嵌套字段和外部改动保持原样。</summary>
    /// <param name="target">最新磁盘对象。</param><param name="previous">修改前快照。</param><param name="next">修改后快照。</param>
    private static void ApplySettingsChanges(JsonObject target, JsonObject previous, JsonObject next)
    {
        foreach (var name in previous.Select(pair => pair.Key).Concat(next.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
        {
            var hadPrevious = previous.TryGetPropertyValue(name, out var before);
            var hasNext = next.TryGetPropertyValue(name, out var after);
            if (hadPrevious == hasNext && JsonNode.DeepEquals(before, after)) continue;
            if (!hasNext) target.Remove(name);
            else if (before is JsonObject beforeObject && after is JsonObject afterObject)
            {
                var changed = target[name] as JsonObject ?? new JsonObject();
                ApplySettingsChanges(changed, beforeObject, afterObject);
                if (target[name] is not JsonObject) target[name] = changed;
            }
            else target[name] = after?.DeepClone();
        }
    }

    /// <summary>【CodingAgent】【原子保存】在同一目录写入唯一临时文件后替换目标，失败时清理临时文件。</summary>
    /// <param name="path">目标文件。</param><param name="value">待保存对象。</param>
    internal static void WriteSettingsFile(string path, JsonObject value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new() { Indented = true })) value.WriteTo(writer);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>【CodingAgent】【配置互斥】跨实例串行化配置文件的读取修改写入事务。</summary>
internal static class CodingAgentSettingsFileLock
{
    /// <summary>【CodingAgent】【文件锁】用固定锁文件的独占句柄串行化保存，避免删除中的 Windows 文件拒绝并发打开。</summary>
    /// <param name="path">受保护文件的绝对路径。</param><returns>调用方必须释放的锁句柄。</returns>
    internal static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            // 1. 【CodingAgent】【文件锁】保留空锁文件，所有进程持续竞争同一个文件，关闭句柄即释放互斥
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
    }
}

/// <summary>【CodingAgent】【工具增减】共享默认工具列表的修饰符识别。</summary>
internal static class CodingAgentToolModifiers
{
    /// <summary>【CodingAgent】【修饰符】判断工具条目是否要求增减继承集合。</summary>
    /// <param name="value">配置条目。</param><returns>是否以加号或减号开头。</returns>
    internal static bool StartsWithAnyToolModifier(this string? value) => value?.StartsWith('+') == true || value?.StartsWith('-') == true;
}
