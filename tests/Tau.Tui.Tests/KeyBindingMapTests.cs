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
    public void Default_MapsControlBackspaceToDeletePrevWord()
    {
        var info = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: true);
        Assert.Equal(EditorAction.DeletePrevWord, KeyBindingMap.Default.Resolve(info));
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
