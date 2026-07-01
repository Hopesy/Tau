using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public class KeyBindingMapTests
{
    [Fact]
    public void Default_MapsControlCToCancel()
    {
        var info = new ConsoleKeyInfo('', ConsoleKey.C, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.Cancel, KeyBindingMap.Default.Resolve(info));
    }

    [Fact]
    public void Default_MapsEnterToSubmit()
    {
        var info = new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.Submit, KeyBindingMap.Default.Resolve(info));
    }

    [Fact]
    public void Default_MapsShiftEnterAndControlJToNewLine()
    {
        var shiftEnter = new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: true, alt: false, control: false);
        var controlJ = new ConsoleKeyInfo('\n', ConsoleKey.J, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.NewLine, KeyBindingMap.Default.Resolve(shiftEnter));
        Assert.Equal(EditorAction.NewLine, KeyBindingMap.Default.Resolve(controlJ));
    }

    [Fact]
    public void Default_MapsControlBackspaceToDeletePrevWord()
    {
        var info = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.DeletePrevWord, KeyBindingMap.Default.Resolve(info));
    }

    [Fact]
    public void Default_MapsModelCycleKeys()
    {
        var forward = new ConsoleKeyInfo('\x10', ConsoleKey.P, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.CycleModelForward, KeyBindingMap.Default.Resolve(forward));

        var backward = new ConsoleKeyInfo('\x10', ConsoleKey.P, shift: true, alt: false, control: true);
        Assert.Equal(EditorAction.CycleModelBackward, KeyBindingMap.Default.Resolve(backward));
    }

    [Fact]
    public void Default_MapsModelSelectKey()
    {
        var select = new ConsoleKeyInfo('\x0C', ConsoleKey.L, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.SelectModel, KeyBindingMap.Default.Resolve(select));
    }

    [Fact]
    public void Default_MapsPasteImageKey()
    {
        var pasteImage = new ConsoleKeyInfo('\x16', ConsoleKey.V, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.PasteImage, KeyBindingMap.Default.Resolve(pasteImage));
    }

    [Fact]
    public void Default_MapsToggleThinkingBlockKey()
    {
        var toggleThinking = new ConsoleKeyInfo('\x14', ConsoleKey.T, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.ToggleThinkingBlock, KeyBindingMap.Default.Resolve(toggleThinking));
    }

    [Fact]
    public void Default_MapsToggleToolOutputExpansionKey()
    {
        var toggleTools = new ConsoleKeyInfo('\x0F', ConsoleKey.O, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.ToggleToolOutputExpansion, KeyBindingMap.Default.Resolve(toggleTools));
    }

    [Fact]
    public void Default_MapsOpenExternalEditorKey()
    {
        var externalEditor = new ConsoleKeyInfo('\x07', ConsoleKey.G, shift: false, alt: false, control: true);

        Assert.Equal(EditorAction.OpenExternalEditor, KeyBindingMap.Default.Resolve(externalEditor));
    }

    [Fact]
    public void Default_MapsQueueFollowUpMessageKey()
    {
        var followUp = new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: false, alt: true, control: false);

        Assert.Equal(EditorAction.QueueFollowUpMessage, KeyBindingMap.Default.Resolve(followUp));
    }

    [Fact]
    public void Default_MapsRestoreQueuedMessagesKey()
    {
        var restoreQueued = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: false, alt: true, control: false);

        Assert.Equal(EditorAction.RestoreQueuedMessages, KeyBindingMap.Default.Resolve(restoreQueued));
    }

    [Fact]
    public void Default_MapsTabToComplete()
    {
        var complete = new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: false, alt: false, control: false);

        Assert.Equal(EditorAction.Complete, KeyBindingMap.Default.Resolve(complete));
    }

    [Fact]
    public void Default_MapsShiftTabToCompletePrevious()
    {
        var completePrevious = new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false);

        Assert.Equal(EditorAction.CompletePrevious, KeyBindingMap.Default.Resolve(completePrevious));
    }

    [Fact]
    public void Default_ReturnsNoneForUnboundKey()
    {
        var info = new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.None, KeyBindingMap.Default.Resolve(info));
    }

    [Fact]
    public void WithOverrides_AddsAndReplacesBindings()
    {
        var map = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
        {
            [new KeyBinding(ConsoleKey.F1, ConsoleModifiers.None)] = EditorAction.Cancel,
            [new KeyBinding(ConsoleKey.Enter, ConsoleModifiers.None)] = EditorAction.None
        });

        var f1 = new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.Cancel, map.Resolve(f1));

        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.None, map.Resolve(enter));
    }

    [Fact]
    public void FileStore_ReturnsDefaultWhenPathMissing()
    {
        var map = KeyBindingFileStore.LoadOrDefault(null);
        Assert.Same(KeyBindingMap.Default, map);

        var nonexistent = Path.Combine(Path.GetTempPath(), $"tau-keybindings-missing-{Guid.NewGuid():N}.json");
        Assert.Same(KeyBindingMap.Default, KeyBindingFileStore.LoadOrDefault(nonexistent));
    }

    [Fact]
    public void FileStore_ParsesJsonBindings()
    {
        var json = """
        {
          "bindings": [
            { "key": "F1", "modifiers": [], "action": "Cancel" },
            { "key": "B", "modifiers": ["Control"], "action": "CursorLeft" }
          ]
        }
        """;

        var map = KeyBindingFileStore.Parse(json);
        var f1 = new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.Cancel, map.Resolve(f1));
        var ctrlB = new ConsoleKeyInfo('', ConsoleKey.B, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.CursorLeft, map.Resolve(ctrlB));
        var ctrlC = new ConsoleKeyInfo('', ConsoleKey.C, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.Cancel, map.Resolve(ctrlC));
    }

    [Fact]
    public void FileStore_FallsBackToDefaultOnMalformedJson()
    {
        var map = KeyBindingFileStore.Parse("{ \"bindings\": [ ] }");
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.Submit, map.Resolve(enter));
    }

    [Fact]
    public void FileStore_IgnoresUnknownKeyOrAction()
    {
        var json = """
        {
          "bindings": [
            { "key": "BogusKey", "action": "Cancel" },
            { "key": "Enter", "action": "TimeTravel" }
          ]
        }
        """;
        var map = KeyBindingFileStore.Parse(json);
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
        Assert.Equal(EditorAction.Submit, map.Resolve(enter));
    }
}
