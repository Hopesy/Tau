using System.Text;

namespace Tau.Mom;

public sealed record MomToolTruncationResult(
    string Content,
    bool Truncated,
    string? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial = false,
    bool FirstLineExceedsLimit = false);

public static class MomToolOutputTruncator
{
    public const int DefaultMaxLines = 2_000;
    public const int DefaultMaxBytes = 50 * 1024;

    public static MomToolTruncationResult TruncateHead(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Split('\n');
        if (lines.Length <= maxLines && totalBytes <= maxBytes)
        {
            return new MomToolTruncationResult(content, false, null, lines.Length, totalBytes, lines.Length, totalBytes);
        }

        var firstLineBytes = Encoding.UTF8.GetByteCount(lines[0]);
        if (firstLineBytes > maxBytes)
        {
            return new MomToolTruncationResult(string.Empty, true, "bytes", lines.Length, totalBytes, 0, 0, FirstLineExceedsLimit: true);
        }

        var selected = new List<string>();
        var bytes = 0;
        var truncatedBy = "lines";
        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (i > 0 ? 1 : 0);
            if (bytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                break;
            }

            selected.Add(lines[i]);
            bytes += lineBytes;
        }

        var output = string.Join("\n", selected);
        var outputBytes = Encoding.UTF8.GetByteCount(output);
        return new MomToolTruncationResult(output, true, truncatedBy, lines.Length, totalBytes, selected.Count, outputBytes);
    }

    public static MomToolTruncationResult TruncateTail(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Split('\n');
        if (lines.Length <= maxLines && totalBytes <= maxBytes)
        {
            return new MomToolTruncationResult(content, false, null, lines.Length, totalBytes, lines.Length, totalBytes);
        }

        var selected = new LinkedList<string>();
        var bytes = 0;
        var truncatedBy = "lines";
        var lastLinePartial = false;
        for (var i = lines.Length - 1; i >= 0 && selected.Count < maxLines; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (selected.Count > 0 ? 1 : 0);
            if (bytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (selected.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(lines[i], maxBytes);
                    selected.AddFirst(truncatedLine);
                    bytes = Encoding.UTF8.GetByteCount(truncatedLine);
                    lastLinePartial = true;
                }

                break;
            }

            selected.AddFirst(lines[i]);
            bytes += lineBytes;
        }

        var output = string.Join("\n", selected);
        var outputBytes = Encoding.UTF8.GetByteCount(output);
        return new MomToolTruncationResult(output, true, truncatedBy, lines.Length, totalBytes, selected.Count, outputBytes, lastLinePartial);
    }

    public static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    private static string TruncateStringToBytesFromEnd(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80)
        {
            start++;
        }

        return Encoding.UTF8.GetString(bytes.AsSpan(start));
    }
}
