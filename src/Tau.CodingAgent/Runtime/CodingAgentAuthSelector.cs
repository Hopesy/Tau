using Tau.Ai.Auth;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentAuthSelectorState(
    string CurrentProvider,
    IReadOnlyList<ProviderAuthStatus> Providers);

public static class CodingAgentAuthSelector
{
    public static Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
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

    public static Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (state, cancellationToken) =>
        {
            var selector = CreateSelectList(state);
            if (selector.FilteredItems.Count == 0)
            {
                return null;
            }

            var result = await TuiCompositionOverlaySessions.RunAsync(selector, session, cancellationToken)
                .ConfigureAwait(false);
            return result.HasSelection ? result.SelectedItem?.Value : null;
        };
    }

    public static TuiSelectList CreateSelectList(
        CodingAgentAuthSelectorState state,
        int maxVisible = 10)
    {
        ArgumentNullException.ThrowIfNull(state);

        var items = state.Providers
            .Select(static status => new TuiSelectItem(
                status.Provider,
                CodingAgentProviderDisplayNames.Resolve(status.Provider),
                FormatDescription(status)))
            .ToArray();
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 18, MaxPrimaryColumnWidth: 32));
        var index = Array.FindIndex(items, item => item.Value.Equals(state.CurrentProvider, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            selector.SetSelectedIndex(index);
        }

        return selector;
    }

    public static async Task<string?> SelectAsync(
        CodingAgentAuthSelectorState state,
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

    private static string FormatDescription(ProviderAuthStatus status)
    {
        var configured = status.IsConfigured ? "configured" : "missing";
        var login = status.CanLogin ? ", login available" : string.Empty;
        var oauth = status.UsesOAuth ? ", oauth" : string.Empty;
        return $"{configured} via {status.Source}{oauth}{login}";
    }
}
