using Tau.Ai;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentThinkingSelectorState(
    ThinkingLevel? CurrentLevel,
    IReadOnlyList<string> AvailableLevels);

public static class CodingAgentThinkingSelector
{
    public static readonly IReadOnlyList<string> DefaultLevels =
        CodingAgentThinkingLevels.DefaultLevels;

    public static Func<CodingAgentThinkingSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (state, cancellationToken) => SelectAsync(
            state,
            keyReader,
            TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
            cancellationToken);
    }

    public static TuiSelectList CreateSelectList(
        CodingAgentThinkingSelectorState state,
        int maxVisible = 6)
    {
        ArgumentNullException.ThrowIfNull(state);

        var levels = NormalizeAvailableLevels(state.AvailableLevels);
        var items = levels
            .Select(static level => new TuiSelectItem(
                level,
                level,
                FormatDescription(level)))
            .ToArray();
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32));
        var current = FormatThinkingLevel(state.CurrentLevel);
        var index = Array.FindIndex(items, item => item.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            selector.SetSelectedIndex(index);
        }

        return selector;
    }

    public static async Task<string?> SelectAsync(
        CodingAgentThinkingSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateSelectList(state);
        if (selector.FilteredItems.Count == 0)
        {
            return null;
        }

        var result = await new TuiSelectorSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.HasSelection ? result.SelectedItem?.Value : null;
    }

    private static IReadOnlyList<string> NormalizeAvailableLevels(IReadOnlyList<string> availableLevels)
    {
        if (availableLevels.Count == 0)
        {
            return DefaultLevels;
        }

        var result = new List<string>();
        foreach (var level in availableLevels)
        {
            var normalized = NormalizeLevel(level);
            if (normalized is null || result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static string? NormalizeLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => "off",
            "minimal" => "minimal",
            "low" => "low",
            "medium" or "med" => "medium",
            "high" => "high",
            "xhigh" or "extrahigh" or "extra-high" => "xhigh",
            _ => null
        };
    }

    private static string FormatDescription(string level) => level switch
    {
        "off" => "No reasoning",
        "minimal" => "Very brief reasoning (~1k tokens)",
        "low" => "Light reasoning (~2k tokens)",
        "medium" => "Moderate reasoning (~8k tokens)",
        "high" => "Deep reasoning (~16k tokens)",
        "xhigh" => "Maximum reasoning (~32k tokens)",
        _ => string.Empty
    };

    private static string FormatThinkingLevel(ThinkingLevel? level) => level switch
    {
        null => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "off"
    };
}
