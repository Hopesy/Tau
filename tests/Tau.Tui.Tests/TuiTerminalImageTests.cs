using System.Text;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiTerminalImageTests
{
    [Fact]
    public void TerminalImage_DetectsCapabilitiesFromEnvironment()
    {
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: true, Hyperlinks: false),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["TERM"] = "tmux-256color",
                ["COLORTERM"] = "truecolor",
            }));
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "WezTerm",
            }));
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["term_program"] = "WezTerm",
            }));
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.ITerm2, TrueColor: true, Hyperlinks: true),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["ITERM_SESSION_ID"] = "session",
            }));
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: true, Hyperlinks: true),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["TERM_PROGRAM"] = "vscode",
            }));
        Assert.Equal(
            new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: true, Hyperlinks: false),
            TuiTerminalImage.DetectCapabilities(new Dictionary<string, string?>
            {
                ["COLORTERM"] = "24bit",
            }));
    }

    [Fact]
    public void TerminalImage_EncodesKittySingleAndChunkedSequences()
    {
        var single = TuiTerminalImage.EncodeKitty("abc", columns: 4, rows: 2, imageId: 7);

        Assert.Equal("\u001b_Ga=T,f=100,q=2,c=4,r=2,i=7;abc\u001b\\", single);
        Assert.True(TuiTerminalImage.IsImageLine("prefix " + single));

        var chunked = TuiTerminalImage.EncodeKitty(new string('x', 4097));

        Assert.Contains(",m=1;", chunked, StringComparison.Ordinal);
        Assert.Contains("\u001b_Gm=0;", chunked, StringComparison.Ordinal);
        Assert.EndsWith("x\u001b\\", chunked, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalImage_EncodesITerm2AndKittyDeleteSequences()
    {
        var encoded = TuiTerminalImage.EncodeITerm2(
            "base64",
            width: "40",
            height: "auto",
            name: "file.png",
            preserveAspectRatio: false,
            inline: true);

        Assert.Equal("\u001b]1337;File=inline=1;width=40;height=auto;name=ZmlsZS5wbmc=;preserveAspectRatio=0:base64\u0007", encoded);
        Assert.True(TuiTerminalImage.IsImageLine(encoded));
        Assert.Equal("\u001b_Ga=d,d=I,i=42\u001b\\", TuiTerminalImage.DeleteKittyImage(42));
        Assert.Equal("\u001b_Ga=d,d=A\u001b\\", TuiTerminalImage.DeleteAllKittyImages());
    }

    [Fact]
    public void TerminalImage_ReadsImageDimensionsFromKnownFormats()
    {
        Assert.Equal(new TuiImageDimensions(3, 5), TuiTerminalImage.GetPngDimensions(PngBase64(3, 5)));
        Assert.Equal(new TuiImageDimensions(11, 7), TuiTerminalImage.GetJpegDimensions(JpegBase64(11, 7)));
        Assert.Equal(new TuiImageDimensions(13, 17), TuiTerminalImage.GetGifDimensions(GifBase64(13, 17)));
        Assert.Equal(new TuiImageDimensions(19, 23), TuiTerminalImage.GetWebpDimensions(WebpVp8xBase64(19, 23)));
        Assert.Null(TuiTerminalImage.GetImageDimensions("not-base64", "image/png"));
        Assert.Null(TuiTerminalImage.GetImageDimensions(PngBase64(1, 1), "image/bmp"));
    }

    [Fact]
    public void TerminalImage_RendersKittyOrITerm2ByCachedCapabilities()
    {
        TuiTerminalImage.SetCellDimensions(new TuiCellDimensions(10, 20));
        TuiTerminalImage.SetCapabilities(new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true));

        var kitty = TuiTerminalImage.RenderImage(
            "abc",
            new TuiImageDimensions(20, 20),
            new TuiImageRenderOptions { MaxWidthCells = 4, ImageId = 9 });

        Assert.NotNull(kitty);
        Assert.Equal(2, kitty.Rows);
        Assert.Equal(9, kitty.ImageId);
        Assert.Equal("\u001b_Ga=T,f=100,q=2,c=4,r=2,i=9;abc\u001b\\", kitty.Sequence);

        TuiTerminalImage.SetCapabilities(new TuiTerminalCapabilities(TuiImageProtocol.ITerm2, TrueColor: true, Hyperlinks: true));

        var iterm = TuiTerminalImage.RenderImage(
            "abc",
            new TuiImageDimensions(10, 40),
            new TuiImageRenderOptions { MaxWidthCells = 5, PreserveAspectRatio = true });

        Assert.NotNull(iterm);
        Assert.Equal(10, iterm.Rows);
        Assert.Equal("\u001b]1337;File=inline=1;width=5;height=auto:abc\u0007", iterm.Sequence);

        TuiTerminalImage.SetCapabilities(new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: false, Hyperlinks: false));
        Assert.Null(TuiTerminalImage.RenderImage("abc", new TuiImageDimensions(10, 10)));
    }

    [Fact]
    public void ImageComponent_RendersFallbackWhenTerminalHasNoImageProtocol()
    {
        TuiTerminalImage.SetCapabilities(new TuiTerminalCapabilities(TuiImageProtocol.None, TrueColor: false, Hyperlinks: false));
        var image = new TuiImage(
            PngBase64(3, 5),
            "image/png",
            new TuiImageTheme { FallbackColor = static value => $"dim:{value}" },
            new TuiImageOptions { Filename = "red.png" });

        var lines = image.Render(20);
        var cached = image.Render(20);

        Assert.Same(lines, cached);
        Assert.Equal(["dim:[Image: red.png [image/png] 3x5]"], lines);
        Assert.Equal(new TuiImageDimensions(3, 5), image.Dimensions);
    }

    [Fact]
    public void ImageComponent_RendersProtocolRowsAndCachesByWidth()
    {
        TuiTerminalImage.SetCellDimensions(new TuiCellDimensions(10, 10));
        TuiTerminalImage.SetCapabilities(new TuiTerminalCapabilities(TuiImageProtocol.Kitty, TrueColor: true, Hyperlinks: true));
        var image = new TuiImage(
            "abc",
            "image/unknown",
            options: new TuiImageOptions { MaxWidthCells = 4, ImageId = 12 },
            dimensions: new TuiImageDimensions(20, 40));

        var lines = image.Render(20);

        Assert.Equal(8, lines.Count);
        Assert.All(lines.Take(7), line => Assert.Equal(string.Empty, line));
        Assert.Equal(12, image.ImageId);
        Assert.StartsWith("\u001b[7A\u001b_Ga=T,f=100,q=2,c=4,r=8,i=12;abc", lines[^1], StringComparison.Ordinal);
        Assert.Same(lines, image.Render(20));
        Assert.NotSame(lines, image.Render(21));
    }

    [Fact]
    public void TerminalImage_HyperlinkAndFallbackMirrorUpstreamFormatting()
    {
        Assert.Equal(
            "\u001b]8;;https://example.com\u001b\\Docs\u001b]8;;\u001b\\",
            TuiTerminalImage.Hyperlink("Docs", "https://example.com"));
        Assert.Equal(
            "[Image: file.webp [image/webp] 10x12]",
            TuiTerminalImage.ImageFallback("image/webp", new TuiImageDimensions(10, 12), "file.webp"));
        Assert.Equal(
            "[Image: [image/png]]",
            TuiTerminalImage.ImageFallback("image/png"));
    }

    private static string PngBase64(int width, int height)
    {
        var bytes = new byte[24];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4e;
        bytes[3] = 0x47;
        WriteUInt32BigEndian(bytes, 16, width);
        WriteUInt32BigEndian(bytes, 20, height);
        return Convert.ToBase64String(bytes);
    }

    private static string JpegBase64(int width, int height)
    {
        var bytes = new byte[20];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[2] = 0xff;
        bytes[3] = 0xc0;
        bytes[4] = 0x00;
        bytes[5] = 0x0b;
        WriteUInt16BigEndian(bytes, 7, height);
        WriteUInt16BigEndian(bytes, 9, width);
        return Convert.ToBase64String(bytes);
    }

    private static string GifBase64(int width, int height)
    {
        var bytes = Encoding.ASCII.GetBytes("GIF89a\0\0\0\0");
        WriteUInt16LittleEndian(bytes, 6, width);
        WriteUInt16LittleEndian(bytes, 8, height);
        return Convert.ToBase64String(bytes);
    }

    private static string WebpVp8xBase64(int width, int height)
    {
        var bytes = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("VP8X").CopyTo(bytes, 12);
        var storedWidth = width - 1;
        var storedHeight = height - 1;
        bytes[24] = (byte)(storedWidth & 0xff);
        bytes[25] = (byte)((storedWidth >> 8) & 0xff);
        bytes[26] = (byte)((storedWidth >> 16) & 0xff);
        bytes[27] = (byte)(storedHeight & 0xff);
        bytes[28] = (byte)((storedHeight >> 8) & 0xff);
        bytes[29] = (byte)((storedHeight >> 16) & 0xff);
        return Convert.ToBase64String(bytes);
    }

    private static void WriteUInt16BigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)((value >> 8) & 0xff);
        bytes[offset + 1] = (byte)(value & 0xff);
    }

    private static void WriteUInt16LittleEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xff);
        bytes[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)((value >> 24) & 0xff);
        bytes[offset + 1] = (byte)((value >> 16) & 0xff);
        bytes[offset + 2] = (byte)((value >> 8) & 0xff);
        bytes[offset + 3] = (byte)(value & 0xff);
    }
}
