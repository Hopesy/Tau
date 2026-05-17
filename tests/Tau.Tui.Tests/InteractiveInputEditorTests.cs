using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class InteractiveInputEditorTests
{
    [Fact]
    public async Task ReadLineAsync_AppendsCharactersAndCommitsOnEnter()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('h');
        reader.Enqueue('i');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Submitted, result.Kind);
        Assert.Equal("hi", result.Text);
        Assert.Contains(("> ", (ConsoleColor?)null), renderer.PromptCalls);
        // Final render should reflect the full buffer and cursor at end.
        Assert.Equal(("hi", 2), renderer.RenderCalls[^1]);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_BackspaceRemovesCharBeforeCursor()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.Enqueue('b');
        reader.Enqueue('c');
        reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("ab", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_DeleteRemovesCharAtCursor()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.Enqueue('b');
        reader.Enqueue('c');
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.Delete);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("ac", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CursorMovesWithArrowsAndHomeEnd()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.Enqueue('b');
        reader.Enqueue('c');
        reader.EnqueueKey(ConsoleKey.Home);
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.End);
        reader.Enqueue('?');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("!abc?", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlCCancelsInput()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('h');
        reader.EnqueueRaw(new ConsoleKeyInfo('', ConsoleKey.C, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Cancelled, result.Kind);
        Assert.Null(result.Text);
        Assert.Equal(1, renderer.CancelCalls);
        Assert.Equal(0, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_UpArrowRecallsHistory()
    {
        var history = new InputHistory();
        history.Add("first command");
        history.Add("second command");

        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("second command", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_DownArrowReturnsToEmptyAfterHistoryWalk()
    {
        var history = new InputHistory();
        history.Add("only");

        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.Enqueue('x');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("x", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_AddsSubmittedLineToHistory()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        await editor.ReadLineAsync("> ");

        Assert.Equal(1, history.Count);
        Assert.Equal("a", history.Peek(0));
    }

    [Fact]
    public void History_DropsConsecutiveDuplicates()
    {
        var history = new InputHistory();
        history.Add("a");
        history.Add("a");
        history.Add("b");

        Assert.Equal(2, history.Count);
        Assert.Equal("b", history.Peek(0));
        Assert.Equal("a", history.Peek(1));
    }

    [Fact]
    public void History_RespectsCapacity()
    {
        var history = new InputHistory(capacity: 2);
        history.Add("a");
        history.Add("b");
        history.Add("c");

        Assert.Equal(2, history.Count);
        Assert.Equal("c", history.Peek(0));
        Assert.Equal("b", history.Peek(1));
    }

    private sealed class FakeKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void Enqueue(char ch)
        {
            _keys.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, shift: false, alt: false, control: false));
        }

        public void EnqueueKey(ConsoleKey key)
        {
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false));
        }

        public void EnqueueRaw(ConsoleKeyInfo key)
        {
            _keys.Enqueue(key);
        }

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            if (_keys.Count == 0)
            {
                throw new InvalidOperationException("No more queued keys.");
            }

            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class FakeRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        public List<(string Prompt, ConsoleColor? Color)> PromptCalls { get; } = [];
        public List<(string Buffer, int Cursor)> RenderCalls { get; } = [];
        public int CommitCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public void WritePrompt(string prompt, ConsoleColor? color = null) =>
            PromptCalls.Add((prompt, color));

        public void Render(string buffer, int cursorIndex) =>
            RenderCalls.Add((buffer, cursorIndex));

        public void Commit() => CommitCalls++;
        public void Cancel() => CancelCalls++;
    }
}
