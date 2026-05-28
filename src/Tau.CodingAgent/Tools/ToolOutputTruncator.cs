using System.Text;

namespace Tau.CodingAgent.Tools;

public sealed record ToolOutputTruncationResult(
    string Content,
    bool Truncated,
    string? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial,
    bool FirstLineExceedsLimit,
    int MaxLines,
    int MaxBytes);

internal static class ToolOutputTruncator
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;

    public static ToolOutputTruncationResult TruncateHead(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Split('\n');
        var totalLines = lines.Length;

        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new ToolOutputTruncationResult(
                content,
                Truncated: false,
                TruncatedBy: null,
                totalLines,
                totalBytes,
                totalLines,
                totalBytes,
                LastLinePartial: false,
                FirstLineExceedsLimit: false,
                maxLines,
                maxBytes);
        }

        var firstLineBytes = Encoding.UTF8.GetByteCount(lines[0]);
        if (firstLineBytes > maxBytes)
        {
            return new ToolOutputTruncationResult(
                string.Empty,
                Truncated: true,
                TruncatedBy: "bytes",
                totalLines,
                totalBytes,
                OutputLines: 0,
                OutputBytes: 0,
                LastLinePartial: false,
                FirstLineExceedsLimit: true,
                maxLines,
                maxBytes);
        }

        var outputLines = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";

        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            var line = lines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (i > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                break;
            }

            outputLines.Add(line);
            outputBytes += lineBytes;
        }

        if (outputLines.Count >= maxLines && outputBytes <= maxBytes)
            truncatedBy = "lines";

        var output = string.Join("\n", outputLines);
        var finalOutputBytes = Encoding.UTF8.GetByteCount(output);

        return new ToolOutputTruncationResult(
            output,
            Truncated: true,
            truncatedBy,
            totalLines,
            totalBytes,
            outputLines.Count,
            finalOutputBytes,
            LastLinePartial: false,
            FirstLineExceedsLimit: false,
            maxLines,
            maxBytes);
    }

    public static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };
}
