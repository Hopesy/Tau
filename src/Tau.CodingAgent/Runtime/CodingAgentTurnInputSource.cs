using System.Runtime.CompilerServices;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentTurnInputKind
{
    Steering,
    FollowUp
}

public sealed record CodingAgentTurnInput(CodingAgentTurnInputKind Kind, string Text);

public interface ICodingAgentTurnInputSource
{
    IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemConsoleCodingAgentTurnInputSource : ICodingAgentTurnInputSource
{
    private const int PollDelayMilliseconds = 25;

    public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<char>();

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
                buffer.Clear();
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

public sealed class CompositionCodingAgentTurnInputSource : ICodingAgentTurnInputSource
{
    private const string Prompt = "turn> ";

    private readonly InteractiveInputEditor _editor;
    private readonly TrackingSubmitKeyReader _keyReader;

    public CompositionCodingAgentTurnInputSource(
        IConsoleKeyReader keyReader,
        TuiCompositionSession session,
        IKeyBindingMap? keyBindings = null)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(session);

        _keyReader = new TrackingSubmitKeyReader(keyReader);
        _editor = new InteractiveInputEditor(
            _keyReader,
            new TuiCompositionInteractiveRenderer(session),
            history: new InputHistory(),
            bindings: CreateTurnInputBindings(keyBindings));
    }

    public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            InputResult result;
            try
            {
                result = await _editor.ReadLineAsync(Prompt, ConsoleColor.DarkYellow, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (result.Kind != InputResultKind.Submitted)
            {
                _editor.Buffer.SetDraft(string.Empty);
                continue;
            }

            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new CodingAgentTurnInput(_keyReader.ConsumeSubmitKind(), text);
        }
    }

    private static IKeyBindingMap CreateTurnInputBindings(IKeyBindingMap? keyBindings)
    {
        if (keyBindings is KeyBindingMap keyBindingMap)
        {
            return new KeyBindingMap(keyBindingMap.Bindings.Where(static pair =>
                pair.Value is not EditorAction.CycleModelForward
                    and not EditorAction.CycleModelBackward
                    and not EditorAction.SelectModel));
        }

        return KeyBindingMap.WithOverrides(
        [
            new KeyValuePair<KeyBinding, EditorAction>(
                new KeyBinding(ConsoleKey.P, ConsoleModifiers.Control),
                EditorAction.None),
            new KeyValuePair<KeyBinding, EditorAction>(
                new KeyBinding(ConsoleKey.P, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                EditorAction.None),
            new KeyValuePair<KeyBinding, EditorAction>(
                new KeyBinding(ConsoleKey.L, ConsoleModifiers.Control),
                EditorAction.None)
        ]);
    }

    private sealed class TrackingSubmitKeyReader(IConsoleKeyReader inner) : IConsoleKeyReader
    {
        private CodingAgentTurnInputKind _pendingSubmitKind = CodingAgentTurnInputKind.Steering;

        public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            var key = await inner.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key.Key == ConsoleKey.Enter)
            {
                _pendingSubmitKind = (key.Modifiers & ConsoleModifiers.Alt) != 0
                    ? CodingAgentTurnInputKind.FollowUp
                    : CodingAgentTurnInputKind.Steering;
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
            }

            if (key.Key == ConsoleKey.Escape && key.Modifiers == ConsoleModifiers.None)
            {
                return new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true);
            }

            return key;
        }

        public CodingAgentTurnInputKind ConsumeSubmitKind()
        {
            var kind = _pendingSubmitKind;
            _pendingSubmitKind = CodingAgentTurnInputKind.Steering;
            return kind;
        }
    }
}
