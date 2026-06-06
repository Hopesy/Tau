using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiTruncatedText : ITuiComponent
{
    private readonly int _paddingX;
    private readonly int _paddingY;
    private string _text;

    public TuiTruncatedText(string text, int paddingX = 0, int paddingY = 0)
    {
        _text = text;
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
    }

    public string Text => _text;

    public void SetText(string text) => _text = text;

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var result = new List<string>();
        var emptyLine = new string(' ', width);

        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        var availableWidth = Math.Max(1, width - (_paddingX * 2));
        var displayText = TuiText.TruncateToWidth(FirstLineOnly(_text), availableWidth);
        var paddedLine = new string(' ', _paddingX) + displayText + new string(' ', _paddingX);
        result.Add(TuiText.PadRightToWidth(paddedLine, width));

        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        return result;
    }

    private static string FirstLineOnly(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }
}
