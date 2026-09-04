using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

/// <summary>
/// 定义工具执行各阶段的背景和标题格式化函数。
/// </summary>
/// <param name="PendingBackground">工具运行期间的整行样式格式化函数。</param>
/// <param name="SuccessBackground">工具成功完成后的整行样式格式化函数。</param>
/// <param name="ErrorBackground">工具失败后的整行样式格式化函数。</param>
/// <param name="TitleFormatter">工具生命周期标题的格式化函数。</param>
/// <param name="PanelRenderer">可选的工具 Panel 渲染函数。</param>
public sealed record TuiToolExecutionTheme(
    Func<string, string>? PendingBackground = null,
    Func<string, string>? SuccessBackground = null,
    Func<string, string>? ErrorBackground = null,
    Func<string, string>? TitleFormatter = null,
    Func<string, string, int, IReadOnlyList<string>>? PanelRenderer = null)
{
    /// <summary>
    /// 使用状态符号和低饱和背景区分工具执行阶段。
    /// </summary>
    public static TuiToolExecutionTheme Agent { get; } = new(
        PendingBackground: static value => $"\u001b[38;5;252;48;5;236m{value}\u001b[39;49m",
        SuccessBackground: static value => $"\u001b[38;5;255;48;5;22m{value}\u001b[39;49m",
        ErrorBackground: static value => $"\u001b[38;5;255;48;5;52m{value}\u001b[39;49m",
        TitleFormatter: static value => $"\u001b[1;38;5;214m{value}\u001b[22;39m",
        PanelRenderer: static (title, content, width) =>
            TuiSpectreRenderer.Default.RenderPanel(title, content, width));
}

public abstract record TuiToolResultBlock(string Type);

public sealed record TuiToolTextBlock(string Text) : TuiToolResultBlock("text");

public sealed record TuiToolImageBlock(string Data, string MimeType) : TuiToolResultBlock("image");

public sealed record TuiToolExecutionResult(
    IReadOnlyList<TuiToolResultBlock> Content,
    bool IsError = false,
    object? Details = null);

public sealed partial class TuiToolExecution : ITuiComponent
{
    public const int DefaultPreviewLines = 20;
    private readonly string _toolName;
    private readonly string _toolCallId;
    private readonly TuiToolExecutionTheme _theme;
    private object? _args;
    private bool _expanded;
    private int _previewLines = DefaultPreviewLines;
    private bool _showImages;
    private int _imageWidthCells;
    private bool _isPartial = true;
    private bool _executionStarted;
    private bool _argsComplete;
    private TuiToolExecutionResult? _result;
    private string? _expandKeyHint;

    /// <summary>
    /// 创建工具执行显示组件。
    /// </summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="toolCallId">工具调用标识。</param>
    /// <param name="args">工具调用参数。</param>
    /// <param name="theme">工具生命周期和标题的显示主题。</param>
    /// <param name="showImages">是否显示工具结果中的图片。</param>
    /// <param name="imageWidthCells">图片预览的目标列宽。</param>
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
    public int PreviewLines => _previewLines;
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

    public void SetExpandKeyHint(string? keyText)
    {
        _expandKeyHint = string.IsNullOrWhiteSpace(keyText) ? null : keyText.Trim();
    }

    public void SetPreviewLines(int previewLines)
    {
        _previewLines = Math.Max(1, previewLines);
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
            if (_theme.PanelRenderer is { } panelRenderer)
            {
                lines.AddRange(panelRenderer(FormatToolTitle(styled: false), FormatToolBody(), width));
            }
            else
            {
                RenderTextBlock(lines, text, width);
            }
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

    /// <summary>
    /// 将工具文本内容按当前主题渲染为行式布局。
    /// </summary>
    /// <param name="lines">接收渲染结果的行集合。</param>
    /// <param name="text">包含标题、参数和输出的工具文本。</param>
    /// <param name="width">目标终端列数。</param>
    private void RenderTextBlock(List<string> lines, string text, int width)
    {
        var contentWidth = Math.Max(1, width - 2);
        lines.Add(FormatLine(string.Empty, width));

        foreach (var line in TuiText.WrapTextWithAnsi(text, contentWidth))
        {
            lines.Add(FormatLine(" " + line, width));
        }

        lines.Add(FormatLine(string.Empty, width));
    }

    private string FormatToolTitle(bool styled)
    {
        var title = _isPartial
            ? $"⏳ {_toolName}"
            : _result?.IsError == true
                ? $"✗ {_toolName}"
                : $"✓ {_toolName}";
        if (!styled)
        {
            return title;
        }

        return _theme.TitleFormatter?.Invoke(title) ?? _toolName;
    }

    private string FormatToolBody()
    {
        var builder = new StringBuilder();
        var args = FormatArgs(_args);
        if (!string.IsNullOrWhiteSpace(args))
        {
            builder.Append(args);
        }

        var output = GetTextOutput(_result, _showImages);
        if (!string.IsNullOrEmpty(output))
        {
            var outputLines = FormatTextOutput(output);
            if (outputLines.Count > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.AppendJoin('\n', outputLines);
            }
        }

        return builder.ToString();
    }

    private string FormatToolExecution()
    {
        var title = FormatToolTitle(styled: true);
        var body = FormatToolBody();
        if (body.Length == 0)
        {
            return title;
        }

        return $"{title}\n\n{body}";
    }

    private IReadOnlyList<string> FormatTextOutput(string output)
    {
        if (_expanded)
        {
            return output.Split('\n');
        }

        var logicalLines = output.Split('\n');
        if (logicalLines.Length <= _previewLines)
        {
            return logicalLines;
        }

        var hiddenCount = logicalLines.Length - _previewLines;
        return logicalLines
            .Skip(hiddenCount)
            .Concat([$"... {hiddenCount} more lines ({ExpandHintText()})"])
            .ToArray();
    }

    private string ExpandHintText() =>
        _expandKeyHint is null ? "expand to view" : $"{_expandKeyHint} to expand";

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
