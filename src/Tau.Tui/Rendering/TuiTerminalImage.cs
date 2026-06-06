using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;

namespace Tau.Tui.Rendering;

public enum TuiImageProtocol
{
    None,
    Kitty,
    ITerm2,
}

public readonly record struct TuiTerminalCapabilities(
    TuiImageProtocol Images,
    bool TrueColor,
    bool Hyperlinks);

public readonly record struct TuiCellDimensions(int WidthPx, int HeightPx);

public readonly record struct TuiImageDimensions(int WidthPx, int HeightPx);

public sealed record TuiImageRenderOptions
{
    public int? MaxWidthCells { get; init; }
    public int? MaxHeightCells { get; init; }
    public bool PreserveAspectRatio { get; init; } = true;
    public long? ImageId { get; init; }
}

public sealed record TuiImageRenderResult(string Sequence, int Rows, long? ImageId = null);

public static class TuiTerminalImage
{
    public const string KittyPrefix = "\u001b_G";
    public const string ITerm2Prefix = "\u001b]1337;File=";

    private const int KittyChunkSize = 4096;
    private static readonly object Gate = new();
    private static TuiTerminalCapabilities? _cachedCapabilities;
    private static TuiCellDimensions _cellDimensions = new(9, 18);

    public static TuiCellDimensions GetCellDimensions() => _cellDimensions;

    public static void SetCellDimensions(TuiCellDimensions dimensions)
    {
        if (dimensions.WidthPx <= 0 || dimensions.HeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Cell dimensions must be positive.");
        }

        _cellDimensions = dimensions;
    }

    public static TuiTerminalCapabilities DetectCapabilities(IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment = environment is null
            ? CaptureEnvironment()
            : NormalizeEnvironment(environment);

        var termProgram = Get(environment, "TERM_PROGRAM").ToLowerInvariant();
        var term = Get(environment, "TERM").ToLowerInvariant();
        var colorTerm = Get(environment, "COLORTERM").ToLowerInvariant();
        var trueColor = colorTerm is "truecolor" or "24bit";
        var inTmuxOrScreen = Has(environment, "TMUX") ||
            term.StartsWith("tmux", StringComparison.Ordinal) ||
            term.StartsWith("screen", StringComparison.Ordinal);

        if (inTmuxOrScreen)
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.None, trueColor, Hyperlinks: false);
        }

        if (Has(environment, "KITTY_WINDOW_ID") || termProgram == "kitty")
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true);
        }

        if (termProgram == "ghostty" || term.Contains("ghostty", StringComparison.Ordinal) || Has(environment, "GHOSTTY_RESOURCES_DIR"))
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true);
        }

        if (Has(environment, "WEZTERM_PANE") || termProgram == "wezterm")
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true);
        }

        if (Has(environment, "ITERM_SESSION_ID") || termProgram == "iterm.app")
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.ITerm2, TrueColor: true, Hyperlinks: true);
        }

        if (termProgram is "vscode" or "alacritty")
        {
            return new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: true, Hyperlinks: true);
        }

        return new TuiTerminalCapabilities(TuiImageProtocol.None, trueColor, Hyperlinks: false);
    }

    public static TuiTerminalCapabilities GetCapabilities()
    {
        lock (Gate)
        {
            _cachedCapabilities ??= DetectCapabilities();
            return _cachedCapabilities.Value;
        }
    }

    public static void ResetCapabilitiesCache()
    {
        lock (Gate)
        {
            _cachedCapabilities = null;
        }
    }

    public static void SetCapabilities(TuiTerminalCapabilities capabilities)
    {
        lock (Gate)
        {
            _cachedCapabilities = capabilities;
        }
    }

    public static bool IsImageLine(string? line) =>
        !string.IsNullOrEmpty(line) &&
        (line.StartsWith(KittyPrefix, StringComparison.Ordinal) ||
         line.StartsWith(ITerm2Prefix, StringComparison.Ordinal) ||
         line.Contains(KittyPrefix, StringComparison.Ordinal) ||
         line.Contains(ITerm2Prefix, StringComparison.Ordinal));

    public static long AllocateImageId() =>
        Random.Shared.NextInt64(1, 0xffffffffL);

    public static string EncodeKitty(
        string base64Data,
        int? columns = null,
        int? rows = null,
        long? imageId = null)
    {
        var parameters = new List<string> { "a=T", "f=100", "q=2" };
        if (columns is > 0)
        {
            parameters.Add($"c={columns.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (rows is > 0)
        {
            parameters.Add($"r={rows.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (imageId is > 0)
        {
            parameters.Add($"i={imageId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (base64Data.Length <= KittyChunkSize)
        {
            return $"\u001b_G{string.Join(',', parameters)};{base64Data}\u001b\\";
        }

        var builder = new StringBuilder();
        var offset = 0;
        var isFirst = true;
        while (offset < base64Data.Length)
        {
            var length = Math.Min(KittyChunkSize, base64Data.Length - offset);
            var chunk = base64Data.Substring(offset, length);
            var isLast = offset + length >= base64Data.Length;

            if (isFirst)
            {
                builder.Append("\u001b_G");
                builder.Append(string.Join(',', parameters));
                builder.Append(",m=1;");
                builder.Append(chunk);
                builder.Append("\u001b\\");
                isFirst = false;
            }
            else if (isLast)
            {
                builder.Append("\u001b_Gm=0;");
                builder.Append(chunk);
                builder.Append("\u001b\\");
            }
            else
            {
                builder.Append("\u001b_Gm=1;");
                builder.Append(chunk);
                builder.Append("\u001b\\");
            }

            offset += length;
        }

        return builder.ToString();
    }

    public static string DeleteKittyImage(long imageId) =>
        $"\u001b_Ga=d,d=I,i={imageId.ToString(CultureInfo.InvariantCulture)}\u001b\\";

    public static string DeleteAllKittyImages() =>
        "\u001b_Ga=d,d=A\u001b\\";

    public static string EncodeITerm2(
        string base64Data,
        string? width = null,
        string? height = null,
        string? name = null,
        bool preserveAspectRatio = true,
        bool inline = true)
    {
        var parameters = new List<string> { $"inline={(inline ? 1 : 0)}" };
        if (width is not null)
        {
            parameters.Add($"width={width}");
        }

        if (height is not null)
        {
            parameters.Add($"height={height}");
        }

        if (!string.IsNullOrEmpty(name))
        {
            parameters.Add($"name={Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}");
        }

        if (!preserveAspectRatio)
        {
            parameters.Add("preserveAspectRatio=0");
        }

        return $"\u001b]1337;File={string.Join(';', parameters)}:{base64Data}\u0007";
    }

    public static int CalculateImageRows(
        TuiImageDimensions imageDimensions,
        int targetWidthCells,
        TuiCellDimensions? cellDimensions = null)
    {
        var cells = cellDimensions ?? new TuiCellDimensions(9, 18);
        var widthPx = Math.Max(1, imageDimensions.WidthPx);
        var heightPx = Math.Max(1, imageDimensions.HeightPx);
        var targetWidthPx = Math.Max(1, targetWidthCells) * Math.Max(1, cells.WidthPx);
        var scale = (double)targetWidthPx / widthPx;
        var scaledHeightPx = heightPx * scale;
        return Math.Max(1, (int)Math.Ceiling(scaledHeightPx / Math.Max(1, cells.HeightPx)));
    }

    public static TuiImageDimensions? GetPngDimensions(string base64Data)
    {
        var buffer = TryDecode(base64Data);
        if (buffer is null || buffer.Length < 24)
        {
            return null;
        }

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47];
        if (!buffer.AsSpan(0, 4).SequenceEqual(signature))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(20, 4));
        return new TuiImageDimensions(checked((int)width), checked((int)height));
    }

    public static TuiImageDimensions? GetJpegDimensions(string base64Data)
    {
        var buffer = TryDecode(base64Data);
        if (buffer is null || buffer.Length < 2 || buffer[0] != 0xff || buffer[1] != 0xd8)
        {
            return null;
        }

        var offset = 2;
        while (offset < buffer.Length - 9)
        {
            if (buffer[offset] != 0xff)
            {
                offset++;
                continue;
            }

            var marker = buffer[offset + 1];
            if (marker is >= 0xc0 and <= 0xc2)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 7, 2));
                return new TuiImageDimensions(width, height);
            }

            if (offset + 3 >= buffer.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
            if (length < 2)
            {
                return null;
            }

            offset += 2 + length;
        }

        return null;
    }

    public static TuiImageDimensions? GetGifDimensions(string base64Data)
    {
        var buffer = TryDecode(base64Data);
        if (buffer is null || buffer.Length < 10)
        {
            return null;
        }

        var signature = Encoding.ASCII.GetString(buffer, 0, 6);
        if (signature is not ("GIF87a" or "GIF89a"))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8, 2));
        return new TuiImageDimensions(width, height);
    }

    public static TuiImageDimensions? GetWebpDimensions(string base64Data)
    {
        var buffer = TryDecode(base64Data);
        if (buffer is null || buffer.Length < 30)
        {
            return null;
        }

        var riff = Encoding.ASCII.GetString(buffer, 0, 4);
        var webp = Encoding.ASCII.GetString(buffer, 8, 4);
        if (riff != "RIFF" || webp != "WEBP")
        {
            return null;
        }

        var chunk = Encoding.ASCII.GetString(buffer, 12, 4);
        if (chunk == "VP8 ")
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(26, 2)) & 0x3fff;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28, 2)) & 0x3fff;
            return new TuiImageDimensions(width, height);
        }

        if (chunk == "VP8L")
        {
            if (buffer.Length < 25)
            {
                return null;
            }

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(21, 4));
            var width = (int)(bits & 0x3fff) + 1;
            var height = (int)((bits >> 14) & 0x3fff) + 1;
            return new TuiImageDimensions(width, height);
        }

        if (chunk == "VP8X")
        {
            var width = buffer[24] | (buffer[25] << 8) | (buffer[26] << 16);
            var height = buffer[27] | (buffer[28] << 8) | (buffer[29] << 16);
            return new TuiImageDimensions(width + 1, height + 1);
        }

        return null;
    }

    public static TuiImageDimensions? GetImageDimensions(string base64Data, string mimeType) =>
        mimeType switch
        {
            "image/png" => GetPngDimensions(base64Data),
            "image/jpeg" => GetJpegDimensions(base64Data),
            "image/gif" => GetGifDimensions(base64Data),
            "image/webp" => GetWebpDimensions(base64Data),
            _ => null,
        };

    public static TuiImageRenderResult? RenderImage(
        string base64Data,
        TuiImageDimensions imageDimensions,
        TuiImageRenderOptions? options = null)
    {
        options ??= new TuiImageRenderOptions();
        var capabilities = GetCapabilities();
        if (capabilities.Images == TuiImageProtocol.None)
        {
            return null;
        }

        var maxWidth = Math.Max(1, options.MaxWidthCells ?? 80);
        var rows = CalculateImageRows(imageDimensions, maxWidth, _cellDimensions);
        if (capabilities.Images == TuiImageProtocol.Kitty)
        {
            var sequence = EncodeKitty(base64Data, maxWidth, rows, options.ImageId);
            return new TuiImageRenderResult(sequence, rows, options.ImageId);
        }

        if (capabilities.Images == TuiImageProtocol.ITerm2)
        {
            var sequence = EncodeITerm2(
                base64Data,
                width: maxWidth.ToString(CultureInfo.InvariantCulture),
                height: "auto",
                preserveAspectRatio: options.PreserveAspectRatio);
            return new TuiImageRenderResult(sequence, rows);
        }

        return null;
    }

    public static string Hyperlink(string text, string url) =>
        $"\u001b]8;;{url}\u001b\\{text}\u001b]8;;\u001b\\";

    public static string ImageFallback(string mimeType, TuiImageDimensions? dimensions = null, string? filename = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(filename))
        {
            parts.Add(filename);
        }

        parts.Add($"[{mimeType}]");
        if (dimensions is { } dims)
        {
            parts.Add($"{dims.WidthPx.ToString(CultureInfo.InvariantCulture)}x{dims.HeightPx.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"[Image: {string.Join(' ', parts)}]";
    }

    private static byte[]? TryDecode(string base64Data)
    {
        try
        {
            return Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            values[(string)entry.Key] = entry.Value?.ToString();
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string?> NormalizeEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in environment)
        {
            values[entry.Key] = entry.Value;
        }

        return values;
    }

    private static string Get(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;

    private static bool Has(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value);
}
