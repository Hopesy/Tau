using Microsoft.VisualBasic.FileIO;
using System.Text;
using System.Text.Json;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentResumeSelectorState(
    string? CurrentSessionPath,
    string CurrentWorkingDirectory,
    IReadOnlyList<CodingAgentResumeSessionInfo> Sessions);

public readonly record struct CodingAgentResumeSelectionResult(
    string? SelectedPath,
    string? RenamedCurrentSessionName = null)
{
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedPath);
}

public readonly record struct CodingAgentResumeRenameRequest(
    string SessionPath,
    string NextName,
    bool IsCurrentSession);

public readonly record struct CodingAgentResumeDeleteRequest(
    string SessionPath);

internal readonly record struct CodingAgentResumeDeleteResult(
    bool Success,
    string Method,
    string? Error = null);

internal enum CodingAgentResumeSortMode
{
    Threaded,
    Recent,
    Relevance
}

public sealed class CodingAgentResumeSelectorComponent : ITuiInputComponent
{
    private readonly int _maxVisible;
    private IReadOnlyList<CodingAgentResumeSessionInfo> _sessions;
    private readonly string _currentWorkingDirectory;
    private string? _currentSessionPath;
    private string _filter;
    private bool _showAllSessions;
    private bool _showPath = true;
    private bool _namedOnly;
    private CodingAgentResumeSortMode _sortMode;
    private TuiSelectList _selector;
    private ResumeSelectorMode _mode;
    private string _renameValue;
    private string? _renameTargetPath;
    private bool _renameCurrentSession;
    private CodingAgentResumeRenameRequest? _pendingRenameRequest;
    private string? _confirmingDeletePath;
    private CodingAgentResumeDeleteRequest? _pendingDeleteRequest;
    private string? _statusMessage;
    private bool _statusIsError;

    public CodingAgentResumeSelectorComponent(
        CodingAgentResumeSelectorState state,
        int maxVisible = 10)
    {
        ArgumentNullException.ThrowIfNull(state);
        _maxVisible = Math.Max(1, maxVisible);
        _sessions = state.Sessions;
        _currentWorkingDirectory = NormalizePath(state.CurrentWorkingDirectory);
        _currentSessionPath = NormalizePath(state.CurrentSessionPath);
        _filter = string.Empty;
        _renameValue = string.Empty;
        _sortMode = CodingAgentResumeSortMode.Threaded;
        _selector = CreateSelectList();
    }

    public event Action<TuiSelectItem>? Selected;
    public event Action? Cancelled;

    public string Filter => _filter;
    public IReadOnlyList<TuiSelectItem> FilteredItems => _selector.FilteredItems;
    public TuiSelectItem? SelectedItem => _selector.SelectedItem;
    public string? CurrentSessionPath => _currentSessionPath;
    public bool HasSessions => _sessions.Count > 0;

    public void Invalidate() => _selector.Invalidate();

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        return _mode == ResumeSelectorMode.Rename
            ? RenderRename(width)
            : RenderList(width);
    }

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        return _mode == ResumeSelectorMode.Rename
            ? HandleRenameInput(key)
            : HandleListInput(key);
    }

    public bool TryDequeueRenameRequest(out CodingAgentResumeRenameRequest request)
    {
        if (_pendingRenameRequest is { } pending)
        {
            _pendingRenameRequest = null;
            request = pending;
            return true;
        }

        request = default;
        return false;
    }

    public bool TryDequeueDeleteRequest(out CodingAgentResumeDeleteRequest request)
    {
        if (_pendingDeleteRequest is { } pending)
        {
            _pendingDeleteRequest = null;
            request = pending;
            return true;
        }

        request = default;
        return false;
    }

    public void CompleteRenameSuccess(
        IReadOnlyList<CodingAgentResumeSessionInfo> sessions,
        string? currentSessionPath,
        string preferredSelection,
        string message)
    {
        _sessions = sessions;
        _currentSessionPath = NormalizePath(currentSessionPath);
        _mode = ResumeSelectorMode.List;
        _renameTargetPath = null;
        _renameCurrentSession = false;
        _renameValue = string.Empty;
        SetStatus(message, isError: false);
        RebuildSelector(preferredSelection);
    }

    public void CompleteRenameFailure(string message)
    {
        SetStatus(message, isError: true);
    }

    public void CompleteDeleteSuccess(
        IReadOnlyList<CodingAgentResumeSessionInfo> sessions,
        string? currentSessionPath,
        string? preferredSelection,
        string message)
    {
        _sessions = sessions;
        _currentSessionPath = NormalizePath(currentSessionPath);
        _confirmingDeletePath = null;
        SetStatus(message, isError: false);
        RebuildSelector(preferredSelection);
    }

    public void CompleteDeleteFailure(string message)
    {
        _confirmingDeletePath = null;
        SetStatus(message, isError: true);
    }

    private IReadOnlyList<string> RenderList(int width)
    {
        var lines = new List<string>
        {
            RenderRule("Resume Session", width),
            TuiText.TruncateToWidth($"Search: {_filter}", width, string.Empty),
            TuiText.TruncateToWidth(
                $"Scope: {(_showAllSessions ? "all" : "current")} | Filter: {(_namedOnly ? "named" : "all")} | Sort: {FormatSortMode(_sortMode)} | Path: {(_showPath ? "on" : "off")}",
                width,
                string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            lines.Add(TuiText.TruncateToWidth(_statusMessage!, width, string.Empty));
        }

        lines.AddRange(_selector.Render(width));
        lines.Add(RenderRule(string.Empty, width));
        return lines;
    }

    private IReadOnlyList<string> RenderRename(int width)
    {
        var lines = new List<string>
        {
            RenderRule("Rename Session", width),
            TuiText.TruncateToWidth($"Name: {_renameValue}", width, string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            lines.Add(TuiText.TruncateToWidth(_statusMessage!, width, string.Empty));
        }

        lines.Add(TuiText.TruncateToWidth("Enter to save, Esc to cancel", width, string.Empty));
        lines.Add(RenderRule(string.Empty, width));
        return lines;
    }

    private TuiInputResult HandleListInput(ConsoleKeyInfo key)
    {
        if (IsRenameShortcut(key))
        {
            EnterRenameMode();
            return TuiInputResult.Handled;
        }

        if (!string.IsNullOrWhiteSpace(_confirmingDeletePath))
        {
            if (key.Key == ConsoleKey.Enter)
            {
                _pendingDeleteRequest = new CodingAgentResumeDeleteRequest(_confirmingDeletePath!);
                return TuiInputResult.Handled;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                _confirmingDeletePath = null;
                ClearStatus();
                return TuiInputResult.Handled;
            }

            return TuiInputResult.Handled;
        }

        var selectorResult = _selector.HandleInput(key);
        if (selectorResult.Consumed)
        {
            return selectorResult;
        }

        if (key.Key == ConsoleKey.Backspace && _filter.Length > 0)
        {
            _filter = _filter[..^1];
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        if (IsPrintableSearchInput(key))
        {
            _filter += key.KeyChar;
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        if (IsDeleteShortcut(key))
        {
            StartDeleteConfirmation();
            return TuiInputResult.Handled;
        }

        if (key.Key == ConsoleKey.Tab && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0)
        {
            _showAllSessions = !_showAllSessions;
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        if (IsToggleNamedFilterShortcut(key))
        {
            _namedOnly = !_namedOnly;
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        if (IsTogglePathShortcut(key))
        {
            _showPath = !_showPath;
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        if (IsToggleSortShortcut(key))
        {
            _sortMode = NextSortMode(_sortMode);
            ClearStatus();
            RebuildSelector();
            return TuiInputResult.Handled;
        }

        return TuiInputResult.Ignored;
    }

    private TuiInputResult HandleRenameInput(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            ExitRenameMode(clearStatus: true);
            return TuiInputResult.Handled;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var next = _renameValue.Trim();
            if (next.Length == 0 || string.IsNullOrWhiteSpace(_renameTargetPath))
            {
                SetStatus("name cannot be empty", isError: true);
                return TuiInputResult.Handled;
            }

            _pendingRenameRequest = new CodingAgentResumeRenameRequest(
                _renameTargetPath!,
                next,
                _renameCurrentSession);
            return TuiInputResult.Handled;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_renameValue.Length > 0)
            {
                _renameValue = _renameValue[..^1];
            }
            ClearStatus();
            return TuiInputResult.Handled;
        }

        if (IsPrintableSearchInput(key))
        {
            _renameValue += key.KeyChar;
            ClearStatus();
            return TuiInputResult.Handled;
        }

        return TuiInputResult.Ignored;
    }

    private void EnterRenameMode()
    {
        var session = ResolveSelectedSession();
        if (session is null)
        {
            return;
        }

        _mode = ResumeSelectorMode.Rename;
        _renameTargetPath = session.FilePath;
        _renameCurrentSession = session.IsCurrent;
        _renameValue = session.Name ?? string.Empty;
        ClearStatus();
    }

    private void ExitRenameMode(bool clearStatus)
    {
        _mode = ResumeSelectorMode.List;
        _renameTargetPath = null;
        _renameCurrentSession = false;
        _renameValue = string.Empty;
        _pendingRenameRequest = null;
        if (clearStatus)
        {
            ClearStatus();
        }
    }

    private void RebuildSelector(string? preferredSelection = null)
    {
        var selectedValue = preferredSelection ?? _selector.SelectedItem?.Value;
        _selector = CreateSelectList(selectedValue);
    }

    private TuiSelectList CreateSelectList(string? preferredSelection = null)
    {
        var selector = CodingAgentResumeSelector.CreateSelectList(
            new CodingAgentResumeSelectorState(_currentSessionPath, _currentWorkingDirectory, _sessions),
            _maxVisible,
            includeFooterHint: false,
            filter: _filter,
            showAllSessions: _showAllSessions,
            namedOnly: _namedOnly,
            showPath: _showPath,
            sortMode: _sortMode,
            preferredSelection: preferredSelection);
        selector.Selected += item => Selected?.Invoke(item);
        selector.Cancelled += () => Cancelled?.Invoke();
        return selector;
    }

    private CodingAgentResumeSessionInfo? ResolveSelectedSession()
    {
        var selectedValue = _selector.SelectedItem?.Value;
        if (string.IsNullOrWhiteSpace(selectedValue))
        {
            return null;
        }

        var normalized = NormalizePath(selectedValue);
        return _sessions.FirstOrDefault(session =>
            NormalizePath(session.FilePath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string message, bool isError)
    {
        _statusMessage = message;
        _statusIsError = isError;
        _ = _statusIsError;
    }

    private void StartDeleteConfirmation()
    {
        var session = ResolveSelectedSession();
        if (session is null)
        {
            return;
        }

        if (session.IsCurrent)
        {
            SetStatus("Cannot delete the currently active session", isError: true);
            return;
        }

        _confirmingDeletePath = session.FilePath;
        SetStatus("Delete session? Enter to confirm, Esc to cancel", isError: true);
    }

    private void ClearStatus()
    {
        _statusMessage = null;
        _statusIsError = false;
    }

    private static bool IsPrintableSearchInput(ConsoleKeyInfo key) =>
        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0 &&
        key.KeyChar != '\0' &&
        !char.IsControl(key.KeyChar);

    private static bool IsRenameShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.R &&
        (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control &&
        (key.Modifiers & ConsoleModifiers.Alt) == 0;

    private static bool IsDeleteShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.D &&
        (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control &&
        (key.Modifiers & ConsoleModifiers.Alt) == 0;

    private static bool IsToggleNamedFilterShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.N &&
        (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control &&
        (key.Modifiers & ConsoleModifiers.Alt) == 0;

    private static bool IsTogglePathShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.P &&
        (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control &&
        (key.Modifiers & ConsoleModifiers.Alt) == 0;

    private static bool IsToggleSortShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.S &&
        (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control &&
        (key.Modifiers & ConsoleModifiers.Alt) == 0;

    private static CodingAgentResumeSortMode NextSortMode(CodingAgentResumeSortMode sortMode) =>
        sortMode switch
        {
            CodingAgentResumeSortMode.Threaded => CodingAgentResumeSortMode.Recent,
            CodingAgentResumeSortMode.Recent => CodingAgentResumeSortMode.Relevance,
            CodingAgentResumeSortMode.Relevance => CodingAgentResumeSortMode.Threaded,
            _ => CodingAgentResumeSortMode.Threaded
        };

    private static string FormatSortMode(CodingAgentResumeSortMode sortMode) =>
        sortMode switch
        {
            CodingAgentResumeSortMode.Threaded => "threaded",
            CodingAgentResumeSortMode.Recent => "recent",
            CodingAgentResumeSortMode.Relevance => "relevance",
            _ => "threaded"
        };

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static string RenderRule(string title, int width)
    {
        if (width <= 1)
        {
            return "-";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return new string('-', width);
        }

        var text = $" {title.Trim()} ";
        var textWidth = TuiText.VisibleWidth(text);
        if (textWidth >= width)
        {
            return TuiText.TruncateToWidth(title, width, string.Empty);
        }

        var remaining = width - textWidth;
        var left = remaining / 2;
        var right = remaining - left;
        return new string('-', left) + text + new string('-', right);
    }

    private enum ResumeSelectorMode
    {
        List,
        Rename
    }
}

public static class CodingAgentResumeSelector
{
    public static Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>> CreateConsoleSelector(
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

    public static Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (state, cancellationToken) =>
        {
            var selector = CreateComponent(state);
            if (!selector.HasSessions)
            {
                return default;
            }

            string? selected = null;
            string? renamedCurrentName = null;
            var cancelled = false;

            void OnSelected(TuiSelectItem item) => selected = item.Value;
            void OnCancelled() => cancelled = true;

            selector.Selected += OnSelected;
            selector.Cancelled += OnCancelled;
            var width = Math.Max(1, Math.Min(96, session.Viewport.Width));
            var handle = session.OpenOverlay(
                selector,
                new TuiTranscriptOverlayOptions(
                    Width: width,
                    Row: Math.Max(0, Math.Min(1, session.Viewport.MessageHeight - 1)),
                    Column: Math.Max(0, (session.Viewport.Width - width) / 2)));
            try
            {
                if (!session.IsStarted)
                {
                    session.Start();
                }
                else
                {
                    session.Render(force: true);
                }

                while (selected is null && !cancelled)
                {
                    await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
                    if (selector.TryDequeueRenameRequest(out var renameRequest))
                    {
                        var result = await TryRenameSessionAsync(selector, renameRequest, cancellationToken).ConfigureAwait(false);
                        if (renameRequest.IsCurrentSession && result)
                        {
                            renamedCurrentName = renameRequest.NextName;
                        }

                        session.Render(force: true);
                    }

                    if (selector.TryDequeueDeleteRequest(out var deleteRequest))
                    {
                        await TryDeleteSessionAsync(selector, deleteRequest, cancellationToken).ConfigureAwait(false);
                        session.Render(force: true);
                    }
                }

                return new CodingAgentResumeSelectionResult(
                    cancelled ? null : selected,
                    renamedCurrentName);
            }
            finally
            {
                session.CloseOverlay(handle);
                selector.Selected -= OnSelected;
                selector.Cancelled -= OnCancelled;
            }
        };
    }

    public static TuiSelectList CreateSelectList(
        CodingAgentResumeSelectorState state,
        int maxVisible = 10) =>
        CreateSelectList(
            state,
            maxVisible,
            includeFooterHint: true,
            filter: null,
            showAllSessions: false,
            namedOnly: false,
            showPath: true,
            sortMode: CodingAgentResumeSortMode.Threaded,
            preferredSelection: null);

    public static CodingAgentResumeSelectorComponent CreateComponent(
        CodingAgentResumeSelectorState state,
        int maxVisible = 10) =>
        new(state, maxVisible);

    internal static TuiSelectList CreateSelectList(
        CodingAgentResumeSelectorState state,
        int maxVisible,
        bool includeFooterHint,
        string? filter,
        bool showAllSessions,
        bool namedOnly,
        bool showPath,
        CodingAgentResumeSortMode sortMode,
        string? preferredSelection)
    {
        ArgumentNullException.ThrowIfNull(state);

        var currentWorkingDirectory = NormalizeSelectorPath(state.CurrentWorkingDirectory);
        var sessions = state.Sessions
            .Where(session => showAllSessions ||
                NormalizeSelectorPath(session.Cwd).Equals(currentWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(session => !namedOnly || !string.IsNullOrWhiteSpace(session.Name))
            .Where(session => MatchesFilter(session, filter))
            .ToArray();
        var items = BuildSelectItems(sessions, sortMode, filter, showPath);
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(
                MinPrimaryColumnWidth: 24,
                MaxPrimaryColumnWidth: 40,
                FooterHint: includeFooterHint ? "Tab scope | Ctrl+S sort | Ctrl+N named | Ctrl+P path | Enter resume" : null));

        var targetSelection = preferredSelection;
        if (string.IsNullOrWhiteSpace(targetSelection) && !string.IsNullOrWhiteSpace(state.CurrentSessionPath))
        {
            targetSelection = Path.GetFullPath(state.CurrentSessionPath);
        }

        if (!string.IsNullOrWhiteSpace(targetSelection))
        {
            var normalizedTarget = Path.GetFullPath(targetSelection);
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index].Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    selector.SetSelectedIndex(index);
                    break;
                }
            }
        }

        return selector;
    }

    public static async Task<CodingAgentResumeSelectionResult> SelectAsync(
        CodingAgentResumeSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateComponent(state);
        if (!selector.HasSessions)
        {
            return default;
        }

        string? selected = null;
        string? renamedCurrentName = null;
        var cancelled = false;

        void OnSelected(TuiSelectItem item) => selected = item.Value;
        void OnCancelled() => cancelled = true;

        selector.Selected += OnSelected;
        selector.Cancelled += OnCancelled;
        try
        {
            var host = new TuiOverlayHost(selector, keyReader, surface);
            host.Render(force: true);
            while (selected is null && !cancelled)
            {
                await host.ReadInputAsync(cancellationToken).ConfigureAwait(false);
                if (selector.TryDequeueRenameRequest(out var renameRequest))
                {
                    var result = await TryRenameSessionAsync(selector, renameRequest, cancellationToken).ConfigureAwait(false);
                    if (renameRequest.IsCurrentSession && result)
                    {
                        renamedCurrentName = renameRequest.NextName;
                    }

                    host.Render(force: true);
                }

                if (selector.TryDequeueDeleteRequest(out var deleteRequest))
                {
                    await TryDeleteSessionAsync(selector, deleteRequest, cancellationToken).ConfigureAwait(false);
                    host.Render(force: true);
                }
            }

            return new CodingAgentResumeSelectionResult(
                cancelled ? null : selected,
                renamedCurrentName);
        }
        finally
        {
            selector.Selected -= OnSelected;
            selector.Cancelled -= OnCancelled;
        }
    }

    private static string FormatDescription(CodingAgentResumeSessionInfo session)
        => FormatDescription(session, showPath: true);

    private static string FormatDescription(CodingAgentResumeSessionInfo session, bool showPath)
    {
        var modified = session.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var current = session.IsCurrent ? "current, " : string.Empty;
        var baseDescription = $"{current}{session.Provider}/{session.Model}, {session.MessageCount} messages, {modified}";
        return showPath ? $"{baseDescription}, {session.FilePath}" : baseDescription;
    }

    private static IReadOnlyList<TuiSelectItem> BuildSelectItems(
        IReadOnlyList<CodingAgentResumeSessionInfo> sessions,
        CodingAgentResumeSortMode sortMode,
        string? filter,
        bool showPath)
    {
        if (sortMode == CodingAgentResumeSortMode.Threaded && string.IsNullOrWhiteSpace(filter))
        {
            return BuildThreadedItems(sessions, showPath);
        }

        var ordered = OrderFlatSessions(sessions, sortMode, filter);
        return ordered
            .Select(session => new TuiSelectItem(
                session.FilePath,
                session.DisplayName,
                FormatDescription(session, showPath)))
            .ToArray();
    }

    private static IReadOnlyList<TuiSelectItem> BuildThreadedItems(
        IReadOnlyList<CodingAgentResumeSessionInfo> sessions,
        bool showPath)
    {
        var nodesByPath = sessions.ToDictionary(
            session => NormalizeSelectorPath(session.FilePath),
            session => new ResumeTreeNode(session),
            StringComparer.OrdinalIgnoreCase);

        var roots = new List<ResumeTreeNode>();
        foreach (var session in sessions)
        {
            var path = NormalizeSelectorPath(session.FilePath);
            if (!nodesByPath.TryGetValue(path, out var node))
            {
                continue;
            }

            var parentPath = NormalizeSelectorPath(session.ParentSessionPath);
            if (!string.IsNullOrWhiteSpace(parentPath) &&
                !parentPath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                nodesByPath.TryGetValue(parentPath, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        SortThreadedNodes(roots);

        var items = new List<TuiSelectItem>();
        FlattenThreadedNodes(roots, items, [], showPath);
        return items;
    }

    private static void FlattenThreadedNodes(
        IReadOnlyList<ResumeTreeNode> nodes,
        List<TuiSelectItem> items,
        IReadOnlyList<bool> ancestorContinues,
        bool showPath)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index]!;
            var isLast = index == nodes.Count - 1;
            var prefix = BuildTreePrefix(ancestorContinues, isLast);
            var label = string.IsNullOrWhiteSpace(prefix) ? node.Session.DisplayName : prefix + node.Session.DisplayName;
            items.Add(new TuiSelectItem(
                node.Session.FilePath,
                label,
                FormatDescription(node.Session, showPath)));

            var nextAncestorContinues = ancestorContinues.Concat([!isLast]).ToArray();
            FlattenThreadedNodes(node.Children, items, nextAncestorContinues, showPath);
        }
    }

    private static string BuildTreePrefix(IReadOnlyList<bool> ancestorContinues, bool isLast)
    {
        if (ancestorContinues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < ancestorContinues.Count - 1; index++)
        {
            var continues = ancestorContinues[index];
            builder.Append(continues ? "│  " : "   ");
        }

        builder.Append(isLast ? "└─ " : "├─ ");
        return builder.ToString();
    }

    private static void SortThreadedNodes(List<ResumeTreeNode> nodes)
    {
        nodes.Sort(CompareSessionsByRecency);
        foreach (var node in nodes)
        {
            SortThreadedNodes(node.Children);
        }
    }

    private static IReadOnlyList<CodingAgentResumeSessionInfo> OrderFlatSessions(
        IReadOnlyList<CodingAgentResumeSessionInfo> sessions,
        CodingAgentResumeSortMode sortMode,
        string? filter)
    {
        if (sortMode == CodingAgentResumeSortMode.Recent || string.IsNullOrWhiteSpace(filter))
        {
            return sessions;
        }

        return sessions
            .Select((session, index) => new
            {
                Session = session,
                Score = ComputeRelevanceScore(session, filter.Trim()),
                Index = index
            })
            .OrderBy(static entry => entry.Score)
            .ThenByDescending(static entry => entry.Session.LastModifiedUtc)
            .ThenBy(static entry => entry.Index)
            .Select(static entry => entry.Session)
            .ToArray();
    }

    private static double ComputeRelevanceScore(CodingAgentResumeSessionInfo session, string filter)
    {
        var score = MatchRelevanceScore(session.DisplayName, filter, 0);
        score = Math.Min(score, MatchRelevanceScore($"{session.Provider}/{session.Model}", filter, 100));
        score = Math.Min(score, MatchRelevanceScore(session.Cwd, filter, 200));
        score = Math.Min(score, MatchRelevanceScore(session.FilePath, filter, 300));
        return score;
    }

    private static double MatchRelevanceScore(string? text, string filter, double bias)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return double.MaxValue;
        }

        var index = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? double.MaxValue : bias + index * 0.1;
    }

    private static int CompareSessionsByRecency(
        ResumeTreeNode left,
        ResumeTreeNode right) =>
        CompareSessionsByRecency(left.Session, right.Session);

    private static int CompareSessionsByRecency(
        CodingAgentResumeSessionInfo left,
        CodingAgentResumeSessionInfo right)
    {
        var modified = right.LastModifiedUtc.CompareTo(left.LastModifiedUtc);
        if (modified != 0)
        {
            return modified;
        }

        return string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(CodingAgentResumeSessionInfo session, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var normalized = filter.Trim();
        return session.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase)
               || session.Provider.Contains(normalized, StringComparison.OrdinalIgnoreCase)
               || session.Model.Contains(normalized, StringComparison.OrdinalIgnoreCase)
               || session.Cwd.Contains(normalized, StringComparison.OrdinalIgnoreCase)
               || session.FilePath.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSelectorPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static string FormatSortMode(CodingAgentResumeSortMode sortMode) =>
        sortMode switch
        {
            CodingAgentResumeSortMode.Threaded => "Threaded",
            CodingAgentResumeSortMode.Recent => "Recent",
            CodingAgentResumeSortMode.Relevance => "Relevance",
            _ => "Threaded"
        };

    private static CodingAgentResumeSortMode NextSortMode(CodingAgentResumeSortMode sortMode) =>
        sortMode switch
        {
            CodingAgentResumeSortMode.Threaded => CodingAgentResumeSortMode.Recent,
            CodingAgentResumeSortMode.Recent => CodingAgentResumeSortMode.Relevance,
            CodingAgentResumeSortMode.Relevance => CodingAgentResumeSortMode.Threaded,
            _ => CodingAgentResumeSortMode.Threaded
        };

    private static async Task<bool> TryRenameSessionAsync(
        CodingAgentResumeSelectorComponent selector,
        CodingAgentResumeRenameRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var store = new CodingAgentTreeSessionStore(request.SessionPath);
            store.AppendSessionInfo(request.NextName);
            selector.CompleteRenameSuccess(
                CodingAgentTreeSessionStore.ListAvailableSessions(selector.CurrentSessionPath),
                selector.CurrentSessionPath,
                request.SessionPath,
                "Session renamed");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            selector.CompleteRenameFailure($"Rename failed: {ex.Message}");
            return false;
        }
    }

    private static Task TryDeleteSessionAsync(
        CodingAgentResumeSelectorComponent selector,
        CodingAgentResumeDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var result = TryDeleteSessionFile(request.SessionPath, cancellationToken);
        if (result.Success)
        {
            var sessions = CodingAgentTreeSessionStore.ListAvailableSessions(selector.CurrentSessionPath);
            var preferredSelection = sessions.FirstOrDefault()?.FilePath;
            selector.CompleteDeleteSuccess(
                sessions,
                selector.CurrentSessionPath,
                preferredSelection,
                result.Method == "recycle-bin" ? "Session moved to recycle bin" : "Session deleted");
        }
        else
        {
            selector.CompleteDeleteFailure($"Delete failed: {result.Error ?? "Unknown error"}");
        }

        return Task.CompletedTask;
    }

    private static CodingAgentResumeDeleteResult TryDeleteSessionFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
                if (!File.Exists(path))
                {
                    return new CodingAgentResumeDeleteResult(true, "recycle-bin");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException)
            {
                // Fall through to direct delete with the original failure captured in the final error if needed.
            }
        }

        try
        {
            File.Delete(path);
            return new CodingAgentResumeDeleteResult(!File.Exists(path), "unlink");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodingAgentResumeDeleteResult(false, "unlink", ex.Message);
        }
    }

    private sealed record ResumeTreeNode(CodingAgentResumeSessionInfo Session)
    {
        public List<ResumeTreeNode> Children { get; } = [];
    }
}
