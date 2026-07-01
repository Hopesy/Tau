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
    public async Task ReadLineAsync_LargePasteRendersMarkerAndSubmitsExpandedText()
    {
        var paste = string.Join("\n", Enumerable.Range(1, 11).Select(static index => $"line {index}"));
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(paste, result.Text);
        Assert.Contains(("[paste #1 +11 lines]", "[paste #1 +11 lines]".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_LargeSingleLinePasteRendersCharMarkerAndSubmitsExpandedText()
    {
        var paste = new string('x', 1001);
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(paste, result.Text);
        Assert.Contains(("[paste #1 1001 chars]", "[paste #1 1001 chars]".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_LeftArrowTreatsPasteMarkerAsAtomicSegment()
    {
        var paste = CreateLargePaste();
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.Enqueue('X');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("X" + paste, result.Text);
        Assert.Contains(("[paste #1 +11 lines]", 0), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_RightArrowTreatsPasteMarkerAsAtomicSegment()
    {
        var paste = CreateLargePaste();
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueKey(ConsoleKey.RightArrow);
        reader.Enqueue('X');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(paste + "X", result.Text);
    }

    [Fact]
    public async Task GetExpandedDraft_ExpandsPasteMarkersBeforeSubmit()
    {
        var paste = CreateLargePaste();
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x18', ConsoleKey.X, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        InteractiveInputEditor? editor = null;
        string? collapsedDraft = null;
        string? expandedDraft = null;
        editor = new InteractiveInputEditor(reader, renderer);
        editor.SetShortcutHandler((key, _) =>
        {
            if (key.Key != ConsoleKey.X || (key.Modifiers & ConsoleModifiers.Control) == 0)
            {
                return Task.FromResult(false);
            }

            collapsedDraft = editor.GetCollapsedDraft();
            expandedDraft = editor.GetExpandedDraft();
            return Task.FromResult(true);
        });

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(paste, result.Text);
        Assert.Equal("[paste #1 +11 lines]", collapsedDraft);
        Assert.Equal(paste, expandedDraft);
    }

    [Theory]
    [InlineData(ConsoleKey.Backspace, false)]
    [InlineData(ConsoleKey.Delete, true)]
    public async Task ReadLineAsync_DeleteCharTreatsPasteMarkerAsAtomicSegment(ConsoleKey key, bool moveHomeFirst)
    {
        var paste = CreateLargePaste();
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        if (moveHomeFirst)
        {
            reader.EnqueueKey(ConsoleKey.Home);
        }

        reader.EnqueueKey(key);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    [InlineData(ConsoleKey.Backspace, false)]
    [InlineData(ConsoleKey.Delete, true)]
    public async Task ReadLineAsync_DeleteWordTreatsPasteMarkerAsAtomicSegment(ConsoleKey key, bool moveHomeFirst)
    {
        var paste = CreateLargePaste();
        var reader = new FakeInputEventReader();
        reader.EnqueuePaste(paste);
        if (moveHomeFirst)
        {
            reader.EnqueueKey(ConsoleKey.Home);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_SmallPasteInsertsSanitizedTextDirectly()
    {
        var reader = new FakeInputEventReader();
        reader.Enqueue('a');
        reader.EnqueuePaste("\tpath\r\nnext\u0001");
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("a    path\nnext", result.Text);
    }

    private static string CreateLargePaste() =>
        string.Join("\n", Enumerable.Range(1, 11).Select(static index => $"line {index}"));

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
    public async Task ReadLineAsync_BackspaceRemovesPreviousGraphemeCluster()
    {
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueKey(ConsoleKey.Enter);

        var buffer = new InputBuffer();
        buffer.SetDraft("e\u0301");
        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, buffer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_DeleteRemovesNextTextElement()
    {
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueKey(ConsoleKey.Delete);
        reader.EnqueueKey(ConsoleKey.Enter);

        var buffer = new InputBuffer();
        buffer.SetDraft("😀a");
        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, buffer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("a", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_LeftArrowSkipsPreviousGraphemeCluster()
    {
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.Enqueue('X');
        reader.EnqueueKey(ConsoleKey.Enter);

        var buffer = new InputBuffer();
        buffer.SetDraft("e\u0301b");
        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, buffer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("Xe\u0301b", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_RightArrowSkipsNextGraphemeCluster()
    {
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueKey(ConsoleKey.RightArrow);
        reader.Enqueue('X');
        reader.EnqueueKey(ConsoleKey.Enter);

        var buffer = new InputBuffer();
        buffer.SetDraft("e\u0301b");
        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, buffer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("e\u0301Xb", result.Text);
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
    public async Task ReadLineAsync_ShiftTabCyclesAutocompleteBackwardAndWrapsFromLastItem()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('/');
        reader.Enqueue('m');
        reader.Enqueue('o');
        reader.EnqueueRaw(new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, autocompleteProvider: new FakeAutocompleteProvider());

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("/models ", result.Text);
        Assert.Contains(("/models ", "/models ".Length), renderer.RenderCalls);
        Assert.Contains(("/model ", "/model ".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlBackspaceClearsAutocompleteSession()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('/');
        reader.Enqueue('m');
        reader.Enqueue('o');
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, autocompleteProvider: new FakeAutocompleteProvider());

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("/model ", result.Text);
        Assert.Contains(("/model ", "/model ".Length), renderer.RenderCalls);
        Assert.DoesNotContain(renderer.RenderCalls, call => call.Buffer == "/models ");
    }

    [Fact]
    public async Task ReadLineAsync_TabAppliesFirstAutocompleteSuggestion()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('/');
        reader.Enqueue('m');
        reader.Enqueue('o');
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var autocomplete = new TuiCombinedAutocompleteProvider(
            [new TuiSlashCommand("model", "Select model")],
            basePath: Environment.CurrentDirectory);
        var editor = new InteractiveInputEditor(reader, renderer, autocompleteProvider: autocomplete);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("/model ", result.Text);
        Assert.Contains(("/model ", "/model ".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_RepeatedTabCyclesAutocompleteSuggestions()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('/');
        reader.Enqueue('m');
        reader.Enqueue('o');
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var autocomplete = new TuiCombinedAutocompleteProvider(
            [
                new TuiSlashCommand("model", "Select model"),
                new TuiSlashCommand("models", "List models")
            ],
            basePath: Environment.CurrentDirectory);
        var editor = new InteractiveInputEditor(reader, renderer, autocompleteProvider: autocomplete);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("/model ", result.Text);
        Assert.Contains(("/model ", "/model ".Length), renderer.RenderCalls);
        Assert.Contains(("/models ", "/models ".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlZUndoesAutocomplete()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('/');
        reader.Enqueue('m');
        reader.Enqueue('o');
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueKey(ConsoleKey.Tab);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x1A', ConsoleKey.Z, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var autocomplete = new TuiCombinedAutocompleteProvider(
            [
                new TuiSlashCommand("model", "Select model"),
                new TuiSlashCommand("models", "List models")
            ],
            basePath: Environment.CurrentDirectory);
        var editor = new InteractiveInputEditor(reader, renderer, autocompleteProvider: autocomplete);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("/mo", result.Text);
        Assert.Contains(("/models ", "/models ".Length), renderer.RenderCalls);
        Assert.Contains(("/mo", 3), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlZRestoresPreviousTextAndCursor()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.Enqueue('b');
        reader.Enqueue('c');
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.Enqueue('X');
        reader.EnqueueRaw(new ConsoleKeyInfo('\x1A', ConsoleKey.Z, shift: false, alt: false, control: true));
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("ab!c", result.Text);
        Assert.Contains(("abc", 2), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlUnderscoreRestoresKilledText()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "hello world")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x0B', ConsoleKey.K, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x1F', ConsoleKey.OemMinus, shift: true, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlZUndoesYankAndKeepsKillRingAvailable()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "hello")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x1A', ConsoleKey.Z, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_ShiftEnterInsertsNewLineAndPlainEnterSubmits()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        reader.Enqueue('b');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Submitted, result.Kind);
        Assert.Equal("a\nb", result.Text);
        Assert.Contains(("a\n", 2), renderer.RenderCalls);
        Assert.Equal(("a\nb", 3), renderer.RenderCalls[^1]);
        Assert.Equal("a\nb", history.Peek(0));
    }

    [Fact]
    public async Task ReadLineAsync_CtrlJInsertsNewLine()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a');
        reader.EnqueueRaw(new ConsoleKeyInfo('\n', ConsoleKey.J, shift: false, alt: false, control: true));
        reader.Enqueue('b');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("a\nb", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_BackslashEnterInsertsNewLineInsteadOfSubmitting()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one\\")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Enter);
        foreach (var ch in "two")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one\ntwo", result.Text);
        Assert.Contains(("one\n", 4), renderer.RenderCalls);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_CustomBindingMapDisablesShiftEnterNewLine()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('x');
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        reader.Enqueue('y');
        reader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.None)] = EditorAction.None,
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.Shift)] = EditorAction.None
        });
        var editor = new InteractiveInputEditor(reader, renderer, bindings: bindings);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Cancelled, result.Kind);
        Assert.Equal("xy", editor.Buffer.Draft);
    }

    [Fact]
    public async Task ReadLineAsync_HomeEndMoveWithinCurrentMultilineLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "three")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        foreach (var ch in "two ")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.End);
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one\ntwo three!", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_BackspaceAtLineStartMergesWithPreviousLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "two")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("onetwo", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_DeleteAtLineEndMergesNextLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "two")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.Delete);
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("onetwo", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_UpArrowMovesWithinMultilineBufferBeforeHistory()
    {
        var history = new InputHistory();
        history.Add("old command");

        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "two")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one!\ntwo", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_UpArrowPreservesColumnAcrossShortLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "2222222222x222")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "1111111111_111111111111")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        for (var i = 0; i < 10; i++)
        {
            reader.EnqueueKey(ConsoleKey.RightArrow);
        }
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("2222222222!x222\n\n1111111111_111111111111", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_DownArrowPreservesColumnAcrossShortLine()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "1111111111_111")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "2222222222x222222222222")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueKey(ConsoleKey.Home);
        for (var i = 0; i < 10; i++)
        {
            reader.EnqueueKey(ConsoleKey.RightArrow);
        }
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.Enqueue('!');
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("1111111111_111\n\n2222222222!x222222222222", result.Text);
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
    public async Task ReadLineAsync_ModelCycleActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x10', ConsoleKey.P, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.CycleModelForward, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_ModelSelectActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x0C', ConsoleKey.L, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.SelectModel, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_ToggleThinkingBlockActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x14', ConsoleKey.T, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.ToggleThinkingBlock, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_ToggleToolOutputExpansionActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x0F', ConsoleKey.O, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.ToggleToolOutputExpansion, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_OpenExternalEditorActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x07', ConsoleKey.G, shift: false, alt: false, control: true));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.OpenExternalEditor, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_QueueFollowUpMessageActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: false, alt: true, control: false));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.QueueFollowUpMessage, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
    }

    [Fact]
    public async Task ReadLineAsync_RestoreQueuedMessagesActionReturnsActionAndPreservesDraft()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "draft")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: false, alt: true, control: false));

        var renderer = new FakeRenderer();
        var history = new InputHistory();
        var editor = new InteractiveInputEditor(reader, renderer, history: history);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.RestoreQueuedMessages, result.Action);
        Assert.Null(result.Text);
        Assert.Equal("draft", editor.Buffer.Draft);
        Assert.Equal(0, history.Count);
        Assert.Equal(1, renderer.CommitCalls);
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

    [Fact]
    public async Task ReadLineAsync_CtrlBackspaceStopsAtAsciiPunctuationBoundary()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha.beta")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("alpha.", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlDeleteStopsAtAsciiPunctuationBoundary()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "alpha.beta")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(".beta", result.Text);
    }

    [Theory]
    [InlineData("hello world", 11, 6)]
    [InlineData("hello world", 6, 0)]
    [InlineData("hello", 5, 0)]
    [InlineData("", 0, 0)]
    [InlineData("  spaces", 8, 2)]
    [InlineData("alpha.beta", 10, 6)]
    [InlineData("alpha.beta", 6, 5)]
    [InlineData("alpha::beta", 7, 5)]
    [InlineData("e\u0301/foo", 2, 0)]
    [InlineData("你好abc", 2, 1)]
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
    [InlineData("alpha.beta", 0, 5)]
    [InlineData("alpha.beta", 5, 6)]
    [InlineData("alpha::beta", 5, 7)]
    [InlineData("e\u0301/foo", 0, 2)]
    [InlineData("你好abc", 0, 1)]
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
    public async Task ReadLineAsync_CtrlKKillsToEndOfCurrentLineOnly()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "two three")
        {
            reader.Enqueue(ch);
        }

        for (var i = 0; i < "three".Length; i++)
        {
            reader.EnqueueKey(ConsoleKey.LeftArrow);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x0B', ConsoleKey.K, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one\ntwo ", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlUKillsToStartOfCurrentLineOnly()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false));
        foreach (var ch in "two three")
        {
            reader.Enqueue(ch);
        }

        for (var i = 0; i < "three".Length; i++)
        {
            reader.EnqueueKey(ConsoleKey.LeftArrow);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one\nthree", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlKYanksKilledText()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "hello world")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\x0B', ConsoleKey.K, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlUYanksKilledTextBackAtCursor()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "hello world")
        {
            reader.Enqueue(ch);
        }

        for (var i = 0; i < "world".Length; i++)
        {
            reader.EnqueueKey(ConsoleKey.LeftArrow);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlBackspaceAccumulatesBackwardKillsForYank()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one two three")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one two three", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_CtrlDeleteAccumulatesForwardKillsForYank()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one two three")
        {
            reader.Enqueue(ch);
        }

        reader.EnqueueKey(ConsoleKey.Home);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("one two three", result.Text);
    }

    [Fact]
    public async Task ReadLineAsync_AltYCyclesKillRingAfterYank()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "first")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));

        foreach (var ch in "second")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));

        foreach (var ch in "third")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));

        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('y', ConsoleKey.Y, shift: false, alt: true, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('y', ConsoleKey.Y, shift: false, alt: true, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("first", result.Text);
        Assert.Contains(("third", "third".Length), renderer.RenderCalls);
        Assert.Contains(("second", "second".Length), renderer.RenderCalls);
    }

    [Fact]
    public async Task ReadLineAsync_NonYankActionBreaksAltYChain()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "first")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));

        foreach (var ch in "second")
        {
            reader.Enqueue(ch);
        }
        reader.EnqueueRaw(new ConsoleKeyInfo('\x15', ConsoleKey.U, shift: false, alt: false, control: true));

        reader.EnqueueRaw(new ConsoleKeyInfo('\x19', ConsoleKey.Y, shift: false, alt: false, control: true));
        reader.Enqueue('x');
        reader.EnqueueRaw(new ConsoleKeyInfo('y', ConsoleKey.Y, shift: false, alt: true, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(reader, renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal("secondx", result.Text);
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

    private sealed class FakeInputEventReader : IConsoleInputEventReader
    {
        private readonly Queue<ConsoleInputEvent> _events = new();

        public void Enqueue(char ch)
        {
            var key = new ConsoleKeyInfo(ch, ConsoleKey.NoName, shift: false, alt: false, control: false);
            _events.Enqueue(ConsoleInputEvent.KeyPress(key));
        }

        public void EnqueueKey(ConsoleKey key)
        {
            var keyInfo = new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false);
            _events.Enqueue(ConsoleInputEvent.KeyPress(keyInfo));
        }

        public void EnqueueRaw(ConsoleKeyInfo key)
        {
            _events.Enqueue(ConsoleInputEvent.KeyPress(key));
        }

        public void EnqueuePaste(string text)
        {
            _events.Enqueue(ConsoleInputEvent.Paste(text));
        }

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            var inputEvent = ReadEvent();
            if (inputEvent.Kind != ConsoleInputEventKind.KeyPress)
            {
                throw new InvalidOperationException("Expected a key event.");
            }

            return ValueTask.FromResult(inputEvent.Key);
        }

        public ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadEvent());

        private ConsoleInputEvent ReadEvent()
        {
            if (_events.Count == 0)
            {
                throw new InvalidOperationException("No more queued input.");
            }

            return _events.Dequeue();
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

    private sealed class FakeAutocompleteProvider : ITuiAutocompleteProvider
    {
        public ValueTask<TuiAutocompleteSuggestions?> GetSuggestionsAsync(
            string text,
            int cursorIndex,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            if (text == "/mo")
            {
                return ValueTask.FromResult<TuiAutocompleteSuggestions?>(
                    new TuiAutocompleteSuggestions(
                        [
                            new TuiAutocompleteItem("/model ", "model"),
                            new TuiAutocompleteItem("/models ", "models")
                        ],
                        "/mo"));
            }

            if (text == "/")
            {
                return ValueTask.FromResult<TuiAutocompleteSuggestions?>(
                    new TuiAutocompleteSuggestions(
                        [new TuiAutocompleteItem("/model ", "model")],
                        "/"));
            }

            if (string.IsNullOrEmpty(text))
            {
                return ValueTask.FromResult<TuiAutocompleteSuggestions?>(
                    new TuiAutocompleteSuggestions(
                        [new TuiAutocompleteItem("/model ", "model")],
                        string.Empty));
            }

            return ValueTask.FromResult<TuiAutocompleteSuggestions?>(null);
        }

        public TuiCompletionResult ApplyCompletion(
            string text,
            int cursorIndex,
            TuiAutocompleteItem item,
            string prefix) =>
            new(item.Value, item.Value.Length);
    }
}
