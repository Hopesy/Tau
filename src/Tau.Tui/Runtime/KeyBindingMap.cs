using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class KeyBindingMap : IKeyBindingMap
{
    private readonly Dictionary<KeyBinding, EditorAction> _bindings;

    public KeyBindingMap(IEnumerable<KeyValuePair<KeyBinding, EditorAction>> bindings)
    {
        _bindings = new Dictionary<KeyBinding, EditorAction>();
        foreach (var pair in bindings)
        {
            _bindings[pair.Key] = pair.Value;
        }
    }

    public EditorAction Resolve(ConsoleKeyInfo key) =>
        _bindings.TryGetValue(KeyBinding.From(key), out var action) ? action : EditorAction.None;

    public IReadOnlyDictionary<KeyBinding, EditorAction> Bindings => _bindings;

    public static KeyBindingMap Default { get; } = new(DefaultBindings());

    public static KeyBindingMap WithOverrides(IEnumerable<KeyValuePair<KeyBinding, EditorAction>> overrides)
    {
        var combined = new Dictionary<KeyBinding, EditorAction>(Default._bindings);
        foreach (var pair in overrides)
        {
            if (pair.Value == EditorAction.None)
            {
                combined.Remove(pair.Key);
            }
            else
            {
                combined[pair.Key] = pair.Value;
            }
        }
        return new KeyBindingMap(combined);
    }

    private static IEnumerable<KeyValuePair<KeyBinding, EditorAction>> DefaultBindings()
    {
        yield return Pair(ConsoleKey.C, ConsoleModifiers.Control, EditorAction.Cancel);
        yield return Pair(ConsoleKey.Enter, default, EditorAction.Submit);
        yield return Pair(ConsoleKey.Backspace, default, EditorAction.DeletePrevChar);
        yield return Pair(ConsoleKey.Backspace, ConsoleModifiers.Control, EditorAction.DeletePrevWord);
        yield return Pair(ConsoleKey.Delete, default, EditorAction.DeleteNextChar);
        yield return Pair(ConsoleKey.Delete, ConsoleModifiers.Control, EditorAction.DeleteNextWord);
        yield return Pair(ConsoleKey.LeftArrow, default, EditorAction.CursorLeft);
        yield return Pair(ConsoleKey.LeftArrow, ConsoleModifiers.Control, EditorAction.CursorPrevWord);
        yield return Pair(ConsoleKey.RightArrow, default, EditorAction.CursorRight);
        yield return Pair(ConsoleKey.RightArrow, ConsoleModifiers.Control, EditorAction.CursorNextWord);
        yield return Pair(ConsoleKey.Home, default, EditorAction.CursorLineStart);
        yield return Pair(ConsoleKey.End, default, EditorAction.CursorLineEnd);
        yield return Pair(ConsoleKey.UpArrow, default, EditorAction.HistoryPrev);
        yield return Pair(ConsoleKey.DownArrow, default, EditorAction.HistoryNext);
        yield return Pair(ConsoleKey.A, ConsoleModifiers.Control, EditorAction.CursorLineStart);
        yield return Pair(ConsoleKey.E, ConsoleModifiers.Control, EditorAction.CursorLineEnd);
        yield return Pair(ConsoleKey.K, ConsoleModifiers.Control, EditorAction.KillToLineEnd);
        yield return Pair(ConsoleKey.U, ConsoleModifiers.Control, EditorAction.KillToLineStart);
        yield return Pair(ConsoleKey.R, ConsoleModifiers.Control, EditorAction.ReverseSearch);
    }

    private static KeyValuePair<KeyBinding, EditorAction> Pair(ConsoleKey key, ConsoleModifiers mods, EditorAction action) =>
        new(new KeyBinding(key, mods), action);
}

public static class KeyBindingFileStore
{
    public static KeyBindingMap LoadOrDefault(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return KeyBindingMap.Default;
        }

        try
        {
            var json = File.ReadAllText(path);
            return Parse(json);
        }
        catch
        {
            return KeyBindingMap.Default;
        }
    }

    public static KeyBindingMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
        {
            return KeyBindingMap.Default;
        }

        var overrides = new List<KeyValuePair<KeyBinding, EditorAction>>();
        foreach (var entry in bindings.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String) continue;

            if (!Enum.TryParse<ConsoleKey>(keyEl.GetString(), ignoreCase: true, out var key)) continue;
            if (!Enum.TryParse<EditorAction>(actionEl.GetString(), ignoreCase: true, out var action)) continue;

            var modifiers = ConsoleModifiers.None;
            if (entry.TryGetProperty("modifiers", out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in modsEl.EnumerateArray())
                {
                    if (mod.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<ConsoleModifiers>(mod.GetString(), ignoreCase: true, out var parsed))
                    {
                        modifiers |= parsed;
                    }
                }
            }

            overrides.Add(new(new KeyBinding(key, modifiers), action));
        }

        return KeyBindingMap.WithOverrides(overrides);
    }
}
