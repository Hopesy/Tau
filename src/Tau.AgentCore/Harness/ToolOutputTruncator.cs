using System.Text;

namespace Tau.AgentCore.Harness;

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

public static class ToolOutputTruncator
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;
    public const int GrepMaxLineLength = 500;

    public static ToolOutputTruncationResult TruncateHead(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        var totalBytes = Utf8ByteCount(content);
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

        var firstLineBytes = Utf8ByteCount(lines[0]);
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
            var lineBytes = Utf8ByteCount(line) + (i > 0 ? 1 : 0);
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
        var finalOutputBytes = Utf8ByteCount(output);

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

    public static ToolOutputTruncationResult TruncateTail(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        var totalBytes = Utf8ByteCount(content);
        var lines = content.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var totalLines = lines.Count;
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

        var outputLines = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";
        var lastLinePartial = false;

        for (var i = lines.Count - 1; i >= 0 && outputLines.Count < maxLines; i--)
        {
            var line = lines[i];
            var lineBytes = Utf8ByteCount(line) + (outputLines.Count > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (outputLines.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(line, maxBytes);
                    outputLines.Insert(0, truncatedLine);
                    outputBytes = Utf8ByteCount(truncatedLine);
                    lastLinePartial = true;
                }

                break;
            }

            outputLines.Insert(0, line);
            outputBytes += lineBytes;
        }

        if (outputLines.Count >= maxLines && outputBytes <= maxBytes)
            truncatedBy = "lines";

        var output = string.Join("\n", outputLines);
        var finalOutputBytes = Utf8ByteCount(output);

        return new ToolOutputTruncationResult(
            output,
            Truncated: true,
            truncatedBy,
            totalLines,
            totalBytes,
            outputLines.Count,
            finalOutputBytes,
            lastLinePartial,
            FirstLineExceedsLimit: false,
            maxLines,
            maxBytes);
    }

    public static (string Text, bool WasTruncated) TruncateLine(
        string line,
        int maxChars = GrepMaxLineLength)
    {
        if (line.Length <= maxChars)
            return (line, WasTruncated: false);

        return ($"{line[..maxChars]}... [truncated]", WasTruncated: true);
    }

    public static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    private static int Utf8ByteCount(string content) =>
        Encoding.UTF8.GetByteCount(content);

    private static string TruncateStringToBytesFromEnd(string value, int maxBytes)
    {
        if (maxBytes <= 0)
            return string.Empty;

        var outputBytes = 0;
        var start = value.Length;
        var needsReplacement = false;

        for (var i = value.Length; i > 0;)
        {
            var characterStart = i - 1;
            var code = value[characterStart];
            int characterBytes;
            var unpairedSurrogate = false;

            if (char.IsLowSurrogate(code) && characterStart > 0)
            {
                var previous = value[characterStart - 1];
                if (char.IsHighSurrogate(previous))
                {
                    characterStart--;
                    characterBytes = 4;
                }
                else
                {
                    characterBytes = 3;
                    unpairedSurrogate = true;
                }
            }
            else if (char.IsSurrogate(code))
            {
                characterBytes = 3;
                unpairedSurrogate = true;
            }
            else
            {
                characterBytes = code <= 0x7f ? 1 : code <= 0x7ff ? 2 : 3;
            }

            if (outputBytes + characterBytes > maxBytes)
                break;

            outputBytes += characterBytes;
            start = characterStart;
            needsReplacement |= unpairedSurrogate;
            i = characterStart;
        }

        var output = value[start..];
        return needsReplacement ? ReplaceUnpairedSurrogates(output) : output;
    }

    private static string ReplaceUnpairedSurrogates(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var code = value[i];
            if (char.IsHighSurrogate(code))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(code);
                    builder.Append(value[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(code))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(code);
            }
        }

        return builder.ToString();
    }
}
