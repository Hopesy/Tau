using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed record TuiToolExecutionTheme(
    Func<string, string>? PendingBackground = null,
    Func<string, string>? SuccessBackground = null,
    Func<string, string>? ErrorBackground = null);

public abstract record TuiToolResultBlock(string Type);

public sealed record TuiToolTextBlock(string Text) : TuiToolResultBlock("text");

public sealed record TuiToolImageBlock(string Data, string MimeType) : TuiToolResultBlock("image");

public sealed record TuiToolExecutionResult(
    IReadOnlyList<TuiToolResultBlock> Content,
    bool IsError = false,
    object? Details = null);

public sealed partial class TuiToolExecution : ITuiComponent
{
    private readonly string _toolName;
    private readonly string _toolCallId;
    private readonly TuiToolExecutionTheme _theme;
    private object? _args;
    private bool _expanded;
    private bool _showImages;
    private int _imageWidthCells;
    private bool _isPartial = true;
    private bool _executionStarted;
    private bool _argsComplete;
    private TuiToolExecutionResult? _result;

    public TuiToolExecution(
        string toolName,
        string toolCallId,
        object? args = null,
        TuiToolExecutionTheme? theme = null,
        bool showImages = true,
        int imageWidthCells = 60)
    {
        _toolName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
        _toolCallId = toolCallId ?? string.Empty;
        _args = args;
        _theme = theme ?? new TuiToolExecutionTheme();
        _showImages = showImages;
        _imageWidthCells = Math.Max(1, imageWidthCells);
    }

    public string ToolName => _toolName;
    public string ToolCallId => _toolCallId;
    public bool Expanded => _expanded;
    public bool ShowImages => _showImages;
    public int ImageWidthCells => _imageWidthCells;
    public bool IsPartial => _isPartial;
    public bool ExecutionStarted => _executionStarted;
    public bool ArgsComplete => _argsComplete;
    public TuiToolExecutionResult? Result => _result;

    public void UpdateArgs(object? args)
    {
        _args = args;
    }

    public void MarkExecutionStarted()
    {
        _executionStarted = true;
    }

    public void SetArgsComplete()
    {
        _argsComplete = true;
    }

    public void UpdateResult(TuiToolExecutionResult result, bool isPartial = false)
    {
        _result = result;
        _isPartial = isPartial;
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
    }

    public void SetShowImages(bool showImages)
    {
        _showImages = showImages;
    }

    public void SetImageWidthCells(int imageWidthCells)
    {
        _imageWidthCells = Math.Max(1, imageWidthCells);
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var text = FormatToolExecution();
        var imageLines = RenderImageBlocks(width);
        if (string.IsNullOrWhiteSpace(text) && imageLines.Count == 0)
        {
            return [];
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var contentWidth = Math.Max(1, width - 2);
            lines.Add(FormatLine(string.Empty, width));

            foreach (var line in TuiText.WrapTextWithAnsi(text, contentWidth))
            {
                lines.Add(FormatLine(" " + line, width));
            }

            lines.Add(FormatLine(string.Empty, width));
        }

        if (imageLines.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(imageLines);
        }

        return lines;
    }

    private string FormatToolExecution()
    {
        var builder = new StringBuilder(_toolName);
        var args = FormatArgs(_args);
        if (!string.IsNullOrWhiteSpace(args))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(args);
        }

        var output = GetTextOutput(_result, _showImages);
        if (!string.IsNullOrEmpty(output))
        {
            builder.AppendLine();
            builder.Append(output);
        }

        return builder.ToString();
    }

    private string FormatLine(string line, int width)
    {
        var padded = TuiText.PadRightToWidth(line, width);
        return CurrentBackgroundFormatter() is { } formatter
            ? formatter(padded)
            : padded;
    }

    private IReadOnlyList<string> RenderImageBlocks(int width)
    {
        if (_result is null || !_showImages)
        {
            return [];
        }

        var capabilities = TuiTerminalImage.GetCapabilities();
        if (capabilities.Images == TuiImageProtocol.None)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var block in _result.Content.OfType<TuiToolImageBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Data))
            {
                continue;
            }

            var mimeType = string.IsNullOrWhiteSpace(block.MimeType)
                ? "image/unknown"
                : block.MimeType;
            if (capabilities.Images == TuiImageProtocol.Kitty &&
                !string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            var dimensions = TuiTerminalImage.GetImageDimensions(block.Data, mimeType);
            var image = new TuiImage(
                block.Data,
                mimeType,
                options: new TuiImageOptions { MaxWidthCells = _imageWidthCells },
                dimensions: dimensions);
            lines.AddRange(image.Render(width));
        }

        return lines;
    }

    private Func<string, string>? CurrentBackgroundFormatter()
    {
        if (_isPartial)
        {
            return _theme.PendingBackground;
        }

        return _result?.IsError == true
            ? _theme.ErrorBackground
            : _theme.SuccessBackground;
    }

    private static string FormatArgs(object? args)
    {
        if (args is null)
        {
            return string.Empty;
        }

        if (args is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? string.Empty
                : FormatJsonElement(element);
        }

        if (args is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return FormatJsonElement(document.RootElement);
            }
            catch (JsonException)
            {
                return text;
            }
        }

        return Convert.ToString(args, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatJsonElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string GetTextOutput(TuiToolExecutionResult? result, bool showImages = true)
    {
        if (result is null)
        {
            return string.Empty;
        }

        var textBlocks = result.Content.OfType<TuiToolTextBlock>()
            .Select(static block => NormalizeTextOutput(block.Text))
            .Where(static text => text.Length > 0);
        var output = string.Join('\n', textBlocks);

        var imageBlocks = result.Content.OfType<TuiToolImageBlock>().ToArray();
        var capabilities = TuiTerminalImage.GetCapabilities();
        if (imageBlocks.Length > 0 && (!showImages || capabilities.Images == TuiImageProtocol.None))
        {
            var imageIndicators = imageBlocks.Select(static image =>
            {
                var mimeType = string.IsNullOrWhiteSpace(image.MimeType) ? "image/unknown" : image.MimeType;
                var dimensions = string.IsNullOrWhiteSpace(image.Data)
                    ? null
                    : TuiTerminalImage.GetImageDimensions(image.Data, mimeType);
                return TuiTerminalImage.ImageFallback(mimeType, dimensions);
            });
            output = output.Length > 0
                ? $"{output}\n{string.Join('\n', imageIndicators)}"
                : string.Join('\n', imageIndicators);
        }

        return output;
    }

    private static string NormalizeTextOutput(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = AnsiRegex()
            .Replace(text, string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        return SanitizeBinaryOutput(stripped);
    }

    private static string SanitizeBinaryOutput(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\t' or '\n')
            {
                builder.Append(ch);
                continue;
            }

            if (char.IsSurrogate(ch) ||
                char.GetUnicodeCategory(ch) == UnicodeCategory.Format ||
                ch <= '\u001f' ||
                ch is >= '\ufff9' and <= '\ufffb')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiRegex();
}
