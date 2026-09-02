using Tau.Tui;

namespace Tau.Tui.Tests;

public sealed class CurrentReferenceParityTests
{
    [Fact]
    public void Input_EmitsCursorMarkerWhenFocused()
    {
        var input = new Input("hello") { Focused = true };
        Assert.Contains(TuiMarkers.CURSOR_MARKER, input.Render(80)[0]);
    }

    [Fact]
    public void VStack_RendersChildrenInOrder()
    {
        var stack = new VStack();
        stack.Add(new Input("one"));
        stack.Add(new Input("two"));
        Assert.Equal(["one", "two"], stack.Render(20));
    }
}
