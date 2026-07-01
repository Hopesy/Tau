using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentImageMimeDetectorTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    public void DetectSupportedImageMimeType_WithJpegMagic_ReturnsJpeg()
    {
        byte[] buffer = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10];

        Assert.Equal("image/jpeg", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithJpegProgressiveMarker0xF7_ReturnsNull()
    {
        // 上游会拒绝 FFD8FF SOI 序列后的 0xF7 标记
        byte[] buffer = [0xff, 0xd8, 0xff, 0xf7, 0x00, 0x10];

        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithValidPng_ReturnsPng()
    {
        var buffer = BuildPng(animated: false);

        Assert.Equal("image/png", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithSignatureButInvalidIhdr_ReturnsNull()
    {
        // PNG 签名有效，但首个 chunk 不是长度为 13 的 IHDR
        var buffer = new byte[16];
        PngSignature.CopyTo(buffer.AsSpan());
        WriteUint32BE(buffer, 8, 13);
        // chunk 类型字节 12..15 保持为 0，确保它不是 IHDR

        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithAnimatedPng_ReturnsNull()
    {
        // acTL 出现在 IDAT 前表示动态 PNG，上游会拒绝
        var buffer = BuildPng(animated: true);

        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithGif87a_ReturnsGif()
    {
        byte[] buffer = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a'];

        Assert.Equal("image/gif", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithGif89a_ReturnsGif()
    {
        byte[] buffer = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a'];

        Assert.Equal("image/gif", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithWebp_ReturnsWebp()
    {
        byte[] buffer =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x00, 0x00, 0x00, 0x00,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P'
        ];

        Assert.Equal("image/webp", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithRiffButNotWebp_ReturnsNull()
    {
        byte[] buffer =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x00, 0x00, 0x00, 0x00,
            (byte)'A', (byte)'V', (byte)'I', (byte)' '
        ];

        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithUnknownBytes_ReturnsNull()
    {
        byte[] buffer = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(buffer));
    }

    [Fact]
    public void DetectSupportedImageMimeType_WithEmptyBuffer_ReturnsNull()
    {
        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task DetectSupportedImageMimeTypeFromFileAsync_ReadsLeadingBytesAndDetects()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-mime-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, BuildPng(animated: false));
        try
        {
            var mime = await CodingAgentImageMimeDetector.DetectSupportedImageMimeTypeFromFileAsync(path);
            Assert.Equal("image/png", mime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DetectSupportedImageMimeTypeFromFileAsync_WithAnimatedPng_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-mime-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, BuildPng(animated: true));
        try
        {
            var mime = await CodingAgentImageMimeDetector.DetectSupportedImageMimeTypeFromFileAsync(path);
            Assert.Null(mime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 构造最小 PNG 字节流：签名、IHDR，然后写入静态 PNG 的 IDAT 或动态 PNG 的 acTL + IDAT
    // CRC 字节会写入但取值任意，因为嗅探器不校验 CRC
    private static byte[] BuildPng(bool animated)
    {
        using var ms = new MemoryStream();
        ms.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(ms, "IHDR", new byte[13]);
        if (animated)
        {
            WriteChunk(ms, "acTL", new byte[8]);
        }

        WriteChunk(ms, "IDAT", new byte[4]);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteUint32BE(length, 0, data.Length);
        stream.Write(length);
        foreach (var c in type)
        {
            stream.WriteByte((byte)c);
        }

        stream.Write(data, 0, data.Length);
        // 4-byte CRC placeholder (not validated by the detector).
        stream.Write(new byte[4], 0, 4);
    }

    private static void WriteUint32BE(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xff);
        buffer[offset + 1] = (byte)((value >> 16) & 0xff);
        buffer[offset + 2] = (byte)((value >> 8) & 0xff);
        buffer[offset + 3] = (byte)(value & 0xff);
    }
}
