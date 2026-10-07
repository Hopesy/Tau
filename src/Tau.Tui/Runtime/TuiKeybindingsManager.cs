// 作者：xxx
using System.Text.Json;

namespace Tau.Tui.Runtime;

/// <summary>【Tui】【快捷键定义】动作的默认按键及可选说明。</summary>
/// <param name="DefaultKeys">默认按键 ID。</param><param name="Description">动作说明。</param>
public sealed record TuiKeybindingDefinition(IReadOnlyList<string> DefaultKeys, string? Description = null);

/// <summary>【Tui】【快捷键冲突】用户显式为多个动作设置的同一个按键。</summary>
/// <param name="Key">原始按键 ID。</param><param name="Keybindings">按配置顺序排列的动作标识。</param>
public sealed record TuiKeybindingConflict(string Key, IReadOnlyList<string> Keybindings);

/// <summary>【Tui】【快捷键管理】按动作查询独立绑定；保留上下文间共享按键，并报告用户配置冲突。</summary>
public class TuiKeybindingsManager
{
    private readonly IReadOnlyDictionary<string, TuiKeybindingDefinition> _definitions;
    private Snapshot _snapshot;

    /// <summary>【Tui】【快捷键快照】保存同一配置版本的原始设置、解析结果和冲突。</summary>
    /// <param name="User">用户覆盖。</param><param name="Resolved">有效绑定。</param><param name="Conflicts">显式冲突。</param>
    private sealed record Snapshot(IReadOnlyDictionary<string, IReadOnlyList<string>> User,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Resolved, IReadOnlyList<TuiKeybindingConflict> Conflicts);

    /// <summary>【Tui】【快捷键初始化】复制定义与覆盖配置，避免调用方后续修改影响当前状态。</summary>
    /// <param name="definitions">已注册动作，缺省为完整终端动作。</param><param name="userBindings">可选用户覆盖，空数组禁用动作。</param>
    public TuiKeybindingsManager(IReadOnlyDictionary<string, TuiKeybindingDefinition>? definitions = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? userBindings = null)
    {
        _definitions = (definitions ?? TuiKeybindingDefinitions.All).ToDictionary(pair => pair.Key,
            pair => new TuiKeybindingDefinition(pair.Value.DefaultKeys.ToArray(), pair.Value.Description), StringComparer.Ordinal);
        _snapshot = BuildSnapshot(userBindings ?? new Dictionary<string, IReadOnlyList<string>>());
    }

    /// <summary>【Tui】【终端匹配】使用现有终端协议解码器按动作匹配原始输入。</summary>
    /// <param name="data">VT、Kitty 或普通字符输入。</param><param name="keybinding">动作标识。</param><returns>是否匹配任一有效按键。</returns>
    public bool Matches(string data, string keybinding) => Volatile.Read(ref _snapshot).Resolved.TryGetValue(keybinding, out var keys)
        && keys.Any(key => TuiKeyDecoder.MatchesKey(data, key));

    /// <summary>【Tui】【控制台匹配】匹配控制台按键及修饰键，无法表示的 Super 键不降级为普通键。</summary>
    /// <param name="data">控制台按键。</param><param name="keybinding">动作标识。</param><returns>是否匹配。</returns>
    public bool Matches(ConsoleKeyInfo data, string keybinding) => Volatile.Read(ref _snapshot).Resolved.TryGetValue(keybinding, out var keys)
        && keys.Any(key => MatchesConsoleKey(data, key));

    /// <summary>【Tui】【按键查询】返回独立数组，未知动作返回空数组。</summary>
    /// <param name="keybinding">动作标识。</param><returns>去重后的有效按键，保留原顺序。</returns>
    public IReadOnlyList<string> GetKeys(string keybinding) => Volatile.Read(ref _snapshot).Resolved.GetValueOrDefault(keybinding)?.ToArray() ?? [];

    /// <summary>【Tui】【定义查询】返回动作定义副本。</summary>
    /// <param name="keybinding">动作标识。</param><returns>定义副本，未知动作为空。</returns>
    public TuiKeybindingDefinition? GetDefinition(string keybinding) => _definitions.TryGetValue(keybinding, out var value)
        ? new(value.DefaultKeys.ToArray(), value.Description) : null;

    /// <summary>【Tui】【冲突查询】只返回用户覆盖之间的冲突，默认动作共享按键不算冲突。</summary>
    /// <returns>独立的冲突列表。</returns>
    public IReadOnlyList<TuiKeybindingConflict> GetConflicts() => Volatile.Read(ref _snapshot).Conflicts
        .Select(conflict => new TuiKeybindingConflict(conflict.Key, conflict.Keybindings.ToArray())).ToArray();

    /// <summary>【Tui】【覆盖查询】读取当前用户配置，未知动作也保留供外层配置管理使用。</summary>
    /// <returns>独立的配置字典。</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetUserBindings() => Clone(Volatile.Read(ref _snapshot).User);

    /// <summary>【Tui】【有效配置】读取所有已注册动作的最终绑定，禁用动作保留空数组。</summary>
    /// <returns>独立的有效绑定字典。</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetResolvedBindings() => Clone(Volatile.Read(ref _snapshot).Resolved);

    /// <summary>【Tui】【配置更新】一次替换完整用户配置，读者不会观察到部分更新。</summary>
    /// <param name="bindings">新的用户覆盖。</param>
    public void SetUserBindings(IReadOnlyDictionary<string, IReadOnlyList<string>> bindings) => Volatile.Write(ref _snapshot, BuildSnapshot(bindings));

    /// <summary>【Tui】【配置解析】读取上游动作名到字符串或字符串数组的配置，忽略无效字段类型。</summary>
    /// <param name="json">可带 UTF-8 BOM 的 JSON。</param><returns>动作覆盖，非对象根节点返回空字典。</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\ufeff'));
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.String) result[field.Name] = [field.Value.GetString()!];
            else if (field.Value.ValueKind == JsonValueKind.Array && field.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String))
                result[field.Name] = field.Value.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        return result;
    }

    /// <summary>【Tui】【快照构建】先收集显式按键声明，再按定义顺序应用用户覆盖或默认值。</summary>
    /// <param name="bindings">新的用户配置。</param><returns>可原子发布的完整状态。</returns>
    private Snapshot BuildSnapshot(IReadOnlyDictionary<string, IReadOnlyList<string>> bindings)
    {
        var user = Clone(bindings);
        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // 1. 【Tui】【显式冲突】仅已注册动作参与冲突检测，同一动作的重复按键只计一次
        foreach (var (id, keys) in user)
            if (_definitions.ContainsKey(id))
                foreach (var key in keys.Distinct(StringComparer.Ordinal))
                {
                    if (!claims.TryGetValue(key, out var actions)) claims[key] = actions = [];
                    actions.Add(id);
                }
        var resolved = _definitions.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)(user.GetValueOrDefault(pair.Key) ?? pair.Value.DefaultKeys).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return new(user, resolved, claims.Where(pair => pair.Value.Count > 1).Select(pair => new TuiKeybindingConflict(pair.Key, pair.Value.ToArray())).ToArray());
    }

    /// <summary>【Tui】【配置复制】复制字典及每个按键数组。</summary>
    /// <param name="bindings">待复制配置。</param><returns>独立字典。</returns>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Clone(IReadOnlyDictionary<string, IReadOnlyList<string>> bindings) =>
        bindings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal);

    /// <summary>【Tui】【按键映射】复用控制台映射并保留可打印符号的区别。</summary>
    /// <param name="actual">实际按键。</param><param name="id">配置的按键 ID。</param><returns>是否匹配。</returns>
    private static bool MatchesConsoleKey(ConsoleKeyInfo actual, string id)
    {
        var normalized = id.ToLowerInvariant();
        if (normalized.Split('+').Contains("super", StringComparer.Ordinal) || !TuiConsoleKeyInfoMapper.TryMapKeyId(normalized, null, out var expected)) return false;
        var modifiers = actual.Modifiers;
        var character = normalized.Split('+')[^1];
        // 1. 【Tui】【控制台符号】Windows 会为问号等成字结果附加 Shift，普通符号绑定按实际字符识别
        if (character.Length == 1 && !char.IsLetterOrDigit(character[0]) && actual.KeyChar == expected.KeyChar &&
            !expected.Modifiers.HasFlag(ConsoleModifiers.Shift) && !modifiers.HasFlag(ConsoleModifiers.Control))
            modifiers &= ~ConsoleModifiers.Shift;
        if (actual.Key != expected.Key || modifiers != expected.Modifiers) return false;
        if (actual.Key == ConsoleKey.NoName) return actual.KeyChar == expected.KeyChar;
        return character.Length != 1 || char.IsLetterOrDigit(character[0]) || actual.Modifiers.HasFlag(ConsoleModifiers.Control) || actual.KeyChar == expected.KeyChar;
    }
}
