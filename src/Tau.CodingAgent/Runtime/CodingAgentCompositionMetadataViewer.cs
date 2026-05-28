using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentCompositionMetadataViewer
{
    public static async Task RunAsync(
        CodingAgentTreeMetadataSnapshot snapshot,
        TuiCompositionSession session,
        CancellationToken cancellationToken = default)
    {
        _ = await RunWithSelectionAsync(snapshot, session, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> RunWithSelectionAsync(
        CodingAgentTreeMetadataSnapshot snapshot,
        TuiCompositionSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(session);

        var component = new MetadataViewerComponent(snapshot, () => Math.Max(1, session.Viewport.MessageHeight));
        var handle = session.OpenOverlay(
            component,
            new TuiTranscriptOverlayOptions(Width: int.MaxValue, Row: 0, Column: 0));

        try
        {
            if (!session.IsStarted)
            {
                session.Start();
            }

            while (!component.IsClosed)
            {
                await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return component.CurrentSelectionEntryId;
        }
        finally
        {
            session.CloseOverlay(handle);
        }
    }

    private sealed class MetadataViewerComponent : ITuiInputComponent
    {
        private const string HighlightStart = "\u001b[7m";
        private const string HighlightEnd = "\u001b[27m";

        private readonly CodingAgentTreeMetadataSnapshot _snapshot;
        private readonly Func<int> _heightProvider;
        private readonly List<string> _summaryLines;
        private readonly Stack<string> _detailHistory = new();
        private int _selectedIndex;
        private int _detailOffset;
        private int _listOffset;
        private int _lastVisibleBodyLines;
        private int _lastWrappedDetailLineCount;
        private bool _detailMode;
        private string? _detailEntryId;

        public MetadataViewerComponent(CodingAgentTreeMetadataSnapshot snapshot, Func<int> heightProvider)
        {
            _snapshot = snapshot;
            _heightProvider = heightProvider;
            _summaryLines =
            [
                $"metadata: file {snapshot.FilePath}, session {snapshot.SessionId}, leaf {FormatOptional(snapshot.LeafId, fallback: "root")}",
                $"cwd: {snapshot.Cwd}",
                $"parent session: {FormatOptional(snapshot.ParentSession, fallback: "none")}",
                $"counts: entries {snapshot.EntryCount}, branch entries {snapshot.BranchEntryCount}, messages {snapshot.MessageCount}, branch messages {snapshot.BranchMessageCount}, branches {snapshot.BranchCount}, labels {snapshot.LabelCount}"
            ];
            _detailMode = snapshot.FocusEntryId is not null && snapshot.VisibleEntryIds.Count > 0;
            _detailEntryId = snapshot.FocusEntryId;
            if (!string.IsNullOrWhiteSpace(snapshot.FocusEntryId))
            {
                var focusedIndex = snapshot.VisibleEntryIds
                    .Select(static (id, index) => new { id, index })
                    .FirstOrDefault(item => item.id.Equals(snapshot.FocusEntryId, StringComparison.OrdinalIgnoreCase))
                    ?.index;
                if (focusedIndex is not null)
                {
                    _selectedIndex = focusedIndex.Value;
                }
            }
        }

        public bool IsClosed { get; private set; }
        public string? CurrentSelectionEntryId => ResolveCurrentSelectionEntryId();

        public IReadOnlyList<string> Render(int width)
        {
            width = Math.Max(1, width);
            var maxHeight = Math.Max(1, _heightProvider());
            var headerText = _detailMode
                ? $"Metadata Inspector - Entry {CurrentEntry().EntryId} - j/k scroll, PgUp/PgDn page, Home/End jump, h/l or Tab browse entries, 1-9 follow relation, Enter/Esc back, q close"
                : "Metadata Inspector - j/k move, PgUp/PgDn page, Home/End jump, Enter inspect, Esc/q close";
            var headerLines = TuiText.Wrap(headerText, width).ToList();
            var summaryLines = _summaryLines
                .SelectMany(line => TuiText.Wrap(line, width))
                .ToList();
            var sectionLines = TuiText.Wrap(BuildSectionHeader(), width).ToList();
            var footerLines = TuiText.Wrap(BuildFooter(), width).ToList();

            var reserved = headerLines.Count + summaryLines.Count + sectionLines.Count + footerLines.Count;
            var bodyHeight = Math.Max(1, maxHeight - reserved);
            _lastVisibleBodyLines = bodyHeight;

            var bodyLines = _detailMode
                ? BuildDetailBody(width)
                : BuildListBody(width, bodyHeight);

            var output = new List<string>();
            output.AddRange(RenderHighlightedLines(headerLines, width));
            output.AddRange(summaryLines.Select(line => TuiText.TruncateToWidth(line, width, string.Empty, pad: true)));
            output.AddRange(RenderHighlightedLines(sectionLines, width));
            output.AddRange(bodyLines);
            output.AddRange(footerLines.Select(line => TuiText.TruncateToWidth(line, width, string.Empty, pad: true)));
            return output;
        }

        public void Invalidate()
        {
        }

        public TuiInputResult HandleInput(ConsoleKeyInfo key)
        {
            if (IsClosed)
            {
                return TuiInputResult.Handled;
            }

            switch (key.Key)
            {
                case ConsoleKey.J:
                case ConsoleKey.DownArrow:
                    Move(1);
                    return TuiInputResult.Handled;

                case ConsoleKey.K:
                case ConsoleKey.UpArrow:
                    Move(-1);
                    return TuiInputResult.Handled;

                case ConsoleKey.PageDown:
                case ConsoleKey.RightArrow:
                    Page(1);
                    return TuiInputResult.Handled;

                case ConsoleKey.PageUp:
                case ConsoleKey.LeftArrow:
                    Page(-1);
                    return TuiInputResult.Handled;

                case ConsoleKey.Home:
                    JumpHome();
                    return TuiInputResult.Handled;

                case ConsoleKey.End:
                    JumpEnd();
                    return TuiInputResult.Handled;

                case ConsoleKey.H when _detailMode:
                    BrowseDetailEntry(-1);
                    return TuiInputResult.Handled;

                case ConsoleKey.L when _detailMode:
                    BrowseDetailEntry(1);
                    return TuiInputResult.Handled;

                case ConsoleKey.Enter:
                    if (_detailMode)
                    {
                        CloseOrBack();
                    }
                    else if (_snapshot.VisibleEntryIds.Count > 0)
                    {
                        _detailMode = true;
                        _detailOffset = 0;
                        _detailEntryId = SelectedEntryId();
                        _detailHistory.Clear();
                    }
                    else
                    {
                        IsClosed = true;
                    }

                    return TuiInputResult.Handled;

                case ConsoleKey.Escape:
                    CloseOrBack();
                    return TuiInputResult.Handled;

                case ConsoleKey.Q:
                    IsClosed = true;
                    return TuiInputResult.Handled;

                default:
                    if (_detailMode && key.Key == ConsoleKey.Tab)
                    {
                        BrowseDetailEntry((key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1);
                        return TuiInputResult.Handled;
                    }

                    if (_detailMode && TryHandleRelationJump(key))
                    {
                        return TuiInputResult.Handled;
                    }

                    return TuiInputResult.Ignored;
            }
        }

        private void Move(int delta)
        {
            if (_detailMode)
            {
                _detailOffset = Math.Max(0, _detailOffset + delta);
                return;
            }

            if (_snapshot.VisibleEntryIds.Count == 0)
            {
                return;
            }

            _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _snapshot.VisibleEntryIds.Count - 1);
        }

        private void Page(int direction)
        {
            var delta = Math.Max(1, _lastVisibleBodyLines);
            if (_detailMode)
            {
                _detailOffset = Math.Max(0, _detailOffset + (direction * delta));
                return;
            }

            if (_snapshot.VisibleEntryIds.Count == 0)
            {
                return;
            }

            _selectedIndex = Math.Clamp(_selectedIndex + (direction * delta), 0, _snapshot.VisibleEntryIds.Count - 1);
        }

        private void JumpHome()
        {
            if (_detailMode)
            {
                _detailOffset = 0;
            }
            else
            {
                _selectedIndex = 0;
            }
        }

        private void JumpEnd()
        {
            if (_detailMode)
            {
                _detailOffset = int.MaxValue;
            }
            else if (_snapshot.VisibleEntryIds.Count > 0)
            {
                _selectedIndex = _snapshot.VisibleEntryIds.Count - 1;
            }
        }

        private void CloseOrBack()
        {
            if (_detailHistory.Count > 0)
            {
                _detailEntryId = _detailHistory.Pop();
                _detailOffset = 0;
                SyncSelectedIndex(_detailEntryId);
                return;
            }

            if (_detailMode && _snapshot.FocusEntryId is null)
            {
                SyncSelectedIndex(_detailEntryId);
                _detailMode = false;
                _detailOffset = 0;
                _detailEntryId = null;
                return;
            }

            IsClosed = true;
        }

        private void BrowseDetailEntry(int delta)
        {
            if (_snapshot.VisibleEntryIds.Count <= 1)
            {
                return;
            }

            var currentId = _detailEntryId ?? SelectedEntryId();
            var currentIndex = _snapshot.VisibleEntryIds
                .Select(static (id, index) => new { id, index })
                .FirstOrDefault(item => item.id.Equals(currentId, StringComparison.OrdinalIgnoreCase))
                ?.index ?? _selectedIndex;
            var nextIndex = Math.Clamp(currentIndex + delta, 0, _snapshot.VisibleEntryIds.Count - 1);
            if (nextIndex == currentIndex)
            {
                return;
            }

            _detailEntryId = _snapshot.VisibleEntryIds[nextIndex];
            SyncSelectedIndex(_detailEntryId);
            _detailOffset = 0;
            _detailHistory.Clear();
        }

        private IReadOnlyList<string> BuildDetailBody(int width)
        {
            var wrapped = BuildDetailLines()
                .SelectMany(line => TuiText.Wrap(line, width))
                .ToList();
            _lastWrappedDetailLineCount = wrapped.Count;
            var maxOffset = Math.Max(0, wrapped.Count - _lastVisibleBodyLines);
            _detailOffset = Math.Clamp(_detailOffset, 0, maxOffset);
            var visible = wrapped.Skip(_detailOffset).Take(_lastVisibleBodyLines).ToList();
            while (visible.Count < _lastVisibleBodyLines)
            {
                visible.Add(string.Empty);
            }

            return visible.Select(line => TuiText.TruncateToWidth(line, width, string.Empty, pad: true)).ToArray();
        }

        private IReadOnlyList<string> BuildListBody(int width, int bodyHeight)
        {
            if (_snapshot.VisibleEntryIds.Count == 0)
            {
                return [TuiText.TruncateToWidth("latest metadata: none", width, string.Empty, pad: true)];
            }

            var maxOffset = Math.Max(0, _snapshot.VisibleEntryIds.Count - bodyHeight);
            _listOffset = Math.Clamp(Math.Min(_selectedIndex, Math.Max(0, _selectedIndex - bodyHeight + 1)), 0, maxOffset);
            if (_selectedIndex < _listOffset)
            {
                _listOffset = _selectedIndex;
            }
            else if (_selectedIndex >= _listOffset + bodyHeight)
            {
                _listOffset = _selectedIndex - bodyHeight + 1;
            }

            var visibleEntryIds = _snapshot.VisibleEntryIds.Skip(_listOffset).Take(bodyHeight).ToArray();
            var lines = new List<string>(bodyHeight);
            for (var i = 0; i < visibleEntryIds.Length; i++)
            {
                var absoluteIndex = _listOffset + i;
                var prefix = absoluteIndex == _selectedIndex
                    ? $"> [{absoluteIndex + 1:00}] "
                    : $"  [{absoluteIndex + 1:00}] ";
                var line = TuiText.TruncateToWidth(prefix + _snapshot.EntriesById[visibleEntryIds[i]].SummaryLine, width, string.Empty, pad: true);
                lines.Add(absoluteIndex == _selectedIndex ? $"{HighlightStart}{line}{HighlightEnd}" : line);
            }

            while (lines.Count < bodyHeight)
            {
                lines.Add(new string(' ', width));
            }

            return lines;
        }

        private string BuildSectionHeader() =>
            _detailMode
                ? $"Entry Details ({CurrentEntry().EntryId})"
                : $"Latest Metadata ({_snapshot.VisibleEntryIds.Count} recent entries)";

        private string BuildFooter()
        {
            if (_detailMode)
            {
                var total = Math.Max(0, _lastWrappedDetailLineCount);
                if (total == 0)
                {
                    return "0 lines";
                }

                var start = Math.Min(total, _detailOffset + 1);
                var end = Math.Min(total, _detailOffset + Math.Max(1, _lastVisibleBodyLines));
                var currentEntryIndex = TryGetVisibleEntryIndex(CurrentEntry().EntryId);
                var browseLabel = currentEntryIndex is null
                    ? $" | entry off-list/{_snapshot.VisibleEntryIds.Count}"
                    : $" | entry {currentEntryIndex.Value + 1}/{_snapshot.VisibleEntryIds.Count}";
                var relationCount = CurrentEntry().Relations.Count;
                var relationHint = relationCount == 0
                    ? string.Empty
                    : $" | 1-{Math.Min(9, relationCount)} jump";
                var historyHint = _detailHistory.Count == 0
                    ? string.Empty
                    : $" | stack {_detailHistory.Count}";
                return _snapshot.FocusEntryId is null
                    ? $"{start}-{end}/{total}{browseLabel} | relations {relationCount}{historyHint} | h/l or Tab browse{relationHint} | Esc/Enter back"
                    : $"{start}-{end}/{total}{browseLabel} | relations {relationCount}{historyHint}{relationHint} | Esc/Enter close";
            }

            if (_snapshot.VisibleEntryIds.Count == 0)
            {
                return "0 entries";
            }

            return $"{_selectedIndex + 1}/{_snapshot.VisibleEntryIds.Count} | Enter inspect";
        }

        private CodingAgentTreeMetadataEntrySnapshot CurrentEntry()
        {
            var entryId = _detailEntryId ?? SelectedEntryId();
            return _snapshot.EntriesById[entryId];
        }

        private string SelectedEntryId() => _snapshot.VisibleEntryIds[_selectedIndex];

        private IReadOnlyList<string> BuildDetailLines()
        {
            var entry = CurrentEntry();
            var lines = new List<string>();
            lines.Add("Entry Summary");
            lines.Add($"  summary: {entry.SummaryLine}");
            lines.Add($"  state: {BuildEntryStateSummary(entry)}");
            if (_detailHistory.Count > 0)
            {
                lines.Add($"  jump stack: {_detailHistory.Count}");
            }

            lines.Add(string.Empty);
            lines.Add("Overview");
            lines.AddRange(entry.OverviewLines.Select(static line => $"  {line}"));
            if (entry.Relations.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"Relations ({entry.Relations.Count})");
                for (var i = 0; i < entry.Relations.Count; i++)
                {
                    var relation = entry.Relations[i];
                    lines.Add($"  [{i + 1}] {relation.Label} -> {relation.EntryId}");
                    var summary = TryGetRelationSummary(relation.EntryId);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        lines.Add($"      {summary}");
                    }
                }
            }
            else
            {
                lines.Add(string.Empty);
                lines.Add("Relations (0)");
                lines.Add("  none");
            }

            foreach (var section in entry.Sections)
            {
                lines.Add(string.Empty);
                lines.Add(section.Title);
                lines.AddRange(section.Lines.Select(static line => $"  {line}"));
            }

            return lines;
        }

        private bool TryHandleRelationJump(ConsoleKeyInfo key)
        {
            var relationIndex = KeyToRelationIndex(key);
            if (relationIndex < 0)
            {
                return false;
            }

            var entry = CurrentEntry();
            if (relationIndex >= entry.Relations.Count)
            {
                return false;
            }

            var targetId = entry.Relations[relationIndex].EntryId;
            if (!_snapshot.EntriesById.ContainsKey(targetId) || string.Equals(targetId, _detailEntryId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_detailEntryId))
            {
                _detailHistory.Push(_detailEntryId);
            }

            _detailEntryId = targetId;
            SyncSelectedIndex(_detailEntryId);
            _detailOffset = 0;
            return true;
        }

        private string BuildEntryStateSummary(CodingAgentTreeMetadataEntrySnapshot entry)
        {
            var type = TryGetOverviewValue(entry, "type") ?? "unknown";
            var path = TryGetOverviewValue(entry, "path") ?? "unknown";
            var visibility = TryGetVisibleEntryIndex(entry.EntryId) is { } index
                ? $"visible {index + 1}/{_snapshot.VisibleEntryIds.Count}"
                : $"off-list/{_snapshot.VisibleEntryIds.Count}";
            return $"type {type}, path {path}, relations {entry.Relations.Count}, sections {entry.Sections.Count}, {visibility}";
        }

        private string? TryGetRelationSummary(string entryId) =>
            _snapshot.EntriesById.TryGetValue(entryId, out var entry)
                ? entry.SummaryLine
                : null;

        private string? TryGetOverviewValue(CodingAgentTreeMetadataEntrySnapshot entry, string label)
        {
            foreach (var line in entry.OverviewLines)
            {
                if (!line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(label.Length + 1)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        private void SyncSelectedIndex(string? entryId)
        {
            var index = TryGetVisibleEntryIndex(entryId);
            if (index is not null)
            {
                _selectedIndex = index.Value;
            }
        }

        private string? ResolveCurrentSelectionEntryId()
        {
            if (_detailMode)
            {
                return _detailEntryId ?? (_snapshot.VisibleEntryIds.Count == 0 ? null : SelectedEntryId());
            }

            return _snapshot.VisibleEntryIds.Count == 0 ? null : SelectedEntryId();
        }

        private int? TryGetVisibleEntryIndex(string? entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return null;
            }

            return _snapshot.VisibleEntryIds
                .Select(static (id, index) => new { id, index })
                .FirstOrDefault(item => item.id.Equals(entryId, StringComparison.OrdinalIgnoreCase))
                ?.index;
        }

        private static int KeyToRelationIndex(ConsoleKeyInfo key) =>
            key.Key switch
            {
                ConsoleKey.D1 or ConsoleKey.NumPad1 when key.KeyChar is '1' or '\0' => 0,
                ConsoleKey.D2 or ConsoleKey.NumPad2 when key.KeyChar is '2' or '\0' => 1,
                ConsoleKey.D3 or ConsoleKey.NumPad3 when key.KeyChar is '3' or '\0' => 2,
                ConsoleKey.D4 or ConsoleKey.NumPad4 when key.KeyChar is '4' or '\0' => 3,
                ConsoleKey.D5 or ConsoleKey.NumPad5 when key.KeyChar is '5' or '\0' => 4,
                ConsoleKey.D6 or ConsoleKey.NumPad6 when key.KeyChar is '6' or '\0' => 5,
                ConsoleKey.D7 or ConsoleKey.NumPad7 when key.KeyChar is '7' or '\0' => 6,
                ConsoleKey.D8 or ConsoleKey.NumPad8 when key.KeyChar is '8' or '\0' => 7,
                ConsoleKey.D9 or ConsoleKey.NumPad9 when key.KeyChar is '9' or '\0' => 8,
                _ => -1
            };

        private static IReadOnlyList<string> RenderHighlightedLines(IEnumerable<string> lines, int width) =>
            lines.Select(line => TuiText.TruncateToWidth($"{HighlightStart}{line}{HighlightEnd}", width, string.Empty, pad: true)).ToArray();

        private static string FormatOptional(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
