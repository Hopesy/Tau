using System.Runtime.InteropServices;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【快捷键提示】对齐上游 <c>modes/interactive/components/keybinding-hints.ts</c>，
/// 把快捷键 id 格式化为内联 UI 提示文本，例如工具输出中的展开提示。macOS 下会把 <c>alt</c> 显示为 <c>option</c>。
/// </summary>
public static class CodingAgentKeyHintFormatter
{
    private static readonly bool IsMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// 格式化快捷键 id 文本，保留 <c>/</c> 备选键和 <c>+</c> 组合键结构。
    /// </summary>
    /// <param name="key">待格式化的快捷键 id，例如 <c>ctrl+o</c> 或 <c>shift+enter/ctrl+j</c>。</param>
    /// <param name="capitalize">是否把每个快捷键片段首字母转为大写。</param>
    /// <returns>用于 UI 提示的快捷键文本。</returns>
    public static string FormatKeyText(string key, bool capitalize = false)
    {
        ArgumentNullException.ThrowIfNull(key);

        return string.Join(
            "/",
            key.Split('/').Select(alternative =>
                string.Join(
                    "+",
                    alternative.Split('+').Select(part => FormatKeyPart(part, capitalize)))));
    }

    /// <summary>
    /// 从快捷键映射中解析指定编辑器动作的首个绑定并格式化为 UI 提示文本。
    /// </summary>
    /// <param name="bindings">当前可用的快捷键映射；为空时不会返回提示。</param>
    /// <param name="action">需要查找快捷键的编辑器动作。</param>
    /// <param name="capitalize">是否把每个快捷键片段首字母转为大写。</param>
    /// <returns>存在绑定时返回格式化后的快捷键文本；不存在绑定时返回 <see langword="null"/>。</returns>
    public static string? KeyTextForAction(
        IKeyBindingMap? bindings,
        EditorAction action,
        bool capitalize = false)
    {
        if (bindings is null)
        {
            return null;
        }

        var keyId = bindings.Bindings
            .Where(pair => pair.Value == action)
            .Select(pair => FormatKeyId(pair.Key))
            .OrderBy(static text => text, StringComparer.Ordinal)
            .FirstOrDefault();

        return string.IsNullOrEmpty(keyId) ? null : FormatKeyText(keyId, capitalize);
    }

    /// <summary>
    /// 把单个快捷键绑定转换为小写快捷键 id，并按上游 ctrl、alt、shift 的顺序排列修饰键。
    /// </summary>
    /// <param name="binding">需要格式化的快捷键绑定。</param>
    /// <returns>小写快捷键 id，例如 <c>ctrl+o</c>。</returns>
    public static string FormatKeyId(KeyBinding binding)
    {
        var parts = new List<string>(4);
        if ((binding.Modifiers & ConsoleModifiers.Control) != 0)
        {
            parts.Add("ctrl");
        }

        if ((binding.Modifiers & ConsoleModifiers.Alt) != 0)
        {
            parts.Add("alt");
        }

        if ((binding.Modifiers & ConsoleModifiers.Shift) != 0)
        {
            parts.Add("shift");
        }

        parts.Add(FormatKeyName(binding.Key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// 格式化快捷键片段，并在 macOS 上把 alt 转为 option。
    /// </summary>
    /// <param name="part">待格式化的单个快捷键片段。</param>
    /// <param name="capitalize">是否把片段首字母转为大写。</param>
    /// <returns>用于展示的快捷键片段。</returns>
    private static string FormatKeyPart(string part, bool capitalize)
    {
        var displayPart = IsMacOs && part.Equals("alt", StringComparison.OrdinalIgnoreCase)
            ? "option"
            : part;

        if (!capitalize || displayPart.Length == 0)
        {
            return displayPart;
        }

        return char.ToUpperInvariant(displayPart[0]) + displayPart[1..];
    }

    /// <summary>
    /// 把 <see cref="ConsoleKey"/> 转换为上游快捷键 id 使用的键名。
    /// </summary>
    /// <param name="key">需要转换的控制台按键。</param>
    /// <returns>小写键名文本。</returns>
    private static string FormatKeyName(ConsoleKey key) => key switch
    {
        ConsoleKey.Backspace => "backspace",
        ConsoleKey.Delete => "delete",
        ConsoleKey.Enter => "enter",
        ConsoleKey.Escape => "esc",
        ConsoleKey.LeftArrow => "left",
        ConsoleKey.RightArrow => "right",
        ConsoleKey.UpArrow => "up",
        ConsoleKey.DownArrow => "down",
        ConsoleKey.Home => "home",
        ConsoleKey.End => "end",
        ConsoleKey.Tab => "tab",
        ConsoleKey.Spacebar => "space",
        _ => key.ToString().ToLowerInvariant()
    };
}
