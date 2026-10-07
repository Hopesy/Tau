using System.Runtime.CompilerServices;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentTurnInputKind
{
    Steering,
    FollowUp,
    Interrupt,
    RestoreQueuedMessages,
    ApplicationAction
}

public sealed record CodingAgentTurnInput(CodingAgentTurnInputKind Kind, string Text, EditorAction Action = EditorAction.None);

public interface ICodingAgentTurnInputSource
{
    /// <summary>【CodingAgent】【活动编辑器】提供应用动作所用的实时草稿，简单输入源无需提供。</summary>
    InteractiveInputEditor? InputEditor => null;
    IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(CancellationToken cancellationToken = default);

    /// <summary>【CodingAgent】【草稿交接】取走未发送文本，兼容无编辑状态的输入源。</summary>
    /// <returns>完整草稿；不支持草稿交接时返回空引用。</returns>
    string? TakeDraft() => null;

    /// <summary>【CodingAgent】【草稿恢复】在读取开始前或输入事件暂停点恢复完整草稿。</summary>
    /// <param name="draft">未折叠的完整文本。</param>
    void SetDraft(string draft) { }
}

public sealed class SystemConsoleCodingAgentTurnInputSource : ICodingAgentTurnInputSource
{
    private const int PollDelayMilliseconds = 25;
    private readonly List<char> _draft = [];

    /// <summary>【CodingAgent】【控制台草稿】取走未发送的文本。</summary><returns>完整草稿。</returns>
    public string TakeDraft()
    {
        var draft = new string(_draft.ToArray());
        _draft.Clear();
        return draft;
    }

    /// <summary>【CodingAgent】【控制台草稿】在输入暂停时恢复未发送文本。</summary><param name="draft">完整草稿。</param>
    public void SetDraft(string draft)
    {
        _draft.Clear();
        _draft.AddRange(draft);
    }

    /// <summary>【CodingAgent】【控制台输入】轮询输入并发布发送与中断事件，取消时保留未发送草稿。</summary>
    /// <param name="cancellationToken">回合取消信号。</param><returns>输入事件。</returns>
    public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = _draft;

        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasKey;
            try
            {
                hasKey = Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            if (!hasKey)
            {
                try
                {
                    await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                var text = new string(buffer.ToArray()).Trim();
                buffer.Clear();
                if (text.Length == 0)
                {
                    continue;
                }

                var kind = (key.Modifiers & ConsoleModifiers.Alt) != 0
                    ? CodingAgentTurnInputKind.FollowUp
                    : CodingAgentTurnInputKind.Steering;
                yield return new CodingAgentTurnInput(kind, text);
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                continue;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                yield return new(CodingAgentTurnInputKind.Interrupt, new string(buffer.ToArray()));
                continue;
            }

            if (key.KeyChar != '\0' &&
                !char.IsControl(key.KeyChar) &&
                (key.Modifiers & ConsoleModifiers.Control) == 0)
            {
                buffer.Add(key.KeyChar);
            }
        }
    }
}

/// <summary>【CodingAgent】【流式输入】使用主编辑器的当前键位接收 steering 和 follow-up，保留粘贴与多行输入。</summary>
public sealed class CompositionCodingAgentTurnInputSource : ICodingAgentTurnInputSource
{
    private const string Prompt = ">> ";
    private readonly InteractiveInputEditor _editor;
    private readonly TrackingSubmitKeyReader _keyReader;
    private readonly IKeyBindingMap _turnBindings;
    public InteractiveInputEditor InputEditor => _editor;

    /// <summary>【CodingAgent】【流式编辑器】复用主编辑器或创建独立编辑器，并跟随主输入区的快捷键重载。</summary>
    /// <param name="keyReader">共享按键来源。</param><param name="session">组合终端。</param><param name="keyBindings">固定配置。</param>
    /// <param name="keyBindingsProvider">可选动态配置，优先于固定配置。</param>
    /// <param name="sharedEditor">可选主编辑器，保留生成前后的完整编辑状态。</param>
    public CompositionCodingAgentTurnInputSource(IConsoleKeyReader keyReader, TuiCompositionSession session,
        IKeyBindingMap? keyBindings = null, Func<IKeyBindingMap?>? keyBindingsProvider = null, InteractiveInputEditor? sharedEditor = null)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(session);
        Func<IKeyBindingMap> currentBindings = () => keyBindingsProvider?.Invoke() ?? keyBindings ?? sharedEditor?.KeyBindings ?? KeyBindingMap.Default;
        InteractiveInputEditor? editor = null;
        _keyReader = new TrackingSubmitKeyReader(keyReader, currentBindings, () => editor?.Buffer.Draft.Length > 0);
        _turnBindings = new TurnInputBindings(currentBindings);
        _editor = editor = sharedEditor ?? new InteractiveInputEditor(_keyReader, new TuiCompositionInteractiveRenderer(session),
            history: new InputHistory(), bindings: _turnBindings);
    }

    /// <summary>【CodingAgent】【流式输入循环】过滤空输入，按触发提交的动作区分即时引导与后续消息。</summary>
    /// <param name="cancellationToken">当前回合的取消信号。</param><returns>输入事件序列。</returns>
    public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            InputResult result;
            try { result = await _editor.ReadLineWithInputAsync(_keyReader, _turnBindings, Prompt, ConsoleColor.DarkYellow, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            if (result.Kind == InputResultKind.Action && result.Action is EditorAction.Interrupt or EditorAction.RestoreQueuedMessages)
            {
                // 1. 【CodingAgent】【队列编辑】先展开粘贴，宿主在迭代暂停点恢复队列或取消当前回合
                var draft = _editor.GetExpandedDraft();
                yield return new(result.Action == EditorAction.Interrupt ? CodingAgentTurnInputKind.Interrupt : CodingAgentTurnInputKind.RestoreQueuedMessages, draft);
                continue;
            }
            if (result.Kind == InputResultKind.Action || result.Kind == InputResultKind.Cancelled && _keyReader.LastAction == EditorAction.ExitIfEmpty)
            {
                yield return new(CodingAgentTurnInputKind.ApplicationAction, _editor.GetExpandedDraft(),
                    result.Kind == InputResultKind.Action ? result.Action : EditorAction.ExitIfEmpty);
                continue;
            }
            if (result.Kind != InputResultKind.Submitted)
            {
                _editor.Buffer.SetDraft(string.Empty);
                continue;
            }
            var kind = _keyReader.ConsumeSubmitKind();
            var text = result.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) yield return new CodingAgentTurnInput(kind, text);
        }
    }

    /// <summary>【CodingAgent】【草稿交接】取走展开后的文本，避免下一回合重复恢复或丢失粘贴内容。</summary>
    /// <returns>完整未发送草稿。</returns>
    public string TakeDraft() => _editor.TakeDraft();

    /// <summary>【CodingAgent】【草稿恢复】在读取开始前或动作事件暂停时更新下一次编辑的文本。</summary>
    /// <param name="draft">完整草稿。</param>
    public void SetDraft(string draft) => _editor.SetExpandedDraft(draft);

    /// <summary>【CodingAgent】【输入上下文映射】保留编辑及应用动作，由宿主按动作类型协调回合和会话生命周期。</summary>
    /// <param name="bindings">当前配置来源。</param>
    private sealed class TurnInputBindings(Func<IKeyBindingMap> bindings) : IKeyBindingMap
    {
        public bool UseLegacyShortcuts => bindings().UseLegacyShortcuts;
        public IReadOnlyDictionary<KeyBinding, EditorAction> Bindings => bindings().Bindings.ToDictionary(pair => pair.Key, pair => Adapt(pair.Value));

        /// <summary>【CodingAgent】【流式动作】解析空输入上下文的按键。</summary>
        /// <param name="key">当前按键。</param><returns>流式编辑动作。</returns>
        public EditorAction Resolve(ConsoleKeyInfo key) => Resolve(key, false);

        /// <summary>【CodingAgent】【流式动作】按真实草稿状态解析按键，follow-up 使用普通提交过程保留粘贴内容。</summary>
        /// <param name="key">当前按键。</param><param name="hasText">草稿是否非空。</param><returns>映射后的编辑动作。</returns>
        public EditorAction Resolve(ConsoleKeyInfo key, bool hasText) => bindings().UseLegacyShortcuts &&
            key.Key == ConsoleKey.Escape && key.Modifiers == ConsoleModifiers.None
                ? EditorAction.Interrupt : Adapt(bindings().Resolve(key, hasText));

        /// <summary>【CodingAgent】【原始流式键位】保留原生协议修饰键，旧配置继续使用控制台兼容动作。</summary>
        /// <param name="input">完整按键事件。</param><param name="hasText">草稿是否非空。</param><returns>流式动作。</returns>
        public EditorAction ResolveInput(ConsoleInputEvent input, bool hasText) => bindings().UseLegacyShortcuts
            ? Resolve(input.Key, hasText) : Adapt(bindings().ResolveInput(input, hasText));

        /// <summary>【CodingAgent】【动作转换】follow-up 复用编辑器提交，其余动作交给宿主处理。</summary>
        /// <param name="action">主编辑器动作。</param><returns>流式编辑动作。</returns>
        private static EditorAction Adapt(EditorAction action) => action switch
        {
            EditorAction.QueueFollowUpMessage => EditorAction.Submit,
            _ => action
        };
    }

    /// <summary>【CodingAgent】【发送键跟踪】记录触发提交的动作，原样转发粘贴事件，仅旧版映射保留 Enter 修饰键兼容。</summary>
    /// <param name="inner">原始输入。</param><param name="bindings">当前绑定。</param><param name="hasText">实时草稿状态。</param>
    private sealed class TrackingSubmitKeyReader(IConsoleKeyReader inner, Func<IKeyBindingMap> bindings, Func<bool> hasText) : IConsoleInputEventReader
    {
        private CodingAgentTurnInputKind _pendingSubmitKind = CodingAgentTurnInputKind.Steering;
        public EditorAction LastAction { get; private set; }

        /// <summary>【CodingAgent】【按键读取】读取并转换单个按键。</summary>
        /// <param name="cancellationToken">取消信号。</param><returns>转换后的按键。</returns>
        public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) =>
            Transform(await inner.ReadKeyAsync(cancellationToken).ConfigureAwait(false));

        /// <summary>【CodingAgent】【粘贴透传】完整保留 bracketed-paste，普通按键才参与提交动作分类。</summary>
        /// <param name="cancellationToken">取消信号。</param><returns>原始粘贴或转换后的按键事件。</returns>
        public async ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken = default)
        {
            var input = inner is IConsoleInputEventReader events
                ? await events.ReadInputEventAsync(cancellationToken).ConfigureAwait(false)
                : ConsoleInputEvent.KeyPress(await inner.ReadKeyAsync(cancellationToken).ConfigureAwait(false));
            return input.Kind == ConsoleInputEventKind.Paste ? input : TransformInput(input);
        }

        /// <summary>【CodingAgent】【发送分类】原生动作按配置决定发送类型，换行键不改变待发送类型。</summary>
        /// <param name="key">输入按键。</param><returns>交给编辑器的按键。</returns>
        private ConsoleKeyInfo Transform(ConsoleKeyInfo key) => TransformInput(ConsoleInputEvent.KeyPress(key)).Key;

        /// <summary>【CodingAgent】【协议分类】按完整按键事件区分提交类型，仅旧格式 Enter 转换为普通回车。</summary>
        /// <param name="input">完整按键事件。</param><returns>传给编辑器的事件。</returns>
        private ConsoleInputEvent TransformInput(ConsoleInputEvent input)
        {
            var map = bindings();
            var key = input.Key;
            if (map.UseLegacyShortcuts && key.Key == ConsoleKey.Enter)
            {
                LastAction = EditorAction.Submit;
                _pendingSubmitKind = key.Modifiers.HasFlag(ConsoleModifiers.Alt) ? CodingAgentTurnInputKind.FollowUp : CodingAgentTurnInputKind.Steering;
                return ConsoleInputEvent.KeyPress(new('\r', ConsoleKey.Enter, false, false, false));
            }
            var action = map.ResolveInput(input, hasText());
            LastAction = action;
            if (action == EditorAction.QueueFollowUpMessage) _pendingSubmitKind = CodingAgentTurnInputKind.FollowUp;
            else if (action == EditorAction.Submit) _pendingSubmitKind = CodingAgentTurnInputKind.Steering;
            return input;
        }

        /// <summary>【CodingAgent】【发送结束】消费本次类型并恢复默认即时引导。</summary>
        /// <returns>本次发送类型。</returns>
        public CodingAgentTurnInputKind ConsumeSubmitKind()
        {
            var kind = _pendingSubmitKind;
            _pendingSubmitKind = CodingAgentTurnInputKind.Steering;
            return kind;
        }
    }
}
