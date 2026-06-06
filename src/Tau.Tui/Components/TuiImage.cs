using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiImageTheme
{
    public Func<string, string> FallbackColor { get; init; } = static value => value;
}

public sealed record TuiImageOptions
{
    public int? MaxWidthCells { get; init; }
    public int? MaxHeightCells { get; init; }
    public string? Filename { get; init; }
    public long? ImageId { get; init; }
}

public sealed class TuiImage : ITuiComponent
{
    private readonly string _base64Data;
    private readonly string _mimeType;
    private readonly TuiImageTheme _theme;
    private readonly TuiImageOptions _options;
    private readonly TuiImageDimensions _dimensions;
    private long? _imageId;
    private int? _cachedWidth;
    private IReadOnlyList<string>? _cachedLines;

    public TuiImage(
        string base64Data,
        string mimeType,
        TuiImageTheme? theme = null,
        TuiImageOptions? options = null,
        TuiImageDimensions? dimensions = null)
    {
        _base64Data = base64Data;
        _mimeType = mimeType;
        _theme = theme ?? new TuiImageTheme();
        _options = options ?? new TuiImageOptions();
        _dimensions = dimensions ??
            TuiTerminalImage.GetImageDimensions(base64Data, mimeType) ??
            new TuiImageDimensions(800, 600);
        _imageId = _options.ImageId;
    }

    public long? ImageId => _imageId;

    public TuiImageDimensions Dimensions => _dimensions;

    public void Invalidate()
    {
        _cachedWidth = null;
        _cachedLines = null;
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        if (_cachedLines is not null && _cachedWidth == width)
        {
            return _cachedLines;
        }

        var maxWidth = Math.Max(1, Math.Min(width - 2, _options.MaxWidthCells ?? 60));
        var result = TuiTerminalImage.RenderImage(
            _base64Data,
            _dimensions,
            new TuiImageRenderOptions
            {
                MaxWidthCells = maxWidth,
                MaxHeightCells = _options.MaxHeightCells,
                ImageId = _imageId,
            });

        IReadOnlyList<string> lines;
        if (result is not null)
        {
            if (result.ImageId is not null)
            {
                _imageId = result.ImageId;
            }

            var rendered = new List<string>();
            for (var i = 0; i < result.Rows - 1; i++)
            {
                rendered.Add(string.Empty);
            }

            var moveUp = result.Rows > 1 ? $"\u001b[{result.Rows - 1}A" : string.Empty;
            rendered.Add(moveUp + result.Sequence);
            lines = rendered;
        }
        else
        {
            var fallback = TuiTerminalImage.ImageFallback(_mimeType, _dimensions, _options.Filename);
            lines = [_theme.FallbackColor(fallback)];
        }

        _cachedWidth = width;
        _cachedLines = lines;
        return lines;
    }
}
