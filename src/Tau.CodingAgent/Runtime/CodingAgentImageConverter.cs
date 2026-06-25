using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Tau.CodingAgent.Runtime;

internal sealed record CodingAgentConvertedImage(string Data, string MimeType);

internal static class CodingAgentImageConverter
{
    public static CodingAgentConvertedImage? ConvertToPng(string base64Data, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(base64Data);
        ArgumentNullException.ThrowIfNull(mimeType);

        if (mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            return new CodingAgentConvertedImage(base64Data, "image/png");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var image = Image.Load(bytes);
            image.Mutate(static context => context.AutoOrient());
            using var output = new MemoryStream();
            image.SaveAsPng(output, new PngEncoder());
            return new CodingAgentConvertedImage(Convert.ToBase64String(output.ToArray()), "image/png");
        }
        catch (Exception ex) when (
            ex is UnknownImageFormatException or
                InvalidImageContentException or
                NotSupportedException or
                InvalidOperationException or
                ArgumentException)
        {
            return null;
        }
    }
}
