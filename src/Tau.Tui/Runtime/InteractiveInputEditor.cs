using System.Globalization;
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class InteractiveInputEditor
{
    private readonly IConsoleKeyReader _reader;
    private readonly IInteractiveRenderer _renderer;
    private readonly InputHistory _history;
    private readonly InputBuffer _buffer;
    private readonly KillRing _killRing = new();
    private IKeyBindingMap _bindings;
    private IConsoleKeyReader? _inputReaderOverride;
    private IKeyBindingMap? _inputBindingsOverride;
    private IKeyBindingMap ActiveBindings => _inputBindingsOverride ?? _bindings;
    private ITuiAutocompleteProvider? _autocompleteProvider;
    private AutocompleteSession? _autocompleteSession;
    private Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? _shortcutHandler;
    private LastEditAction _lastEditAction = LastEditAction.None;
    private int _lastYankStart = -1;
    private string _lastYankedText = string.Empty;
    private readonly Dictionary<int, string> _pastes = new();
    private int _pasteCounter;
    private UndoStack _undoStack = new();
    private string _cursorDraft = string.Empty;
    private int _cursorIndex;

    public InteractiveInputEditor(
        IConsoleKeyReader reader,
        IInteractiveRenderer renderer,
        InputBuffer? buffer = null,
        InputHistory? history = null,
        IKeyBindingMap? bindings = null,
        ITuiAutocompleteProvider? autocompleteProvider = null)
    {
        _reader = reader;
        _renderer = renderer;
        _buffer = buffer ?? new InputBuffer();
        _history = history ?? new InputHistory();
        _bindings = bindings ?? KeyBindingMap.Default;
        _autocompleteProvider = autocompleteProvider;
    }

    public InputBuffer Buffer => _buffer;
    public InputHistory History => _history;
    public IKeyBindingMap KeyBindings => _bindings;

    public string GetCollapsedDraft() => _buffer.Draft;

    public string GetExpandedDraft() => ExpandPasteMarkers(_buffer.Draft);

    /// <summary>【Tui】【草稿交接】用完整文本替换草稿，并清除旧粘贴引用，避免再次展开旧标记。</summary>
    /// <param name="text">完整文本，空引用表示清空。</param><param name="cursorIndex">可选完整文本光标，默认位于末尾。</param>
    public void SetExpandedDraft(string? text, int? cursorIndex = null)
    {
        var normalized = NormalizeEditorText(text ?? string.Empty);
        if (!string.Equals(GetExpandedDraft(), normalized, StringComparison.Ordinal))
            _undoStack.Push(CaptureUndoState(new(_buffer.Draft), GetCollapsedCursorIndex()));
        _pastes.Clear();
        _pasteCounter = 0;
        StoreDraft(normalized, cursorIndex ?? normalized.Length);
        ResetTransientEditAction();
    }

    /// <summary>【Tui】【光标查询】返回展开草稿中的 UTF-16 偏移，用于应用层插入前后分隔符。</summary>
    /// <returns>完整文本中的插入位置。</returns>
    public int GetExpandedCursorIndex() => ExpandPasteMarkers(_buffer.Draft[..GetCollapsedCursorIndex()]).Length;

    /// <summary>【Tui】【程序插入】在当前光标插入完整文本，规范化换行与制表符，并形成一次可撤销操作。</summary>
    /// <param name="text">待插入文本；空文本不改变状态。</param>
    public void InsertTextAtCursor(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cursor = GetCollapsedCursorIndex();
        var previous = CaptureUndoState(new(_buffer.Draft), cursor);
        var normalized = NormalizeEditorText(text);
        var updated = _buffer.Draft.Insert(cursor, normalized);
        cursor += normalized.Length;
        _undoStack.PushIfChanged(previous, updated, cursor);
        StoreDraft(updated, cursor);
        ResetTransientEditAction();
    }

    /// <summary>【Tui】【草稿转移】取走完整草稿，同时释放本编辑器的粘贴、光标和撤销状态。</summary>
    /// <returns>转移前的完整文本。</returns>
    public string TakeDraft()
    {
        var text = GetExpandedDraft();
        _pastes.Clear();
        _pasteCounter = 0;
        _undoStack = new();
        StoreDraft(string.Empty, 0);
        ResetTransientEditAction();
        return text;
    }

    /// <summary>【Tui】【光标连续性】直接替换底层缓冲时回退末尾，正常应用动作保留原位置。</summary>
    /// <returns>折叠缓冲中的 UTF-16 偏移。</returns>
    private int GetCollapsedCursorIndex() => string.Equals(_cursorDraft, _buffer.Draft, StringComparison.Ordinal)
        ? Math.Clamp(_cursorIndex, 0, _buffer.Draft.Length) : _buffer.Draft.Length;

    /// <summary>【Tui】【编辑状态】同时保存文本与光标，供下次输入循环继续编辑。</summary>
    /// <param name="text">折叠文本。</param><param name="cursor">光标位置。</param>
    private void StoreDraft(string text, int cursor)
    {
        _buffer.SetDraft(text);
        _cursorDraft = text;
        _cursorIndex = Math.Clamp(cursor, 0, text.Length);
    }

    /// <summary>【Tui】【程序文本规范化】保持内容，仅统一换行和制表符，与程序设置及插入共用。</summary>
    /// <param name="text">原始文本。</param><returns>规范化文本。</returns>
    private static string NormalizeEditorText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);

    public void SetKeyBindings(IKeyBindingMap bindings)
    {
        _bindings = bindings;
    }

    public void SetAutocompleteProvider(ITuiAutocompleteProvider? autocompleteProvider)
    {
        _autocompleteProvider = autocompleteProvider;
        _autocompleteSession = null;
    }

    public void SetShortcutHandler(Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? shortcutHandler)
    {
        _shortcutHandler = shortcutHandler;
    }

    /// <summary>【Tui】【输入阶段】临时使用阶段读取器和键位，保留当前编辑器的历史、撤销、剪切及光标状态。</summary>
    /// <param name="reader">当前阶段读取器。</param><param name="bindings">当前阶段键位。</param>
    /// <param name="prompt">提示符。</param><param name="promptColor">提示颜色。</param><param name="cancellationToken">阶段取消信号。</param>
    /// <returns>当前输入结果，结束时恢复此前的读取器和键位。</returns>
    public async Task<InputResult> ReadLineWithInputAsync(IConsoleKeyReader reader, IKeyBindingMap bindings,
        string prompt, ConsoleColor? promptColor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(bindings);
        var previousReader = _inputReaderOverride; var previousBindings = _inputBindingsOverride;
        _inputReaderOverride = reader; _inputBindingsOverride = bindings;
        try { return await ReadLineAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false); }
        finally { _inputReaderOverride = previousReader; _inputBindingsOverride = previousBindings; }
    }

    /// <summary>【Tui】【临时询问】用独立状态读取模态问题，不把草稿、撤销或临时回答混入主编辑器。</summary>
    /// <param name="prompt">询问提示符。</param><param name="promptColor">提示颜色。</param><param name="cancellationToken">询问取消信号。</param>
    /// <returns>询问结果。</returns>
    public Task<InputResult> ReadPromptAsync(string prompt, ConsoleColor? promptColor = null, CancellationToken cancellationToken = default) =>
        new InteractiveInputEditor(_reader, _renderer, bindings: _bindings).ReadLineAsync(prompt, promptColor, cancellationToken);

    /// <summary>【Tui】【输入循环】编辑当前草稿直到提交、取消或应用动作，跨动作返回保留折叠粘贴引用。</summary>
    /// <param name="prompt">提示符。</param><param name="promptColor">提示颜色。</param>
    /// <param name="cancellationToken">读取取消信号。</param><returns>输入结果或应用动作。</returns>
    public async Task<InputResult> ReadLineAsync(
        string prompt,
        ConsoleColor? promptColor = null,
        CancellationToken cancellationToken = default)
    {
        _renderer.WritePrompt(prompt, promptColor);
        var chars = new List<char>(_buffer.Draft);
        var cursor = GetCollapsedCursorIndex();
        var historyOffset = -1;
        int? preferredVerticalColumn = null;
        var undoStack = _undoStack;
        ResetTransientEditAction();
        RenderDraft(chars, cursor);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputEvent = await ReadInputEventAsync(cancellationToken).ConfigureAwait(false);
            if (inputEvent.Kind == ConsoleInputEventKind.Paste)
            {
                var undoState = CaptureUndoState(chars, cursor);
                if (TryInsertPastedText(chars, ref cursor, inputEvent.PasteText ?? string.Empty))
                {
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    RenderDraft(chars, cursor);
                }

                continue;
            }

            var key = inputEvent.Key;
            if (_shortcutHandler is not null &&
                await _shortcutHandler(key, cancellationToken).ConfigureAwait(false))
            {
                RenderDraft(chars, cursor);
                continue;
            }

            var action = ActiveBindings.ResolveInput(inputEvent, chars.Count > 0);
            // 1. 【Tui】【补全退出】中断键优先关闭补全，保留输入文本，不触发应用层退出或双击动作
            if (action == EditorAction.Interrupt && _autocompleteSession is not null)
            {
                _autocompleteSession = null;
                RenderDraft(chars, cursor);
                continue;
            }

            if (ShouldInsertNewLine(key, action))
            {
                var undoState = CaptureUndoState(chars, cursor);
                InsertNewLine(chars, ref cursor);
                PushUndoIfChanged(undoStack, undoState, chars, cursor);
                historyOffset = -1;
                preferredVerticalColumn = null;
                ResetTransientEditAction();
                RenderDraft(chars, cursor);
                continue;
            }

            if ((action == EditorAction.Undo || action == EditorAction.None && ActiveBindings.UseLegacyShortcuts && IsUndoKey(key)) &&
                TryApplyUndo(undoStack, ref chars, ref cursor))
            {
                historyOffset = -1;
                preferredVerticalColumn = null;
                ResetTransientEditAction();
                RenderDraft(chars, cursor);
                continue;
            }

            if (action is EditorAction.Yank or EditorAction.YankPop || action == EditorAction.None && ActiveBindings.UseLegacyShortcuts)
            {
                var undoState = CaptureUndoState(chars, cursor);
                var yank = action == EditorAction.Yank || action == EditorAction.None && IsYankKey(key);
                var yankPop = action == EditorAction.YankPop || action == EditorAction.None && IsYankPopKey(key);
                if (TryHandleYankAction(yank, yankPop, chars, ref cursor))
                {
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    RenderDraft(chars, cursor);
                    continue;
                }
            }

            switch (action)
            {
                case EditorAction.Ignore:
                    break;
                case EditorAction.Cancel:
                case EditorAction.ExitIfEmpty:
                    _renderer.Cancel();
                    _buffer.SetDraft(ExpandPasteMarkers(new string(chars.ToArray())));
                    _pastes.Clear();
                    _pasteCounter = 0;
                    ResetTransientEditAction();
                    return InputResult.Cancelled;
                case EditorAction.NewLine:
                {
                    var undoState = CaptureUndoState(chars, cursor);
                    InsertNewLine(chars, ref cursor);
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    break;
                }
                case EditorAction.Submit:
                {
                    if (key.Key == ConsoleKey.Enter &&
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control | ConsoleModifiers.Shift)) == 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        if (TryInsertNewLineForSubmitFallback(chars, ref cursor))
                        {
                            PushUndoIfChanged(undoStack, undoState, chars, cursor);
                            historyOffset = -1;
                            preferredVerticalColumn = null;
                            ResetTransientEditAction();
                            break;
                        }
                    }

                    var committed = ExpandPasteMarkers(new string(chars.ToArray()));
                    _renderer.Commit();
                    _buffer.SetDraft(committed);
                    _buffer.Commit();
                    _undoStack = new();
                    _pastes.Clear();
                    _pasteCounter = 0;
                    ResetTransientEditAction();
                    if (!string.IsNullOrWhiteSpace(committed))
                    {
                        _history.Add(committed);
                    }
                    return InputResult.Submitted(committed);
                }
                case EditorAction.ReverseSearch:
                {
                    var searchOutcome = await RunReverseSearchAsync(chars, cancellationToken).ConfigureAwait(false);
                    chars = new List<char>(searchOutcome.Buffer);
                    cursor = chars.Count;
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (searchOutcome.Submit)
                    {
                        var committed = ExpandPasteMarkers(new string(chars.ToArray()));
                        _renderer.Commit();
                        SetExpandedDraft(committed);
                        _buffer.Commit();
                        _undoStack = new();
                        ResetTransientEditAction();
                        if (!string.IsNullOrWhiteSpace(committed))
                        {
                            _history.Add(committed);
                        }
                        return InputResult.Submitted(committed);
                    }

                    _renderer.WritePrompt(prompt, promptColor);
                    RenderDraft(chars, cursor);
                    continue;
                }
                case EditorAction.Complete:
                case EditorAction.CompletePrevious:
                {
                    var undoState = CaptureUndoState(chars, cursor);
                    var completed = await TryApplyAutocompleteAsync(
                            chars,
                            cursor,
                            action == EditorAction.CompletePrevious ? -1 : 1,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (completed is not null)
                    {
                        chars = new List<char>(completed.Text);
                        cursor = completed.CursorIndex;
                        if (completed.StartedSession)
                        {
                            PushUndoIfChanged(undoStack, undoState, chars, cursor);
                        }

                        historyOffset = -1;
                        preferredVerticalColumn = null;
                        ResetTransientEditAction(preserveAutocomplete: true);
                    }

                    break;
                }
                case EditorAction.CycleModelForward:
                case EditorAction.CycleModelBackward:
                case EditorAction.SelectModel:
                case EditorAction.PasteImage:
                case EditorAction.ToggleThinkingBlock:
                case EditorAction.ToggleToolOutputExpansion:
                case EditorAction.OpenExternalEditor:
                case EditorAction.QueueFollowUpMessage:
                case EditorAction.RestoreQueuedMessages:
                case EditorAction.ClearEditor:
                case EditorAction.Interrupt:
                case EditorAction.CycleThinkingLevel:
                case EditorAction.CopyLastMessage:
                case EditorAction.NewSession:
                case EditorAction.OpenSessionTree:
                case EditorAction.ForkSession:
                case EditorAction.ResumeSession:
                {
                    var draft = new string(chars.ToArray());
                    _renderer.Commit();
                    _buffer.SetDraft(draft);
                    ResetTransientEditAction();
                    return InputResult.ForAction(action);
                }
                case EditorAction.KillToLineEnd:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var lineEnd = FindLineEnd(chars, cursor);
                    var changed = false;
                    if (cursor < lineEnd)
                    {
                        var killedText = SliceChars(chars, cursor, lineEnd - cursor);
                        chars.RemoveRange(cursor, lineEnd - cursor);
                        PushKill(killedText, prepend: false);
                        changed = true;
                    }
                    else if (cursor < chars.Count && chars[cursor] == '\n')
                    {
                        chars.RemoveAt(cursor);
                        PushKill("\n", prepend: false);
                        changed = true;
                    }

                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.KillToLineStart:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var lineStart = FindLineStart(chars, cursor);
                    var changed = false;
                    if (cursor > lineStart)
                    {
                        var killedText = SliceChars(chars, lineStart, cursor - lineStart);
                        chars.RemoveRange(lineStart, cursor - lineStart);
                        cursor = lineStart;
                        PushKill(killedText, prepend: true);
                        changed = true;
                    }
                    else if (cursor > 0 && chars[cursor - 1] == '\n')
                    {
                        chars.RemoveAt(cursor - 1);
                        cursor--;
                        PushKill("\n", prepend: true);
                        changed = true;
                    }

                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.DeletePrevChar:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    var deletePrevCharUndoState = CaptureUndoState(chars, cursor);
                    if (cursor > 0)
                    {
                        var previousStart = FindPreviousInputSegmentStart(chars, cursor);
                        chars.RemoveRange(previousStart, cursor - previousStart);
                        cursor = previousStart;
                    }
                    PushUndoIfChanged(undoStack, deletePrevCharUndoState, chars, cursor);
                    break;
                case EditorAction.DeletePrevWord:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var newCursor = cursor > 0 && chars[cursor - 1] == '\n'
                        ? cursor - 1
                        : FindPreviousWordBoundary(chars, cursor, ValidPasteMarkerSpans(chars));
                    var changed = false;
                    if (newCursor < cursor)
                    {
                        var killedText = SliceChars(chars, newCursor, cursor - newCursor);
                        chars.RemoveRange(newCursor, cursor - newCursor);
                        cursor = newCursor;
                        PushKill(killedText, prepend: true);
                        changed = true;
                    }
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.DeleteNextChar:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    var deleteNextCharUndoState = CaptureUndoState(chars, cursor);
                    if (cursor < chars.Count)
                    {
                        var nextEnd = FindNextInputSegmentEnd(chars, cursor);
                        chars.RemoveRange(cursor, nextEnd - cursor);
                    }
                    PushUndoIfChanged(undoStack, deleteNextCharUndoState, chars, cursor);
                    break;
                case EditorAction.DeleteNextWord:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var nextBoundary = cursor < chars.Count && chars[cursor] == '\n'
                        ? cursor + 1
                        : FindNextWordBoundary(chars, cursor, ValidPasteMarkerSpans(chars));
                    var changed = false;
                    if (nextBoundary > cursor)
                    {
                        var killedText = SliceChars(chars, cursor, nextBoundary - cursor);
                        chars.RemoveRange(cursor, nextBoundary - cursor);
                        PushKill(killedText, prepend: false);
                        changed = true;
                    }
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.CursorLeft:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (cursor > 0) cursor = FindPreviousInputSegmentStart(chars, cursor);
                    break;
                case EditorAction.CursorRight:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (cursor < chars.Count) cursor = FindNextInputSegmentEnd(chars, cursor);
                    break;
                case EditorAction.CursorPrevWord:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = cursor > 0 && chars[cursor - 1] == '\n'
                        ? cursor - 1
                        : FindPreviousWordBoundary(chars, cursor, ValidPasteMarkerSpans(chars));
                    break;
                case EditorAction.CursorNextWord:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = cursor < chars.Count && chars[cursor] == '\n'
                        ? cursor + 1
                        : FindNextWordBoundary(chars, cursor, ValidPasteMarkerSpans(chars));
                    break;
                case EditorAction.CursorLineStart:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = FindLineStart(chars, cursor);
                    break;
                case EditorAction.CursorLineEnd:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = FindLineEnd(chars, cursor);
                    break;
                case EditorAction.HistoryPrev:
                case EditorAction.PromptHistoryPrevious:
                {
                    ResetTransientEditAction();
                    if (action == EditorAction.HistoryPrev && ContainsLineBreak(chars))
                    {
                        if (TryMoveCursorVertically(chars, cursor, -1, ref preferredVerticalColumn, out var verticalCursor))
                        {
                            cursor = verticalCursor;
                        }
                        break;
                    }

                    preferredVerticalColumn = null;
                    var snapshot = _history.Peek(historyOffset + 1);
                    if (snapshot is not null)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset++;
                        chars.Clear();
                        chars.AddRange(snapshot);
                        cursor = chars.Count;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
                }
                case EditorAction.HistoryNext:
                case EditorAction.PromptHistoryNext:
                {
                    ResetTransientEditAction();
                    if (action == EditorAction.HistoryNext && ContainsLineBreak(chars))
                    {
                        if (TryMoveCursorVertically(chars, cursor, 1, ref preferredVerticalColumn, out var verticalCursor))
                        {
                            cursor = verticalCursor;
                        }
                        break;
                    }

                    preferredVerticalColumn = null;
                    if (historyOffset > 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset--;
                        var snapshot = _history.Peek(historyOffset);
                        chars.Clear();
                        if (snapshot is not null)
                        {
                            chars.AddRange(snapshot);
                        }

                        cursor = chars.Count;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    else if (historyOffset == 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset = -1;
                        chars.Clear();
                        cursor = 0;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
                }
                default:
                    var insertedText = inputEvent.Text ?? (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) &&
                        (key.Modifiers & ConsoleModifiers.Control) == 0 ? key.KeyChar.ToString() : null);
                    if (!string.IsNullOrEmpty(insertedText))
                    {
                        preferredVerticalColumn = null;
                        ResetTransientEditAction();
                        var undoState = CaptureUndoState(chars, cursor);
                        chars.InsertRange(cursor, insertedText);
                        cursor += insertedText.Length;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
            }

            RenderDraft(chars, cursor);
        }
    }

    private void RenderDraft(IReadOnlyList<char> chars, int cursor)
    {
        var collapsed = new string(chars.ToArray());
        StoreDraft(collapsed, cursor);
        var batch = (_renderer as IInteractiveRenderBatch)?.BeginRenderBatch();
        try
        {
            if (_renderer is IInteractiveAutocompleteRenderer autocompleteRenderer)
            {
                var session = _autocompleteSession;
                autocompleteRenderer.RenderAutocomplete(
                    session?.Items ?? [],
                    session?.SelectedIndex ?? -1);
            }

            _renderer.Render(collapsed, Math.Clamp(cursor, 0, chars.Count));
        }
        finally
        {
            batch?.Dispose();
        }
    }

    /// <summary>【Tui】【撤销动作】恢复上一编辑状态，没有历史时仍消费撤销动作。</summary>
    /// <param name="undoStack">撤销栈。</param><param name="chars">可替换的当前文本。</param><param name="cursor">恢复后的光标。</param><returns>动作已处理。</returns>
    private bool TryApplyUndo(
        UndoStack undoStack,
        ref List<char> chars,
        ref int cursor)
    {
        if (undoStack.TryPop(out var state) && state is not null)
        {
            chars = new List<char>(state.Text);
            cursor = Math.Clamp(state.Cursor, 0, chars.Count);
            _pastes.Clear();
            foreach (var (id, text) in state.Pastes) _pastes.Add(id, text);
            _pasteCounter = state.PasteCounter;
        }

        return true;
    }

    private async ValueTask<AutocompleteApplyResult?> TryApplyAutocompleteAsync(
        IReadOnlyList<char> chars,
        int cursor,
        int direction,
        CancellationToken cancellationToken)
    {
        if (_autocompleteProvider is null)
        {
            return null;
        }

        if (_autocompleteSession is { } activeSession && activeSession.Items.Count > 0)
        {
            var nextIndex = WrapAutocompleteIndex(activeSession.SelectedIndex + direction, activeSession.Items.Count);
            return ApplyAutocompleteSession(activeSession with { SelectedIndex = nextIndex }, startedSession: false);
        }

        var text = new string(chars.ToArray());
        var suggestions = await _autocompleteProvider
            .GetSuggestionsAsync(text, cursor, force: false, cancellationToken)
            .ConfigureAwait(false);
        suggestions ??= await _autocompleteProvider
            .GetSuggestionsAsync(text, cursor, force: true, cancellationToken)
            .ConfigureAwait(false);
        if (suggestions is null || suggestions.Items.Count == 0)
        {
            return null;
        }

        var selectedIndex = direction < 0 ? suggestions.Items.Count - 1 : 0;
        var session = new AutocompleteSession(
            text,
            Math.Clamp(cursor, 0, text.Length),
            suggestions.Prefix,
            suggestions.Items.ToArray(),
            SelectedIndex: selectedIndex);
        return ApplyAutocompleteSession(session, startedSession: true);
    }

    private static int WrapAutocompleteIndex(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    private AutocompleteApplyResult ApplyAutocompleteSession(
        AutocompleteSession session,
        bool startedSession)
    {
        var item = session.Items[session.SelectedIndex];
        var completed = _autocompleteProvider!.ApplyCompletion(
            session.OriginalText,
            session.OriginalCursor,
            item,
            session.Prefix);
        _autocompleteSession = session;
        return new AutocompleteApplyResult(
            completed.Text,
            Math.Clamp(completed.CursorIndex, 0, completed.Text.Length),
            startedSession);
    }

    private static bool IsUndoKey(ConsoleKeyInfo key)
    {
        var modifiers = key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift);
        if (key.Key == ConsoleKey.Z && modifiers == ConsoleModifiers.Control)
        {
            return true;
        }

        if (key.KeyChar == '\x1F' && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            return true;
        }

        return key.Key == ConsoleKey.OemMinus &&
            modifiers == (ConsoleModifiers.Control | ConsoleModifiers.Shift);
    }

    /// <summary>【Tui】【撤销快照】同时捕获文本、光标和粘贴表，程序替换后仍能恢复完整原文。</summary>
    /// <param name="chars">当前文本。</param><param name="cursor">当前光标。</param><returns>独立编辑快照。</returns>
    private InputEditorState CaptureUndoState(List<char> chars, int cursor) =>
        new(new string(chars.ToArray()), Math.Clamp(cursor, 0, chars.Count), new Dictionary<int, string>(_pastes), _pasteCounter);

    private static void PushUndoIfChanged(
        UndoStack undoStack,
        InputEditorState previous,
        List<char> chars,
        int cursor)
    {
        undoStack.PushIfChanged(previous, new string(chars.ToArray()), Math.Clamp(cursor, 0, chars.Count));
    }

    private async ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken)
    {
        var reader = _inputReaderOverride ?? _reader;
        if (reader is IConsoleInputEventReader eventReader)
        {
            return await eventReader.ReadInputEventAsync(cancellationToken).ConfigureAwait(false);
        }

        var key = await reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        return ConsoleInputEvent.KeyPress(key);
    }

    private bool TryInsertPastedText(List<char> chars, ref int cursor, string text)
    {
        var filteredText = NormalizePastedText(text);
        if (filteredText.Length == 0)
        {
            return false;
        }

        if (filteredText.Length > 0 && filteredText[0] is '/' or '~' or '.')
        {
            var previous = cursor > 0 ? chars[cursor - 1] : '\0';
            if (previous != '\0' && (char.IsLetterOrDigit(previous) || previous == '_'))
            {
                filteredText = " " + filteredText;
            }
        }

        var pastedLines = filteredText.Split('\n');
        var textToInsert = filteredText;
        if (pastedLines.Length > 10 || filteredText.Length > 1000)
        {
            _pasteCounter++;
            var pasteId = _pasteCounter;
            _pastes[pasteId] = filteredText;
            textToInsert = pastedLines.Length > 10
                ? $"[paste #{pasteId} +{pastedLines.Length} lines]"
                : $"[paste #{pasteId} {filteredText.Length} chars]";
        }

        chars.InsertRange(cursor, textToInsert);
        cursor += textToInsert.Length;
        ClearAutocompleteSession();
        return true;
    }

    private static string NormalizePastedText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decoded = DecodePastedControlSequences(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", "    ", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(decoded.Length);
        foreach (var ch in decoded)
        {
            if (ch == '\n' || ch >= 32)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string DecodePastedControlSequences(string text)
    {
        const string prefix = "\u001b[";
        const string suffix = ";5u";
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf(prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);
            var end = text.IndexOf(suffix, start + prefix.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            var codeText = text[(start + prefix.Length)..end];
            if (int.TryParse(codeText, CultureInfo.InvariantCulture, out var code) &&
                ((code is >= 97 and <= 122) || (code is >= 65 and <= 90)))
            {
                builder.Append((char)(char.ToLowerInvariant((char)code) - 96));
                index = end + suffix.Length;
                continue;
            }

            builder.Append(text, start, end + suffix.Length - start);
            index = end + suffix.Length;
        }

        return builder.ToString();
    }

    private string ExpandPasteMarkers(string text)
    {
        if (_pastes.Count == 0 || string.IsNullOrEmpty(text) || !text.Contains("[paste #", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (var (pasteId, pasteText) in _pastes)
        {
            var lineMarker = $"[paste #{pasteId} +";
            var charMarker = $"[paste #{pasteId} ";
            var index = 0;
            while (index < text.Length)
            {
                var markerStart = text.IndexOf($"[paste #{pasteId} ", index, StringComparison.Ordinal);
                if (markerStart < 0)
                {
                    break;
                }

                var markerEnd = text.IndexOf(']', markerStart);
                if (markerEnd < 0)
                {
                    break;
                }

                var marker = text[markerStart..(markerEnd + 1)];
                if ((marker.StartsWith(lineMarker, StringComparison.Ordinal) &&
                     marker.EndsWith(" lines]", StringComparison.Ordinal)) ||
                    (marker.StartsWith(charMarker, StringComparison.Ordinal) &&
                     marker.EndsWith(" chars]", StringComparison.Ordinal)))
                {
                    text = text[..markerStart] + pasteText + text[(markerEnd + 1)..];
                    index = markerStart + pasteText.Length;
                    continue;
                }

                index = markerEnd + 1;
            }
        }

        return text;
    }

    /// <summary>【Tui】【取回动作】取回最近删除内容，或轮换前一次取回使用的删除环条目。</summary>
    /// <param name="yank">是否普通取回。</param><param name="yankPop">是否轮换取回。</param><param name="chars">当前文本。</param><param name="cursor">插入光标。</param><returns>是否处理动作。</returns>
    private bool TryHandleYankAction(bool yank, bool yankPop, List<char> chars, ref int cursor)
    {
        if (yank)
        {
            var text = _killRing.Peek();
            if (text is null)
            {
                return true;
            }

            InsertYankedText(chars, ref cursor, text);
            return true;
        }

        if (!yankPop)
        {
            return false;
        }

        if (_lastEditAction != LastEditAction.Yank || _killRing.Count <= 1 || _lastYankedText.Length == 0)
        {
            return true;
        }

        if (_lastYankStart < 0 || _lastYankStart + _lastYankedText.Length > chars.Count)
        {
            ResetTransientEditAction();
            return true;
        }

        chars.RemoveRange(_lastYankStart, _lastYankedText.Length);
        cursor = _lastYankStart;
        _killRing.Rotate();

        var replacement = _killRing.Peek();
        if (replacement is null)
        {
            ResetTransientEditAction();
            return true;
        }

        InsertYankedText(chars, ref cursor, replacement);
        return true;
    }

    private static bool IsYankKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Y &&
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift))
            == ConsoleModifiers.Control;

    private static bool IsYankPopKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Y &&
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift))
            == ConsoleModifiers.Alt;

    private void PushKill(string text, bool prepend)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _killRing.Push(text, prepend: prepend, accumulate: _lastEditAction == LastEditAction.Kill);
        _lastEditAction = LastEditAction.Kill;
        _lastYankStart = -1;
        _lastYankedText = string.Empty;
    }

    private void InsertYankedText(List<char> chars, ref int cursor, string text)
    {
        var start = cursor;
        chars.InsertRange(cursor, text);
        cursor += text.Length;
        ClearAutocompleteSession();
        _lastEditAction = LastEditAction.Yank;
        _lastYankStart = start;
        _lastYankedText = text;
    }

    private void ClearAutocompleteSession()
    {
        _autocompleteSession = null;
    }

    private void ResetTransientEditAction(bool preserveAutocomplete = false)
    {
        _lastEditAction = LastEditAction.None;
        _lastYankStart = -1;
        _lastYankedText = string.Empty;
        if (!preserveAutocomplete)
        {
            ClearAutocompleteSession();
        }
    }

    private bool ShouldInsertNewLine(ConsoleKeyInfo key, EditorAction action)
    {
        if (key.Key != ConsoleKey.Enter)
        {
            return false;
        }

        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            return false;
        }

        if ((key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == 0)
        {
            return false;
        }

        if (action != EditorAction.None)
        {
            return false;
        }

        return ActiveBindings.Resolve(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: false, alt: false, control: false))
            == EditorAction.Submit;
    }

    private static void InsertNewLine(List<char> chars, ref int cursor)
    {
        chars.Insert(cursor, '\n');
        cursor++;
    }

    private static bool TryInsertNewLineForSubmitFallback(List<char> chars, ref int cursor)
    {
        if (cursor <= 0 || chars[cursor - 1] != '\\')
        {
            return false;
        }

        chars.RemoveAt(cursor - 1);
        cursor--;
        InsertNewLine(chars, ref cursor);
        return true;
    }

    private static string SliceChars(List<char> chars, int start, int length) =>
        length <= 0 ? string.Empty : new string(chars.GetRange(start, length).ToArray());

    private int FindPreviousInputSegmentStart(IReadOnlyList<char> chars, int cursor)
    {
        if (TryFindPasteMarkerSpanBeforeOrAtCursor(chars, cursor, out var span))
        {
            return span.Start;
        }

        return FindPreviousTextElementStart(chars, cursor);
    }

    private int FindNextInputSegmentEnd(IReadOnlyList<char> chars, int cursor)
    {
        if (TryFindPasteMarkerSpanAtOrAfterCursor(chars, cursor, out var span))
        {
            return span.End;
        }

        return FindNextTextElementEnd(chars, cursor);
    }

    private bool TryFindPasteMarkerSpanBeforeOrAtCursor(
        IReadOnlyList<char> chars,
        int cursor,
        out PasteMarkerSpan span)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        foreach (var candidate in ValidPasteMarkerSpans(chars))
        {
            if (candidate.Start < cursor && cursor <= candidate.End)
            {
                span = candidate;
                return true;
            }
        }

        span = default;
        return false;
    }

    private bool TryFindPasteMarkerSpanAtOrAfterCursor(
        IReadOnlyList<char> chars,
        int cursor,
        out PasteMarkerSpan span)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        foreach (var candidate in ValidPasteMarkerSpans(chars))
        {
            if (candidate.Start <= cursor && cursor < candidate.End)
            {
                span = candidate;
                return true;
            }
        }

        span = default;
        return false;
    }

    private List<PasteMarkerSpan> ValidPasteMarkerSpans(IReadOnlyList<char> chars)
    {
        if (_pastes.Count == 0 || chars.Count == 0)
        {
            return [];
        }

        var text = new string(chars.ToArray());
        if (!text.Contains("[paste #", StringComparison.Ordinal))
        {
            return [];
        }

        var spans = new List<PasteMarkerSpan>();
        foreach (var pasteId in _pastes.Keys)
        {
            var index = 0;
            var prefix = $"[paste #{pasteId} ";
            while (index < text.Length)
            {
                var start = text.IndexOf(prefix, index, StringComparison.Ordinal);
                if (start < 0)
                {
                    break;
                }

                var end = text.IndexOf(']', start);
                if (end < 0)
                {
                    break;
                }

                var marker = text[start..(end + 1)];
                if (IsPasteMarkerText(marker, pasteId))
                {
                    spans.Add(new PasteMarkerSpan(start, end + 1));
                }

                index = end + 1;
            }
        }

        spans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return spans;
    }

    private static bool IsPasteMarkerText(string marker, int pasteId)
    {
        var prefix = $"[paste #{pasteId} ";
        if (!marker.StartsWith(prefix, StringComparison.Ordinal) ||
            !marker.EndsWith(']'))
        {
            return false;
        }

        var payload = marker[prefix.Length..^1];
        if (payload.StartsWith('+') &&
            payload.EndsWith(" lines", StringComparison.Ordinal) &&
            int.TryParse(payload[1..^" lines".Length], CultureInfo.InvariantCulture, out var lineCount))
        {
            return lineCount > 0;
        }

        if (payload.EndsWith(" chars", StringComparison.Ordinal) &&
            int.TryParse(payload[..^" chars".Length], CultureInfo.InvariantCulture, out var charCount))
        {
            return charCount > 0;
        }

        return false;
    }

    private static int FindPreviousTextElementStart(IReadOnlyList<char> chars, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor <= 0)
        {
            return 0;
        }

        var text = new string(chars.ToArray());
        var previousStart = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            if (elementStart >= cursor)
            {
                break;
            }

            previousStart = elementStart;
        }

        return previousStart;
    }

    private static int FindNextTextElementEnd(IReadOnlyList<char> chars, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor >= chars.Count)
        {
            return chars.Count;
        }

        var text = new string(chars.ToArray());
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            var element = enumerator.GetTextElement();
            var elementEnd = elementStart + element.Length;
            if (cursor <= elementStart || cursor < elementEnd)
            {
                return Math.Min(chars.Count, elementEnd);
            }
        }

        return chars.Count;
    }

    private async Task<ReverseSearchOutcome> RunReverseSearchAsync(
        List<char> startingBuffer,
        CancellationToken cancellationToken)
    {
        var originalBuffer = startingBuffer.ToArray();
        var pattern = new List<char>();
        var offset = 0;
        var currentMatch = (Match: (string?)null, Offset: 0, Index: 0);

        _renderer.RenderSearch(string.Empty, null, 0);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = await ReadInputEventAsync(cancellationToken).ConfigureAwait(false);
            var key = input.Key;

            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.G)
            {
                return new ReverseSearchOutcome(originalBuffer, Submit: false);
            }

            if (key.Key == ConsoleKey.Escape)
            {
                return new ReverseSearchOutcome(originalBuffer, Submit: false);
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var buffer = currentMatch.Match is null ? originalBuffer : currentMatch.Match.ToCharArray();
                return new ReverseSearchOutcome(buffer, Submit: true);
            }

            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.R)
            {
                if (currentMatch.Match is not null)
                {
                    offset = currentMatch.Offset + 1;
                }
                ApplyMatch();
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (pattern.Count > 0)
                {
                    var start = FindPreviousTextElementStart(pattern, pattern.Count);
                    pattern.RemoveRange(start, pattern.Count - start);
                }
                offset = 0;
                ApplyMatch();
                continue;
            }

            var searchText = input.Text ?? (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) ? key.KeyChar.ToString() : null);
            if (!string.IsNullOrEmpty(searchText))
            {
                pattern.AddRange(searchText);
                offset = 0;
                ApplyMatch();
                continue;
            }
        }

        void ApplyMatch()
        {
            var query = new string(pattern.ToArray());
            if (query.Length == 0)
            {
                currentMatch = (null, 0, 0);
                _renderer.RenderSearch(query, null, 0);
                return;
            }

            var found = _history.FindContaining(query, offset);
            if (found is null)
            {
                currentMatch = (null, offset, 0);
                _renderer.RenderSearch(query, null, 0);
                return;
            }

            currentMatch = (found.Value.Match, found.Value.OffsetFromEnd, found.Value.MatchIndex);
            _renderer.RenderSearch(query, found.Value.Match, found.Value.MatchIndex);
        }
    }

    private readonly record struct ReverseSearchOutcome(char[] Buffer, bool Submit);

    private sealed record AutocompleteApplyResult(string Text, int CursorIndex, bool StartedSession);

    private sealed record AutocompleteSession(
        string OriginalText,
        int OriginalCursor,
        string Prefix,
        IReadOnlyList<TuiAutocompleteItem> Items,
        int SelectedIndex);

    private sealed record InputEditorState(string Text, int Cursor, IReadOnlyDictionary<int, string> Pastes, int PasteCounter);

    private sealed class UndoStack
    {
        private readonly List<InputEditorState> _entries = [];

        /// <summary>【Tui】【程序撤销】直接保存已确认内容发生变化的快照，允许展开内容与标记文字不同但显示相同。</summary>
        /// <param name="state">需要恢复的完整状态。</param>
        public void Push(InputEditorState state) => _entries.Add(state);

        public void PushIfChanged(InputEditorState previous, string currentText, int currentCursor)
        {
            if (string.Equals(previous.Text, currentText, StringComparison.Ordinal) &&
                previous.Cursor == currentCursor)
            {
                return;
            }

            _entries.Add(previous);
        }

        public bool TryPop(out InputEditorState? state)
        {
            if (_entries.Count == 0)
            {
                state = null;
                return false;
            }

            state = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            return true;
        }
    }

    private enum LastEditAction
    {
        None,
        Kill,
        Yank,
    }

    private sealed class KillRing
    {
        private readonly List<string> _entries = [];

        public int Count => _entries.Count;

        public void Push(string text, bool prepend, bool accumulate)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (accumulate && _entries.Count > 0)
            {
                _entries[^1] = prepend ? text + _entries[^1] : _entries[^1] + text;
                return;
            }

            _entries.Add(text);
        }

        public string? Peek() => _entries.Count == 0 ? null : _entries[^1];

        public void Rotate()
        {
            if (_entries.Count <= 1)
            {
                return;
            }

            var newest = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            _entries.Insert(0, newest);
        }
    }

    internal static int FindLineStart(IReadOnlyList<char> chars, int cursor)
    {
        var index = Math.Clamp(cursor, 0, chars.Count);
        while (index > 0 && chars[index - 1] != '\n')
        {
            index--;
        }

        return index;
    }

    internal static int FindLineEnd(IReadOnlyList<char> chars, int cursor)
    {
        var index = Math.Clamp(cursor, 0, chars.Count);
        while (index < chars.Count && chars[index] != '\n')
        {
            index++;
        }

        return index;
    }

    private static bool ContainsLineBreak(IReadOnlyList<char> chars)
    {
        for (var i = 0; i < chars.Count; i++)
        {
            if (chars[i] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMoveCursorVertically(
        IReadOnlyList<char> chars,
        int cursor,
        int direction,
        ref int? preferredColumn,
        out int newCursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        var currentStart = FindLineStart(chars, cursor);
        var currentEnd = FindLineEnd(chars, cursor);
        var column = cursor - currentStart;
        var desiredColumn = preferredColumn ?? column;

        if (direction < 0)
        {
            if (currentStart == 0)
            {
                newCursor = cursor;
                return false;
            }

            var previousEnd = currentStart - 1;
            var previousStart = FindLineStart(chars, previousEnd);
            var previousLength = previousEnd - previousStart;
            if (previousLength < desiredColumn)
            {
                preferredColumn ??= column;
                newCursor = previousEnd;
            }
            else
            {
                preferredColumn = null;
                newCursor = previousStart + desiredColumn;
            }
            return true;
        }

        if (currentEnd >= chars.Count)
        {
            newCursor = cursor;
            return false;
        }

        var nextStart = currentEnd + 1;
        var nextEnd = FindLineEnd(chars, nextStart);
        var nextLength = nextEnd - nextStart;
        if (nextLength < desiredColumn)
        {
            preferredColumn ??= column;
            newCursor = nextEnd;
        }
        else
        {
            preferredColumn = null;
            newCursor = nextStart + desiredColumn;
        }
        return true;
    }

    internal static int FindPreviousWordBoundary(IReadOnlyList<char> chars, int cursor)
    {
        return FindPreviousWordBoundary(chars, cursor, pasteMarkers: []);
    }

    private static int FindPreviousWordBoundary(
        IReadOnlyList<char> chars,
        int cursor,
        IReadOnlyList<PasteMarkerSpan> pasteMarkers)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor <= 0)
        {
            return 0;
        }

        var segments = SegmentWords(chars, endExclusive: cursor, pasteMarkers: pasteMarkers);
        var index = segments.Count - 1;
        while (index >= 0 && segments[index].Kind == WordSegmentKind.Whitespace)
        {
            index--;
        }

        if (index < 0)
        {
            return cursor;
        }

        var segment = segments[index];
        if (segment.Kind is WordSegmentKind.WordLike or WordSegmentKind.Cjk or WordSegmentKind.Atomic)
        {
            return segment.Start;
        }

        var kind = segment.Kind;
        var start = segment.Start;
        index--;
        while (index >= 0 && segments[index].Kind == kind)
        {
            start = segments[index].Start;
            index--;
        }

        return start;
    }

    internal static int FindNextWordBoundary(IReadOnlyList<char> chars, int cursor)
    {
        return FindNextWordBoundary(chars, cursor, pasteMarkers: []);
    }

    private static int FindNextWordBoundary(
        IReadOnlyList<char> chars,
        int cursor,
        IReadOnlyList<PasteMarkerSpan> pasteMarkers)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor >= chars.Count)
        {
            return chars.Count;
        }

        var segments = SegmentWords(chars, startInclusive: cursor, pasteMarkers: pasteMarkers);
        var index = 0;
        while (index < segments.Count && segments[index].Kind == WordSegmentKind.Whitespace)
        {
            index++;
        }

        if (index >= segments.Count)
        {
            return cursor;
        }

        var segment = segments[index];
        if (segment.Kind is WordSegmentKind.WordLike or WordSegmentKind.Cjk or WordSegmentKind.Atomic)
        {
            return segment.End;
        }

        var kind = segment.Kind;
        var end = segment.End;
        index++;
        while (index < segments.Count && segments[index].Kind == kind)
        {
            end = segments[index].End;
            index++;
        }

        return end;
    }

    private static List<WordSegment> SegmentWords(
        IReadOnlyList<char> chars,
        int startInclusive = 0,
        int? endExclusive = null,
        IReadOnlyList<PasteMarkerSpan>? pasteMarkers = null)
    {
        var start = Math.Clamp(startInclusive, 0, chars.Count);
        var end = Math.Clamp(endExclusive ?? chars.Count, start, chars.Count);
        var text = new string(chars.ToArray());
        var segments = new List<WordSegment>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var markerIndex = 0;
        pasteMarkers ??= [];

        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            var element = enumerator.GetTextElement();
            var elementEnd = elementStart + element.Length;
            if (elementEnd <= start)
            {
                continue;
            }

            if (elementStart >= end)
            {
                break;
            }

            while (markerIndex < pasteMarkers.Count && pasteMarkers[markerIndex].End <= elementStart)
            {
                markerIndex++;
            }

            if (markerIndex < pasteMarkers.Count)
            {
                var marker = pasteMarkers[markerIndex];
                if (elementStart == marker.Start)
                {
                    AddWordSegment(segments, new WordSegment(marker.Start, marker.End, WordSegmentKind.Atomic));
                    continue;
                }

                if (elementStart > marker.Start && elementStart < marker.End)
                {
                    continue;
                }
            }

            var segment = new WordSegment(elementStart, elementEnd, ClassifyWordElement(element));
            AddWordSegment(segments, segment);
        }

        return segments;
    }

    private static void AddWordSegment(List<WordSegment> segments, WordSegment segment)
    {
        if (segments.Count == 0 || segment.Kind is WordSegmentKind.Cjk or WordSegmentKind.Atomic)
        {
            segments.Add(segment);
            return;
        }

        var previous = segments[^1];
        if (previous.Kind != segment.Kind || previous.Kind is WordSegmentKind.Cjk or WordSegmentKind.Atomic)
        {
            segments.Add(segment);
            return;
        }

        segments[^1] = previous with { End = segment.End };
    }

    private static WordSegmentKind ClassifyWordElement(string element)
    {
        if (string.IsNullOrEmpty(element))
        {
            return WordSegmentKind.Other;
        }

        if (IsWhitespaceElement(element))
        {
            return WordSegmentKind.Whitespace;
        }

        if (IsCjkElement(element))
        {
            return WordSegmentKind.Cjk;
        }

        if (IsAsciiPunctuationElement(element))
        {
            return WordSegmentKind.Punctuation;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
        return category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber
                or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.ConnectorPunctuation
            ? WordSegmentKind.WordLike
            : WordSegmentKind.Other;
    }

    private static bool IsWhitespaceElement(string element)
    {
        for (var i = 0; i < element.Length; i++)
        {
            if (!char.IsWhiteSpace(element[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiPunctuationElement(string element)
    {
        for (var i = 0; i < element.Length; i++)
        {
            if (IsAsciiWordPunctuation(element[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiWordPunctuation(char ch) =>
        ch is '(' or ')' or '{' or '}' or '[' or ']' or '<' or '>' or '.' or ',' or ';' or ':' or '\''
            or '"' or '!' or '?' or '+' or '-' or '=' or '*' or '/' or '\\' or '|' or '&' or '%' or '^'
            or '$' or '#' or '@' or '~' or '`';

    private static bool IsCjkElement(string element)
    {
        for (var i = 0; i < element.Length; i++)
        {
            var ch = element[i];
            int codePoint;
            if (char.IsHighSurrogate(ch) && i + 1 < element.Length && char.IsLowSurrogate(element[i + 1]))
            {
                codePoint = char.ConvertToUtf32(ch, element[i + 1]);
                i++;
            }
            else
            {
                codePoint = ch;
            }

            if (IsCjkCodePoint(codePoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCjkCodePoint(int codePoint) =>
        codePoint is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EBEF
            or >= 0x3040 and <= 0x309F
            or >= 0x30A0 and <= 0x30FF
            or >= 0x31F0 and <= 0x31FF
            or >= 0x1100 and <= 0x11FF
            or >= 0x3130 and <= 0x318F
            or >= 0xAC00 and <= 0xD7AF
            or >= 0x3100 and <= 0x312F
            or >= 0x31A0 and <= 0x31BF;

    private readonly record struct WordSegment(int Start, int End, WordSegmentKind Kind);

    private readonly record struct PasteMarkerSpan(int Start, int End);

    private enum WordSegmentKind
    {
        Whitespace,
        WordLike,
        Cjk,
        Atomic,
        Punctuation,
        Other,
    }
}

public sealed record InputResult(InputResultKind Kind, string? Text, EditorAction Action = EditorAction.None)
{
    public static InputResult Submitted(string text) => new(InputResultKind.Submitted, text);
    public static InputResult Cancelled { get; } = new(InputResultKind.Cancelled, null);
    public static InputResult ForAction(EditorAction action) => new(InputResultKind.Action, null, action);
}

public enum InputResultKind
{
    Submitted,
    Cancelled,
    Action
}

public sealed class InputHistory
{
    private readonly List<string> _entries = [];
    private readonly int _capacity;
    private readonly IInputHistoryStore? _store;

    public InputHistory(int capacity = 200)
        : this(capacity, store: null)
    {
    }

    public InputHistory(IInputHistoryStore store, int capacity = 200)
        : this(capacity, store)
    {
    }

    private InputHistory(int capacity, IInputHistoryStore? store)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _store = store;

        if (_store is not null)
        {
            foreach (var entry in _store.Load())
            {
                AddInternal(entry, persist: false);
            }
        }
    }

    public int Count => _entries.Count;

    public void Add(string entry) => AddInternal(entry, persist: true);

    /// <summary>【TUI】【会话历史】恢复当前会话的输入，只加入内存回看列表，不重复写入全局历史文件。</summary>
    /// <param name="entry">历史用户输入。</param>
    public void AddRestored(string entry) => AddInternal(entry, persist: false);

    private void AddInternal(string entry, bool persist)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], entry, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(entry);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }

        if (persist)
        {
            _store?.Append(entry);
        }
    }

    public string? Peek(int offsetFromEnd)
    {
        if (offsetFromEnd < 0 || offsetFromEnd >= _entries.Count)
        {
            return null;
        }

        return _entries[_entries.Count - 1 - offsetFromEnd];
    }

    public (string Match, int OffsetFromEnd, int MatchIndex)? FindContaining(string pattern, int startOffsetFromEnd)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var start = Math.Max(0, startOffsetFromEnd);
        for (var offset = start; offset < _entries.Count; offset++)
        {
            var entry = _entries[_entries.Count - 1 - offset];
            var index = entry.IndexOf(pattern, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (entry, offset, index);
            }
        }

        return null;
    }

    public IReadOnlyList<string> Snapshot(int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        if (_entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        var count = Math.Min(limit, _entries.Count);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            // Newest first, so index 0 = most recent.
            result[i] = _entries[_entries.Count - 1 - i];
        }

        return result;
    }
}
