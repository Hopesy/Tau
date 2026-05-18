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

    [Fact]
    public async Task ReadLineAsync_CtrlLeftArrowJumpsToPreviousWord()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha beta gamma")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("alpha beta !gamma", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlRightArrowJumpsToNextWord()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha beta gamma")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: true));
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("alpha! beta gamma", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlBackspaceDeletesPreviousWord()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha beta")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("alpha ", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlDeleteRemovesNextWord()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha beta gamma")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(" beta gamma", result.Text);
    }

    [Theory]
    [InlineData("hello world", 11, 6)]
    [InlineData("hello world", 6, 0)]
    [InlineData("hello", 5, 0)]
    [InlineData("", 0, 0)]
    [InlineData("  spaces", 8, 2)]
    public void FindPreviousWordBoundary_HandlesWhitespaceRuns(string text, int cursor, int expected)
    {
        Assert.Equal(expected, InteractiveInputEditor.FindPreviousWordBoundary(text.ToCharArray(), cursor));
    }

    [Theory]
    [InlineData("hello world", 0, 5)]
    [InlineData("hello world", 5, 11)]
    [InlineData("hello", 0, 5)]
    [InlineData("hello", 5, 5)]
    [InlineData("   trailing   ", 0, 11)]
    public void FindNextWordBoundary_HandlesWhitespaceRuns(string text, int cursor, int expected)
    {
        Assert.Equal(expected, InteractiveInputEditor.FindNextWordBoundary(text.ToCharArray(), cursor));
    }

    [Fact]
    public async Task ReadLineAsync_CtrlAJumpsToLineStart()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "abc")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x01', ConsoleKey.A, shift: false, alt: false, control: true));
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("!abc", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlEJumpsToLineEnd()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "abc")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x05', ConsoleKey.E, shift: false, alt: false, control: true));
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("abc!", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlKKillsToEndOfLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "abc def")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x0B', ConsoleKey.K, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("abc ", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlUKillsToStartOfLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "abc def")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("def", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlRFindsHistoryMatchAndEnterSubmits()
    {
        var history = new InputHistory();
        history.Add("git status");
        history.Add("dotnet build");
        history.Add("git commit");

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('\x12', ConsoleKey.R, shift: false, alt: false, control: true));
        reader.Enqueue('g');
        reader.Enqueue('i');
        reader.Enqueue('t');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("git commit", result.Text);
        Assert.NotEmpty(renderer.SearchRenders);
        Assert.Contains(renderer.SearchRenders, render => render.Pattern == "git" && render.Match == "git commit");
    }

    [Fact]
    public async Task ReadLineAsync_CtrlRTwiceCyclesToOlderMatch()
    {
        var history = new InputHistory();
        history.Add("git status");
        history.Add("git commit");

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('\x12', ConsoleKey.R, shift: false, alt: false, control: true));
        reader.Enqueue('g');
        reader.EnqueueRaw(new ConsoleKeyInfo('\x12', ConsoleKey.R, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("git status", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlREscapeRestoresOriginalBuffer()
    {
        var history = new InputHistory();
        history.Add("git status");

        var reader = new FakeKeyReader();
        reader.Enqueue('h');
        reader.Enqueue('i');
        reader.EnqueueRaw(new ConsoleKeyInfo('\x12', ConsoleKey.R, shift: false, alt: false, control: true));
        reader.Enqueue('g');
        reader.EnqueueKey(ConsoleKey.Escape);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("hi", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlRBackspaceShrinksPattern()
    {
        var history = new InputHistory();
        history.Add("apple");
        history.Add("apricot");

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('\x12', ConsoleKey.R, shift: false, alt: false, control: true));
        reader.Enqueue('a');
        reader.Enqueue('p');
        reader.Enqueue('p');
        reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        // After backspace, pattern is "ap" → newest match wins.
        Assert.Equal("apricot", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CustomBindingMapRebindsF1ToSubmit()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('h');
        reader.Enqueue('i');
        reader.EnqueueKey(ConsoleKey.F1);

        var renderer = new FakeRenderer();
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.F1, ConsoleModifiers.None)] = EditorAction.Submit
        });
        var editor = new InteractiveInputEditor(reader, renderer, bindings: bindings);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Submitted, result.Kind);
        Assert.Equal("hi", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CustomBindingMapDisablesEnterSubmit()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('x');
        reader.EnqueueKey(ConsoleKey.Enter);
        reader.Enqueue('y');
        reader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.None)] = EditorAction.None
        });
        var editor = new InteractiveInputEditor(reader, renderer, bindings: bindings);

        var result = await editor.ReadLineAsync("> ");

        // Enter no longer submits, so the buffer carries until Ctrl-C cancels.
        Assert.Equal(InputResultKind.Cancelled, result.Kind);
        Assert.Equal("xy", editor.Buffer.Draft);
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

        public List<(string Pattern, string? Match, int Cursor)> SearchRenders { get; } = [];

        public void RenderSearch(string pattern, string? match, int cursorInMatch) =>
            SearchRenders.Add((pattern, match, cursorInMatch));

        public void Commit() => CommitCalls++;
        public void Cancel() => CancelCalls++;
    }
}
