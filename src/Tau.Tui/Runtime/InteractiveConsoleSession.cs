using Tau.Tui.Abstractions;
using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

public sealed partial class InteractiveConsoleSession
{
    private const string DefaultPrompt = ">> ";
    private const ConsoleColor DefaultPromptColor = ConsoleColor.Green;
    private const string PromptZoneStart = "\u001b]133;A\u0007";
    private const string PromptZoneEnd = "\u001b]133;B\u0007";
    private const string PromptZoneFinal = "\u001b]133;C\u0007";
    private readonly ITerminal _terminal;
    private readonly InteractiveInputEditor? _editor;
    private readonly Action? _clearScreenAction;
    private readonly List<TranscriptEntry> _transcript = [];
    private readonly object _stateSync = new();
    private int _visibleTranscriptStart;
    private bool _streamingLineOpen;
    private TranscriptEntryKind? _streamingKind;
    private string _streamingBuffer = string.Empty;

    public InputBuffer InputBuffer { get; } = new();
    public IReadOnlyList<TranscriptEntry> Transcript
    {
        get
        {
            lock (_stateSync)
            {
                return _transcript.ToArray();
            }
        }
    }
    public IKeyBindingMap? InputKeyBindings => _editor?.KeyBindings;
    public event Action? TranscriptChanged;

    public InteractiveConsoleSession(
        ITerminal terminal,
        InteractiveInputEditor? editor = null,
        Action? clearScreenAction = null)
    {
        _terminal = terminal;
        _editor = editor;
        _clearScreenAction = clearScreenAction;
    }

    /// <summary>
    /// 【终端启动】【欢迎界面】显示默认品牌 banner、标题和输入提示，或显示扩展提供的自定义头部。
    /// </summary>
    /// <param name="title">欢迎界面标题。</param>
    /// <param name="promptHint">输入区上方的操作提示。</param>
    /// <param name="customHeaderLines">扩展提供的自定义头部；非空时替换默认品牌和标题头部。</param>
    public void ShowWelcome(string title, string promptHint, IReadOnlyList<string>? customHeaderLines = null)
    {
        lock (_stateSync)
        {
            if (customHeaderLines is null)
            {
                foreach (var bannerLine in TuiWelcomeBanner.Lines)
                {
                    _terminal.WriteLine(bannerLine, ConsoleColor.Cyan);
                    _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, bannerLine));
                }

                _terminal.WriteLine(title, ConsoleColor.Cyan);
                _terminal.WriteLine(promptHint);
                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, title));
                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, promptHint));
            }
            else
            {
                foreach (var line in customHeaderLines)
                {
                    _terminal.WriteLine(line);
                    _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, line));
                }
            }

            _terminal.WriteLine();
            NotifyTranscriptChanged();
        }
    }

    public IReadOnlyList<TuiMessage> SnapshotMessages()
    {
        lock (_stateSync)
        {
            var messages = _transcript
                .Skip(Math.Clamp(_visibleTranscriptStart, 0, _transcript.Count))
                .Select(entry => CreateDisplayMessage(entry.Kind, entry.Text, false, entry.ApplyMarkdownTransform))
                .ToList();

            if (_streamingLineOpen && _streamingKind is { } streamingKind)
            {
                messages.Add(CreateDisplayMessage(streamingKind, _streamingBuffer, _streamingIsLive, _streamingApplyMarkdown));
            }

            return messages;
        }
    }

    public async Task<string?> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadInputResultAsync(cancellationToken).ConfigureAwait(false);
        return result.Kind == InputResultKind.Submitted ? result.Text : null;
    }

    public async Task<string?> ReadInputAsync(
        string prompt,
        ConsoleColor? promptColor,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadInputResultAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
        return result.Kind == InputResultKind.Submitted ? result.Text : null;
    }

    public async Task<InputResult> ReadInputResultAsync(CancellationToken cancellationToken = default)
    {
        return await ReadInputResultAsync(DefaultPrompt, DefaultPromptColor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InputResult> ReadInputResultAsync(
        string prompt,
        ConsoleColor? promptColor,
        CancellationToken cancellationToken = default)
    {
        if (_editor is not null)
        {
            lock (_stateSync)
            {
                EnsureStreamingLineClosed();
            }

            // 1. 【Tui】【模态草稿隔离】流式阶段使用独立询问状态，不能把正在编辑的草稿当作问题答案
            var result = _draftEditorOverride is null
                ? await _editor.ReadLineAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false)
                : await _editor.ReadPromptAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
            if (result.Kind != InputResultKind.Submitted)
            {
                return result;
            }

            lock (_stateSync)
            {
                InputBuffer.SetDraft(result.Text);
                return InputResult.Submitted(InputBuffer.Commit());
            }
        }

        var input = await _terminal.PromptAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            InputBuffer.SetDraft(input);
            return InputResult.Submitted(InputBuffer.Commit());
        }
    }

    /// <summary>【Tui】【草稿替换】接收应用层完整文本，清除编辑器旧粘贴引用。</summary>
    /// <param name="value">完整草稿，空引用表示清空。</param>
    public void SetDraft(string? value) => SetDraft(value, null);

    /// <summary>【Tui】【草稿与光标替换】恢复完整文本和可选光标，清除旧粘贴引用。</summary>
    /// <param name="value">完整草稿，空引用表示清空。</param><param name="cursorIndex">可选完整文本光标。</param>
    public void SetDraft(string? value, int? cursorIndex)
    {
        lock (_stateSync)
        {
            InputBuffer.SetDraft(value);
            DraftEditor?.SetExpandedDraft(value, cursorIndex);
        }
    }

    /// <summary>【Tui】【草稿读取】向应用层返回展开的粘贴内容，避免发送或外部编辑时泄漏折叠标记。</summary>
    /// <returns>完整未发送文本。</returns>
    public string GetDraft()
    {
        lock (_stateSync)
        {
            return DraftEditor?.GetExpandedDraft() ?? InputBuffer.Draft;
        }
    }

    /// <summary>【Tui】【草稿光标】读取完整草稿中的 UTF-16 插入偏移，没有交互编辑器时使用末尾。</summary>
    /// <returns>草稿插入位置。</returns>
    public int GetDraftCursorIndex()
    {
        lock (_stateSync) return DraftEditor?.GetExpandedCursorIndex() ?? InputBuffer.Draft.Length;
    }

    /// <summary>【Tui】【应用插入】在保留的输入光标处插入完整文本，交互编辑器将其作为一次撤销操作。</summary>
    /// <param name="text">待插入文本。</param>
    public void InsertTextAtCursor(string text)
    {
        lock (_stateSync)
        {
            if (DraftEditor is { } editor)
            {
                editor.InsertTextAtCursor(text);
                InputBuffer.SetDraft(editor.GetExpandedDraft());
            }
            else InputBuffer.SetDraft(InputBuffer.Draft + text);
        }
    }

    public void SetInputShortcutHandler(Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? shortcutHandler)
    {
        lock (_stateSync)
        {
            _editor?.SetShortcutHandler(shortcutHandler);
        }
    }

    public void WriteUserMessage(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write(PromptZoneStart);
            _terminal.Write("you> ", ConsoleColor.Green);
            _terminal.WriteLine(TransformDisplayMarkdown(message, TranscriptEntryKind.User, false, 75));
            _terminal.Write(PromptZoneEnd + PromptZoneFinal);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.User, message));
            NotifyTranscriptChanged();
        }
    }

    /// <summary>【TUI】【自定义显示】追加消息或按条目标识更新自定义组件。</summary>
    /// <param name="message">显示正文。</param><param name="key">可选稳定条目标识。</param>
    public void WriteCustomMessage(string message, string? key = null)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("custom> ", ConsoleColor.Magenta);
            _terminal.WriteLine(message);
            UpsertTranscriptEntry(TranscriptEntryKind.Custom, message, key);
            NotifyTranscriptChanged();
        }
    }

    public void WriteSkillInvocation(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("skill> ", ConsoleColor.Magenta);
            _terminal.WriteLine(message);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Skill, message));
            NotifyTranscriptChanged();
        }
    }

    /// <summary>【TUI】【助手显示】追加原始助手正文并按完整累计内容更新流式视图。</summary>
    /// <param name="delta">正文增量。</param><param name="isStreaming">是否为实时响应；历史恢复为否。</param>
    public void WriteAssistantText(string delta, bool isStreaming = true)
    {
        lock (_stateSync)
        {
            EnsureStreamingMode(TranscriptEntryKind.Assistant, "tau> ", ConsoleColor.Cyan);
            _streamingIsLive = isStreaming;
            if (_markdownTransform is null) _terminal.Write(delta);
            _streamingBuffer += delta;
            NotifyTranscriptChanged();
        }
    }

    /// <summary>【TUI】【思考显示】保存原始增量，隐藏标签可跳过 Markdown 转换。</summary>
    /// <param name="delta">思考增量或隐藏标签。</param><param name="label">显示前缀。</param>
    /// <param name="applyMarkdownTransform">是否转换正文。</param>
    /// <param name="isStreaming">是否为实时思考；历史恢复为否。</param>
    public void WriteAssistantThinking(string delta, string? label = null, bool applyMarkdownTransform = true, bool isStreaming = true)
    {
        lock (_stateSync)
        {
            var thinkingLabel = string.IsNullOrWhiteSpace(label) ? "thinking" : label.Trim();
            if (_streamingLineOpen && _streamingApplyMarkdown != applyMarkdownTransform) EnsureStreamingLineClosed();
            EnsureStreamingMode(TranscriptEntryKind.Thinking, $"{thinkingLabel}> ", ConsoleColor.DarkGray);
            _streamingApplyMarkdown = applyMarkdownTransform;
            _streamingIsLive = isStreaming;
            if (_markdownTransform is null || !applyMarkdownTransform) _terminal.Write(delta, ConsoleColor.DarkGray);
            _streamingBuffer += delta;
            NotifyTranscriptChanged();
        }
    }

    public void WriteToolStart(string toolName)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("tool> ", ConsoleColor.Yellow);
            _terminal.Write($"[{toolName}] ", ConsoleColor.Yellow);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Tool, $"[{toolName}]"));
            NotifyTranscriptChanged();
        }
    }

    public void WriteToolEnd(bool isError)
    {
        lock (_stateSync)
        {
            var status = isError ? "(error)" : "(done)";
            _terminal.WriteLine(status, isError ? ConsoleColor.Red : ConsoleColor.DarkGreen);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, status));
            NotifyTranscriptChanged();
        }
    }

    public void WriteToolComponent(ITuiComponent component, int width = 80, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();

            var text = RenderComponentText(component, width);
            if (text.Length == 0)
            {
                return;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i == 0)
                {
                    _terminal.Write("tool> ", ConsoleColor.Yellow);
                    _terminal.WriteLine(lines[i]);
                }
                else
                {
                    _terminal.WriteLine("      " + lines[i]);
                }
            }

            UpsertTranscriptEntry(TranscriptEntryKind.Tool, text, key);
            NotifyTranscriptChanged();
        }
    }

    public void WriteBranchSummary(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("branch> ", ConsoleColor.Magenta);
            _terminal.WriteLine(message);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.BranchSummary, message));
            NotifyTranscriptChanged();
        }
    }

    public void WriteCompactionSummary(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("compaction> ", ConsoleColor.Magenta);
            _terminal.WriteLine(message);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.CompactionSummary, message));
            NotifyTranscriptChanged();
        }
    }

    public void WriteStatus(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("status> ", ConsoleColor.DarkGray);
            _terminal.WriteLine(message);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, message));
            NotifyTranscriptChanged();
        }
    }

    public void WriteRuntimeError(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.Write("error> ", ConsoleColor.Red);
            _terminal.WriteLine(message, ConsoleColor.Red);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Error, message));
            NotifyTranscriptChanged();
        }
    }

    public void CompleteAssistantTurn()
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
        }
    }

    public void WriteCancelled()
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.WriteLine("[Cancelled]", ConsoleColor.Yellow);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, "[Cancelled]"));
            NotifyTranscriptChanged();
        }
    }

    public void WriteShutdown(string message)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _terminal.WriteLine(message);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, message));
            NotifyTranscriptChanged();
        }
    }

    public void ClearScreen()
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            if (_clearScreenAction is not null)
            {
                _visibleTranscriptStart = _transcript.Count;
                NotifyTranscriptChanged();
                _clearScreenAction();
                return;
            }

            // ANSI clear screen + move cursor home. Terminals that don't support ANSI
            // will just print the codes, which is acceptable for a /clear best-effort.
            _terminal.Write("\u001b[2J\u001b[H");
        }
    }

    /// <summary>【TUI】【会话重建】清空旧会话的显示与键控工具条目，保留当前编辑草稿。</summary>
    public void ResetTranscript()
    {
        lock (_stateSync)
        {
            // 1. 【TUI】【显示清理】结束流式行后清除历史，避免旧工具标识覆盖新会话内容
            EnsureStreamingLineClosed();
            _transcript.Clear();
            _visibleTranscriptStart = 0;
            if (_clearScreenAction is not null) _clearScreenAction();
            else _terminal.Write("\u001b[2J\u001b[H");
            NotifyTranscriptChanged();
        }
    }

    /// <summary>【TUI】【输入恢复】把历史用户输入加入编辑器回看列表，不改变草稿或重复落盘。</summary>
    /// <param name="text">原始用户输入。</param>
    public void RestoreInputHistory(string text)
    {
        lock (_stateSync) _editor?.History.AddRestored(text);
    }

    /// <summary>【TUI】【隐藏组件】移除返回空内容的键控条目，并保持清屏后的可见范围。</summary>
    /// <param name="kind">条目种类。</param><param name="key">稳定标识。</param>
    public void RemoveTranscriptEntry(TranscriptEntryKind kind, string key)
    {
        lock (_stateSync)
        {
            var index = _transcript.FindIndex(entry => entry.Kind == kind && entry.Key == key);
            if (index < 0) return;
            _transcript.RemoveAt(index);
            if (index < _visibleTranscriptStart) _visibleTranscriptStart--;
            NotifyTranscriptChanged();
        }
    }

    private void EnsureStreamingLineClosed()
    {
        if (!_streamingLineOpen)
        {
            return;
        }

        // 1. 【TUI】【兼容终端】无重绘接口的终端在正文结束时输出转换结果，组合界面由快照持续渲染
        if (_markdownTransform is not null && _streamingApplyMarkdown && _streamingKind is { } completedKind)
            _terminal.Write(TransformDisplayMarkdown(_streamingBuffer, completedKind, false, _streamingContentWidth));
        _terminal.WriteLine();
        if (_streamingKind == TranscriptEntryKind.Assistant)
        {
            _terminal.Write(PromptZoneEnd + PromptZoneFinal);
        }

        _streamingLineOpen = false;
        if (_streamingKind is not null)
        {
            _transcript.Add(new TranscriptEntry(_streamingKind.Value, _streamingBuffer) { ApplyMarkdownTransform = _streamingApplyMarkdown });
        }

        _streamingKind = null;
        _streamingBuffer = string.Empty;
        NotifyTranscriptChanged();
    }

    private void EnsureStreamingMode(TranscriptEntryKind kind, string prefix, ConsoleColor prefixColor)
    {
        if (_streamingLineOpen && _streamingKind == kind)
        {
            return;
        }

        EnsureStreamingLineClosed();
        if (kind == TranscriptEntryKind.Assistant)
        {
            _terminal.Write(PromptZoneStart);
        }

        _terminal.Write(prefix, prefixColor);
        _streamingLineOpen = true;
        _streamingKind = kind;
        _streamingBuffer = string.Empty;
        _streamingApplyMarkdown = true;
        _streamingContentWidth = Math.Max(1, 80 - Tau.Tui.Rendering.TuiText.VisibleWidth(prefix));
    }

    private static TuiMessageRole ToMessageRole(TranscriptEntryKind kind) =>
        kind switch
        {
            TranscriptEntryKind.System => TuiMessageRole.System,
            TranscriptEntryKind.User => TuiMessageRole.User,
            TranscriptEntryKind.Assistant => TuiMessageRole.Assistant,
            TranscriptEntryKind.Thinking => TuiMessageRole.Thinking,
            TranscriptEntryKind.Tool => TuiMessageRole.Tool,
            TranscriptEntryKind.BranchSummary => TuiMessageRole.BranchSummary,
            TranscriptEntryKind.CompactionSummary => TuiMessageRole.CompactionSummary,
            TranscriptEntryKind.Custom => TuiMessageRole.Custom,
            TranscriptEntryKind.Skill => TuiMessageRole.Skill,
            TranscriptEntryKind.Error => TuiMessageRole.Error,
            TranscriptEntryKind.Status => TuiMessageRole.Status,
            _ => TuiMessageRole.Status,
        };

    private static string RenderComponentText(ITuiComponent component, int width)
    {
        var lines = component.Render(Math.Max(1, width))
            .Select(static line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var commonIndent = lines
            .Where(static line => line.Length > 0)
            .Select(LeadingSpaceCount)
            .DefaultIfEmpty(0)
            .Min();
        if (commonIndent > 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = lines[i].Length >= commonIndent
                    ? lines[i][commonIndent..]
                    : string.Empty;
            }
        }

        return string.Join('\n', lines);
    }

    private static int LeadingSpaceCount(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private void UpsertTranscriptEntry(TranscriptEntryKind kind, string text, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var index = _transcript.FindIndex(entry =>
                entry.Kind == kind &&
                string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (index >= 0)
            {
                _transcript[index] = new TranscriptEntry(kind, text, key);
                return;
            }
        }

        _transcript.Add(new TranscriptEntry(kind, text, string.IsNullOrWhiteSpace(key) ? null : key));
    }

    private void NotifyTranscriptChanged() => TranscriptChanged?.Invoke();
}
