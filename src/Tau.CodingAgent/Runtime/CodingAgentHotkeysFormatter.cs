using System.Globalization;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentHotkeysFormatter
{
    private static readonly IReadOnlyDictionary<EditorAction, string> Descriptions =
        new Dictionary<EditorAction, string>
        {
            [EditorAction.Cancel] = "Cancel input",
            [EditorAction.Submit] = "Submit input",
            [EditorAction.DeletePrevChar] = "Delete previous character",
            [EditorAction.DeletePrevWord] = "Delete previous word",
            [EditorAction.DeleteNextChar] = "Delete next character",
            [EditorAction.DeleteNextWord] = "Delete next word",
            [EditorAction.CursorLeft] = "Move cursor left",
            [EditorAction.CursorRight] = "Move cursor right",
            [EditorAction.CursorPrevWord] = "Move cursor to previous word",
            [EditorAction.CursorNextWord] = "Move cursor to next word",
            [EditorAction.CursorLineStart] = "Move cursor to line start",
            [EditorAction.CursorLineEnd] = "Move cursor to line end",
            [EditorAction.KillToLineStart] = "Delete to line start",
            [EditorAction.KillToLineEnd] = "Delete to line end",
            [EditorAction.HistoryPrev] = "Previous input history",
            [EditorAction.HistoryNext] = "Next input history",
            [EditorAction.ReverseSearch] = "Reverse search input history"
        };

    public static string Format(IKeyBindingMap bindings)
    {
        var groups = bindings.Bindings
            .Where(static pair => pair.Value != EditorAction.None)
            .GroupBy(static pair => pair.Value)
            .OrderBy(static group => group.Key)
            .Select(static group =>
            {
                var keys = group
                    .Select(static pair => FormatBinding(pair.Key))
                    .Order(StringComparer.OrdinalIgnoreCase);
                var description = Descriptions.TryGetValue(group.Key, out var text)
                    ? text
                    : ToTitleCase(group.Key.ToString());
                return $"  {FormatActionName(group.Key),-20} {string.Join(", ", keys),-24} {description}";
            })
            .ToArray();

        if (groups.Length == 0)
        {
            return "hotkeys: none";
        }

        return $"hotkeys:{Environment.NewLine}{string.Join(Environment.NewLine, groups)}";
    }

    private static string FormatActionName(EditorAction action) => action switch
    {
        EditorAction.DeletePrevChar => "delete-prev-char",
        EditorAction.DeletePrevWord => "delete-prev-word",
        EditorAction.DeleteNextChar => "delete-next-char",
        EditorAction.DeleteNextWord => "delete-next-word",
        EditorAction.CursorLeft => "cursor-left",
        EditorAction.CursorRight => "cursor-right",
        EditorAction.CursorPrevWord => "cursor-prev-word",
        EditorAction.CursorNextWord => "cursor-next-word",
        EditorAction.CursorLineStart => "cursor-line-start",
        EditorAction.CursorLineEnd => "cursor-line-end",
        EditorAction.KillToLineStart => "kill-to-line-start",
        EditorAction.KillToLineEnd => "kill-to-line-end",
        EditorAction.HistoryPrev => "history-prev",
        EditorAction.HistoryNext => "history-next",
        EditorAction.ReverseSearch => "reverse-search",
        _ => action.ToString().ToLowerInvariant()
    };

    private static string FormatBinding(KeyBinding binding)
    {
        var parts = new List<string>(4);
        if ((binding.Modifiers & ConsoleModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((binding.Modifiers & ConsoleModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((binding.Modifiers & ConsoleModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(FormatKey(binding.Key));
        return string.Join("+", parts);
    }

    private static string FormatKey(ConsoleKey key) => key switch
    {
        ConsoleKey.Backspace => "Backspace",
        ConsoleKey.Delete => "Delete",
        ConsoleKey.Enter => "Enter",
        ConsoleKey.Escape => "Esc",
        ConsoleKey.LeftArrow => "Left",
        ConsoleKey.RightArrow => "Right",
        ConsoleKey.UpArrow => "Up",
        ConsoleKey.DownArrow => "Down",
        ConsoleKey.Home => "Home",
        ConsoleKey.End => "End",
        ConsoleKey.Spacebar => "Space",
        _ => key.ToString()
    };

    private static string ToTitleCase(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' '));
}
