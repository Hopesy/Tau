using Tau.Ai;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentScopedModelsSelectorState(
    string SettingsPath,
    IReadOnlyList<Model> AvailableModels,
    IReadOnlyList<string>? EnabledModels,
    Model CurrentModel);

public readonly record struct CodingAgentScopedModelsSelection(
    IReadOnlyList<string>? EnabledModels,
    bool IsCancelled)
{
    public static CodingAgentScopedModelsSelection Saved(IReadOnlyList<string>? enabledModels) =>
        new(enabledModels?.ToArray(), IsCancelled: false);

    public static CodingAgentScopedModelsSelection Cancelled { get; } =
        new(null, IsCancelled: true);
}

public static class CodingAgentScopedModelsSelector
{
    public static Func<CodingAgentScopedModelsSelectorState, CancellationToken, Task<CodingAgentScopedModelsSelection>> CreateConsoleSelector(
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

    public static Func<CodingAgentScopedModelsSelectorState, CancellationToken, Task<CodingAgentScopedModelsSelection>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (state, cancellationToken) =>
        {
            if (state.AvailableModels.Count == 0)
            {
                return CodingAgentScopedModelsSelection.Cancelled;
            }

            var selector = CreateSelectList(state);
            var result = await TuiCompositionOverlaySessions.RunAsync(selector, session, cancellationToken)
                .ConfigureAwait(false);
            return result.IsCancelled
                ? CodingAgentScopedModelsSelection.Cancelled
                : CodingAgentScopedModelsSelection.Saved(result.SelectedValues);
        };
    }

    public static TuiMultiSelectList CreateSelectList(
        CodingAgentScopedModelsSelectorState state,
        int maxVisible = 12)
    {
        ArgumentNullException.ThrowIfNull(state);

        var availableIds = state.AvailableModels
            .Select(FormatScopedModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = state.EnabledModels is null || state.EnabledModels.Count == 0
            ? null
            : state.EnabledModels
                .Select(id => TryResolveScopedModelId(id, state.AvailableModels, out var modelId) ? modelId : null)
                .Where(id => id is not null && availableIds.Contains(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var items = state.AvailableModels
            .Select(model => new TuiMultiSelectItem(
                FormatScopedModelId(model),
                FormatScopedModelId(model),
                model.Name,
                model.Provider))
            .ToArray();
        var selector = new TuiMultiSelectList(
            items,
            enabled,
            maxVisible: maxVisible,
            layout: new TuiMultiSelectListLayout(MinPrimaryColumnWidth: 34, MaxPrimaryColumnWidth: 48));
        selector.SetSelectedValue(FormatScopedModelId(state.CurrentModel));
        return selector;
    }

    public static async Task<CodingAgentScopedModelsSelection> SelectAsync(
        CodingAgentScopedModelsSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        if (state.AvailableModels.Count == 0)
        {
            return CodingAgentScopedModelsSelection.Cancelled;
        }

        var selector = CreateSelectList(state);
        var result = await new TuiMultiSelectSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsCancelled
            ? CodingAgentScopedModelsSelection.Cancelled
            : CodingAgentScopedModelsSelection.Saved(result.SelectedValues);
    }

    private static string FormatScopedModelId(Model model) => $"{model.Provider}/{model.Id}";

    private static bool TryResolveScopedModelId(
        string pattern,
        IReadOnlyList<Model> availableModels,
        out string id)
    {
        id = string.Empty;
        if (!CodingAgentScopedModelPatterns.TryResolve(pattern, availableModels, out var entry, out _))
        {
            return false;
        }

        id = entry.ModelId;
        return true;
    }
}
