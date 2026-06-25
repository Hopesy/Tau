using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Tau.CodingAgent.Tests;

internal static class ImageTestData
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] CreatePng(
        int width,
        int height,
        byte red = 0x20,
        byte green = 0x80,
        byte blue = 0xc0,
        byte alpha = 0xff,
        bool noisy = false)
    {
        var stride = checked(width * 4);
        var raw = new byte[checked(height * (stride + 1))];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                if (noisy)
                {
                    var noise = Noise(x, y);
                    raw[offset++] = (byte)noise;
                    raw[offset++] = (byte)(noise >> 8);
                    raw[offset++] = (byte)(noise >> 16);
                }
                else
                {
                    raw[offset++] = red;
                    raw[offset++] = green;
                    raw[offset++] = blue;
                }

                raw[offset++] = alpha;
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
        WriteChunk(png, "IHDR", CreateIhdr(width, height));
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] CreateJpeg(int width, int height, bool noisy = false)
    {
        using var image = CreateImage(width, height, noisy);
        using var jpeg = new MemoryStream();
        image.SaveAsJpeg(jpeg, new JpegEncoder { Quality = 90 });
        return jpeg.ToArray();
    }

    public static byte[] CreateWebp(int width, int height, bool noisy = false)
    {
        using var image = CreateImage(width, height, noisy);
        using var webp = new MemoryStream();
        image.SaveAsWebp(webp, new WebpEncoder { Quality = 90 });
        return webp.ToArray();
    }

    public static byte[] CreateJpegWithExifOrientation(int width, int height, ushort orientation)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xff, 0xd8]);
        WriteJpegSegment(jpeg, 0xe1, CreateExifOrientationData(orientation));
        WriteJpegSegment(jpeg, 0xc0, CreateSof0Data(width, height));
        jpeg.Write([0xff, 0xd9]);
        return jpeg.ToArray();
    }

    private static byte[] CreateIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        return ihdr;
    }

    private static Image<Rgba32> CreateImage(int width, int height, bool noisy)
    {
        var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (noisy)
                {
                    var noise = Noise(x, y);
                    image[x, y] = new Rgba32((byte)noise, (byte)(noise >> 8), (byte)(noise >> 16));
                }
                else
                {
                    image[x, y] = new Rgba32(0x80, 0x40, 0x7f);
                }
            }
        }

        return image;
    }

    private static uint Noise(int x, int y)
    {
        var value = unchecked((uint)x * 747796405u + (uint)y * 2891336453u + 0x9e3779b9u);
        value = unchecked((value ^ (value >> 16)) * 2246822519u);
        value = unchecked((value ^ (value >> 13)) * 3266489917u);
        return value ^ (value >> 16);
    }

    private static byte[] CreateExifOrientationData(ushort orientation)
    {
        var data = new byte[32];
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(data, 0);
        data[6] = 0x49;
        data[7] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16, 2), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24, 2), orientation);
        return data;
    }

    private static byte[] CreateSof0Data(int width, int height)
    {
        var data = new byte[15];
        data[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1, 2), checked((ushort)height));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3, 2), checked((ushort)width));
        data[5] = 3;
        data[6] = 1;
        data[7] = 0x11;
        data[8] = 0;
        data[9] = 2;
        data[10] = 0x11;
        data[11] = 0;
        data[12] = 3;
        data[13] = 0x11;
        data[14] = 0;
        return data;
    }

    private static void WriteJpegSegment(Stream stream, byte marker, byte[] data)
    {
        stream.WriteByte(0xff);
        stream.WriteByte(marker);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(data.Length + 2)));
        stream.Write(length);
        stream.Write(data);
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
}
