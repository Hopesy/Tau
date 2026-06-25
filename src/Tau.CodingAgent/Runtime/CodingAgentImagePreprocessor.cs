using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Runtime;

internal sealed record CodingAgentImagePreprocessResult(
    string Data,
    string MimeType,
    int? OriginalWidth,
    int? OriginalHeight,
    int? Width,
    int? Height,
    bool WasResized,
    long OriginalBytes,
    long EstimatedBase64Bytes);

internal static class CodingAgentImagePreprocessor
{
    public const int DefaultMaxWidth = 2000;
    public const int DefaultMaxHeight = 2000;
    public const int DefaultMaxBase64Bytes = 4_718_592;

    private const int MaxDecodedPixels = 50_000_000;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static CodingAgentImagePreprocessResult? Process(
        byte[] bytes,
        string mimeType,
        bool autoResizeImages,
        int maxWidth = DefaultMaxWidth,
        int maxHeight = DefaultMaxHeight,
        long maxBase64Bytes = DefaultMaxBase64Bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        var encodedLength = base64.Length;
        var dimensions = GetOrientedDimensions(bytes, base64, mimeType);

        if (!autoResizeImages)
        {
            return CreateResult(
                base64,
                mimeType,
                dimensions,
                dimensions,
                wasResized: false,
                originalBytes: bytes.Length,
                encodedLength);
        }

        if (encodedLength < maxBase64Bytes && IsWithinDimensions(dimensions, maxWidth, maxHeight))
        {
            return CreateResult(
                base64,
                mimeType,
                dimensions,
                dimensions,
                wasResized: false,
                originalBytes: bytes.Length,
                encodedLength);
        }

        if (!mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
            !TryDecodePngSafely(bytes, out var png))
        {
            return null;
        }

        var original = new TuiImageDimensions(png.Width, png.Height);
        var target = CalculateTargetDimensions(original, maxWidth, maxHeight);
        var currentWidth = target.WidthPx;
        var currentHeight = target.HeightPx;

        while (true)
        {
            var resizedPixels = ResizeBilinear(png.Rgba, png.Width, png.Height, currentWidth, currentHeight);
            var encodedBytes = EncodePngOptimized(resizedPixels, currentWidth, currentHeight);
            var resizedBase64 = Convert.ToBase64String(encodedBytes);
            if (resizedBase64.Length < maxBase64Bytes)
            {
                return CreateResult(
                    resizedBase64,
                    "image/png",
                    original,
                    new TuiImageDimensions(currentWidth, currentHeight),
                    wasResized: true,
                    originalBytes: bytes.Length,
                    resizedBase64.Length);
            }

            if (currentWidth == 1 && currentHeight == 1)
            {
                return null;
            }

            var nextWidth = currentWidth == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentWidth * 0.75));
            var nextHeight = currentHeight == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentHeight * 0.75));
            if (nextWidth == currentWidth && nextHeight == currentHeight)
            {
                return null;
            }

            currentWidth = nextWidth;
            currentHeight = nextHeight;
        }
    }

    public static string? FormatDimensionNote(CodingAgentImagePreprocessResult result)
    {
        if (!result.WasResized ||
            result.OriginalWidth is null ||
            result.OriginalHeight is null ||
            result.Width is null ||
            result.Height is null ||
            result.Width <= 0)
        {
            return null;
        }

        var scale = (double)result.OriginalWidth.Value / result.Width.Value;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[Image: original {result.OriginalWidth}x{result.OriginalHeight}, displayed at {result.Width}x{result.Height}. Multiply coordinates by {scale:F2} to map to original image.]");
    }

    private static CodingAgentImagePreprocessResult CreateResult(
        string data,
        string mimeType,
        TuiImageDimensions? original,
        TuiImageDimensions? current,
        bool wasResized,
        long originalBytes,
        long encodedLength) =>
        new(
            data,
            mimeType,
            original?.WidthPx,
            original?.HeightPx,
            current?.WidthPx,
            current?.HeightPx,
            wasResized,
            originalBytes,
            encodedLength);

    private static bool IsWithinDimensions(TuiImageDimensions? dimensions, int maxWidth, int maxHeight) =>
        dimensions is null || (dimensions.Value.WidthPx <= maxWidth && dimensions.Value.HeightPx <= maxHeight);

    private static TuiImageDimensions CalculateTargetDimensions(
        TuiImageDimensions dimensions,
        int maxWidth,
        int maxHeight)
    {
        var width = dimensions.WidthPx;
        var height = dimensions.HeightPx;

        if (width > maxWidth)
        {
            height = Math.Max(1, (int)Math.Round((double)height * maxWidth / width));
            width = maxWidth;
        }

        if (height > maxHeight)
        {
            width = Math.Max(1, (int)Math.Round((double)width * maxHeight / height));
            height = maxHeight;
        }

        return new TuiImageDimensions(width, height);
    }

    private static TuiImageDimensions? GetOrientedDimensions(byte[] bytes, string base64, string mimeType)
    {
        var dimensions = TuiTerminalImage.GetImageDimensions(base64, mimeType);
        if (dimensions is null)
        {
            return null;
        }

        var orientation = GetExifOrientation(bytes, mimeType);
        return orientation is >= 5 and <= 8
            ? new TuiImageDimensions(dimensions.Value.HeightPx, dimensions.Value.WidthPx)
            : dimensions;
    }

    private static int GetExifOrientation(byte[] bytes, string mimeType)
    {
        var tiffOffset = mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? FindJpegTiffOffset(bytes)
            : mimeType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                ? FindWebpTiffOffset(bytes)
                : -1;

        return tiffOffset < 0 ? 1 : ReadOrientationFromTiff(bytes, tiffOffset);
    }

    private static int FindJpegTiffOffset(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
        {
            return -1;
        }

        var offset = 2;
        while (offset < bytes.Length - 1)
        {
            if (bytes[offset] != 0xff)
            {
                return -1;
            }

            var marker = bytes[offset + 1];
            if (marker == 0xff)
            {
                offset++;
                continue;
            }

            if (offset + 4 > bytes.Length)
            {
                return -1;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
            if (length < 2 || offset + 2 + length > bytes.Length)
            {
                return -1;
            }

            if (marker == 0xe1)
            {
                var segmentStart = offset + 4;
                if (segmentStart + 6 <= bytes.Length && HasExifHeader(bytes, segmentStart))
                {
                    return segmentStart + 6;
                }

                return -1;
            }

            offset += 2 + length;
        }

        return -1;
    }

    private static int FindWebpTiffOffset(byte[] bytes)
    {
        if (bytes.Length < 12 ||
            bytes[0] != 'R' ||
            bytes[1] != 'I' ||
            bytes[2] != 'F' ||
            bytes[3] != 'F' ||
            bytes[8] != 'W' ||
            bytes[9] != 'E' ||
            bytes[10] != 'B' ||
            bytes[11] != 'P')
        {
            return -1;
        }

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize > int.MaxValue)
            {
                return -1;
            }

            var dataStart = offset + 8;
            var dataEnd = dataStart + (int)chunkSize;
            if (dataEnd > bytes.Length)
            {
                return -1;
            }

            if (chunkId == "EXIF")
            {
                return chunkSize >= 6 && HasExifHeader(bytes, dataStart) ? dataStart + 6 : dataStart;
            }

            offset = dataEnd + ((int)chunkSize % 2);
        }

        return -1;
    }

    private static bool HasExifHeader(byte[] bytes, int offset) =>
        offset + 6 <= bytes.Length &&
        bytes[offset] == 0x45 &&
        bytes[offset + 1] == 0x78 &&
        bytes[offset + 2] == 0x69 &&
        bytes[offset + 3] == 0x66 &&
        bytes[offset + 4] == 0x00 &&
        bytes[offset + 5] == 0x00;

    private static int ReadOrientationFromTiff(byte[] bytes, int tiffStart)
    {
        if (tiffStart + 8 > bytes.Length)
        {
            return 1;
        }

        var byteOrder = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tiffStart, 2));
        var littleEndian = byteOrder == 0x4949;
        ushort Read16(int pos) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos, 2));
        uint Read32(int pos) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));

        var ifdOffset = Read32(tiffStart + 4);
        if (ifdOffset > int.MaxValue)
        {
            return 1;
        }

        var ifdStart = tiffStart + (int)ifdOffset;
        if (ifdStart + 2 > bytes.Length)
        {
            return 1;
        }

        var entryCount = Read16(ifdStart);
        for (var i = 0; i < entryCount; i++)
        {
            var entryPos = ifdStart + 2 + i * 12;
            if (entryPos + 12 > bytes.Length)
            {
                return 1;
            }

            if (Read16(entryPos) == 0x0112)
            {
                var value = Read16(entryPos + 8);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    private static bool TryDecodePng(byte[] bytes, out PngImage png)
    {
        png = default;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
        {
            return false;
        }

        var offset = 8;
        int width = 0;
        int height = 0;
        int colorType = -1;
        var idat = new MemoryStream();

        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue)
            {
                return false;
            }

            var chunkLength = (int)length;
            var typeOffset = offset + 4;
            var dataOffset = offset + 8;
            var nextOffset = dataOffset + chunkLength + 4;
            if (nextOffset > bytes.Length)
            {
                return false;
            }

            var chunkType = Encoding.ASCII.GetString(bytes, typeOffset, 4);
            switch (chunkType)
            {
                case "IHDR":
                    if (chunkLength != 13)
                    {
                        return false;
                    }

                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset, 4)));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset + 4, 4)));
                    var bitDepth = bytes[dataOffset + 8];
                    colorType = bytes[dataOffset + 9];
                    var compression = bytes[dataOffset + 10];
                    var filter = bytes[dataOffset + 11];
                    var interlace = bytes[dataOffset + 12];
                    if (width <= 0 ||
                        height <= 0 ||
                        (long)width * height > MaxDecodedPixels ||
                        bitDepth != 8 ||
                        compression != 0 ||
                        filter != 0 ||
                        interlace != 0 ||
                        colorType is not (0 or 2 or 6))
                    {
                        return false;
                    }

                    break;
                case "IDAT":
                    idat.Write(bytes, dataOffset, chunkLength);
                    break;
                case "IEND":
                    return TryInflatePng(width, height, colorType, idat.ToArray(), out png);
            }

            offset = nextOffset;
        }

        return false;
    }

    private static bool TryDecodePngSafely(byte[] bytes, out PngImage png)
    {
        try
        {
            return TryDecodePng(bytes, out png);
        }
        catch (OverflowException)
        {
            png = default;
            return false;
        }
        catch (InvalidDataException)
        {
            png = default;
            return false;
        }
    }

    private static bool TryInflatePng(
        int width,
        int height,
        int colorType,
        byte[] compressed,
        out PngImage png)
    {
        png = default;
        var bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            6 => 4,
            _ => 0
        };
        if (bytesPerPixel == 0)
        {
            return false;
        }

        var stride = checked(width * bytesPerPixel);
        var expected = checked(height * (stride + 1));
        byte[] raw;
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected);
            zlib.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (raw.Length < expected)
        {
            return false;
        }

        var rgba = new byte[checked(width * height * 4)];
        var previous = new byte[stride];
        var current = new byte[stride];
        var rawOffset = 0;

        try
        {
            for (var y = 0; y < height; y++)
            {
                var filter = raw[rawOffset++];
                raw.AsSpan(rawOffset, stride).CopyTo(current);
                rawOffset += stride;

                Unfilter(current, previous, bytesPerPixel, filter);
                CopyScanlineToRgba(current, rgba, y * width * 4, colorType);
                current.CopyTo(previous, 0);
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        png = new PngImage(width, height, rgba);
        return true;
    }

    private static void Unfilter(byte[] current, byte[] previous, int bytesPerPixel, int filter)
    {
        for (var i = 0; i < current.Length; i++)
        {
            var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
            var up = previous[i];
            var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => -1
            };
            if (predictor < 0)
            {
                throw new InvalidDataException("Unsupported PNG filter.");
            }

            current[i] = unchecked((byte)(current[i] + predictor));
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);
        if (pa <= pb && pa <= pc)
        {
            return left;
        }

        return pb <= pc ? up : upLeft;
    }

    private static void CopyScanlineToRgba(byte[] scanline, byte[] rgba, int destinationOffset, int colorType)
    {
        var source = 0;
        var destination = destinationOffset;
        while (source < scanline.Length)
        {
            switch (colorType)
            {
                case 0:
                    rgba[destination++] = scanline[source];
                    rgba[destination++] = scanline[source];
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = 255;
                    break;
                case 2:
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = 255;
                    break;
                case 6:
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = scanline[source++];
                    rgba[destination++] = scanline[source++];
                    break;
            }
        }
    }

    private static byte[] ResizeBilinear(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var destination = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = height == 1 ? 0 : (double)y * (sourceHeight - 1) / (height - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(sourceHeight - 1, y0 + 1);
            var yWeight = sourceY - y0;

            for (var x = 0; x < width; x++)
            {
                var sourceX = width == 1 ? 0 : (double)x * (sourceWidth - 1) / (width - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(sourceWidth - 1, x0 + 1);
                var xWeight = sourceX - x0;

                var topLeft = (y0 * sourceWidth + x0) * 4;
                var topRight = (y0 * sourceWidth + x1) * 4;
                var bottomLeft = (y1 * sourceWidth + x0) * 4;
                var bottomRight = (y1 * sourceWidth + x1) * 4;
                var destinationIndex = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    destination[destinationIndex + channel] = InterpolateChannel(
                        source[topLeft + channel],
                        source[topRight + channel],
                        source[bottomLeft + channel],
                        source[bottomRight + channel],
                        xWeight,
                        yWeight);
                }
            }
        }

        return destination;
    }

    private static byte InterpolateChannel(
        byte topLeft,
        byte topRight,
        byte bottomLeft,
        byte bottomRight,
        double xWeight,
        double yWeight)
    {
        var top = topLeft + (topRight - topLeft) * xWeight;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
        var value = top + (bottom - top) * yWeight;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte[] EncodePngOptimized(byte[] rgba, int width, int height)
    {
        var colorType = SelectPngColorType(rgba);
        var bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            6 => 4,
            _ => throw new InvalidOperationException("Unsupported PNG color type.")
        };
        var stride = checked(width * bytesPerPixel);
        var raw = new byte[checked(height * (stride + 1))];
        var sourceOffset = 0;
        var destinationOffset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[destinationOffset++] = 0;
            for (var x = 0; x < width; x++)
            {
                switch (colorType)
                {
                    case 0:
                        raw[destinationOffset++] = rgba[sourceOffset];
                        sourceOffset += 4;
                        break;
                    case 2:
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        sourceOffset++;
                        break;
                    case 6:
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        raw[destinationOffset++] = rgba[sourceOffset++];
                        break;
                }
            }
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = output.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        WriteChunk(png, "IHDR", CreateIhdr(width, height, colorType));
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int SelectPngColorType(byte[] rgba)
    {
        var opaque = true;
        var grayscale = true;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] != 255)
            {
                opaque = false;
            }

            if (rgba[i] != rgba[i + 1] || rgba[i] != rgba[i + 2])
            {
                grayscale = false;
            }

            if (!opaque && !grayscale)
            {
                break;
            }
        }

        if (!opaque)
        {
            return 6;
        }

        return grayscale ? 0 : 2;
    }

    private static byte[] CreateIhdr(int width, int height, int colorType)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = checked((byte)colorType);
        return ihdr;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc(typeBytes, data));
        stream.Write(crc);
    }

    private static uint ComputeCrc(byte[] typeBytes, byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in typeBytes)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc ^ 0xffffffffu;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private readonly record struct PngImage(int Width, int Height, byte[] Rgba);
}
