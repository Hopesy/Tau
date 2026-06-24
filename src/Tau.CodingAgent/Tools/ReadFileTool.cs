using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tools;

public sealed class ReadFileTool : IAgentTool
{
    internal const int DefaultMaxImageBase64Bytes = CodingAgentImagePreprocessor.DefaultMaxBase64Bytes;
    private readonly bool _autoResizeImages;

    public ReadFileTool(bool autoResizeImages = true)
    {
        _autoResizeImages = autoResizeImages;
    }

    public string Name => "read_file";
    public string Label => "Read File";
    public string Description => "Read the contents of a file at the given path. Supports text files and images (jpg, png, gif, webp). Text output is truncated to 2000 lines or 50KB; use offset/limit for large text files.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Absolute or relative file path to read" },
                "file_path": { "type": "string", "description": "Alias for path, accepted for upstream renderer/tool compatibility" },
                "offset": { "type": "integer", "description": "Line number to start reading from (1-indexed)" },
                "limit": { "type": "integer", "description": "Maximum number of lines to read" }
            },
            "anyOf": [
                { "required": ["path"] },
                { "required": ["file_path"] }
            ]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = GetPathArgument(args);
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 1;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : (int?)null;

        if (!File.Exists(path))
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);

        var imageMimeType = await DetectImageMimeTypeAsync(path, ct).ConfigureAwait(false);
        if (imageMimeType is not null)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var processed = CodingAgentImagePreprocessor.Process(bytes, imageMimeType, _autoResizeImages);
            var originalEncodedSize = EstimateBase64ByteCount(bytes.Length);
            if (processed is null)
            {
                return new ToolResult(
                    [
                        new TextContent(
                            $"Read image file [{imageMimeType}]\n[Image omitted: could not be resized below the inline image size limit.]")
                    ],
                    Details: ReadFileToolDetails.ForImage(
                        path,
                        imageMimeType,
                        bytes.Length,
                        originalEncodedSize,
                        imageOmitted: true));
            }

            var imageText = $"Read image file [{processed.MimeType}]";
            var dimensionNote = CodingAgentImagePreprocessor.FormatDimensionNote(processed);
            if (dimensionNote is not null)
            {
                imageText += $"\n{dimensionNote}";
            }

            return new ToolResult(
                [
                    new TextContent(imageText),
                    new ImageContent(processed.Data, processed.MimeType)
                ],
                Details: ReadFileToolDetails.ForImage(
                    path,
                    processed.MimeType,
                    processed.OriginalBytes,
                    processed.EstimatedBase64Bytes,
                    imageOmitted: false,
                    imageResized: processed.WasResized,
                    originalWidth: processed.OriginalWidth,
                    originalHeight: processed.OriginalHeight,
                    width: processed.Width,
                    height: processed.Height));
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var lines = NormalizeLineEndings(text).Split('\n');
        var startLine = Math.Max(1, offset);
        var startIndex = startLine - 1;
        if (startIndex >= lines.Length)
        {
            return new ToolResult(
                [new TextContent($"Offset {startLine} is beyond end of file ({lines.Length} lines total)")],
                IsError: true);
        }

        var remainingLines = lines.Length - startIndex;
        var requestedLineCount = limit.HasValue
            ? Math.Min(Math.Max(0, limit.Value), remainingLines)
            : remainingLines;
        var selectedContent = string.Join("\n", lines.Skip(startIndex).Take(requestedLineCount));
        var truncation = ToolOutputTruncator.TruncateHead(selectedContent);
        string content;
        ToolOutputTruncationResult? detailTruncation = null;

        if (truncation.FirstLineExceedsLimit)
        {
            var firstLineSize = ToolOutputTruncator.FormatSize(System.Text.Encoding.UTF8.GetByteCount(lines[startIndex]));
            content = $"[Line {startLine} is {firstLineSize}, exceeds {ToolOutputTruncator.FormatSize(ToolOutputTruncator.DefaultMaxBytes)} limit. Use bash: sed -n '{startLine}p' {path} | head -c {ToolOutputTruncator.DefaultMaxBytes}]";
            detailTruncation = truncation;
        }
        else if (truncation.Truncated)
        {
            var endLine = startLine + truncation.OutputLines - 1;
            var nextOffset = endLine + 1;
            content = truncation.Content;
            content += truncation.TruncatedBy == "lines"
                ? $"\n\n[Showing lines {startLine}-{endLine} of {lines.Length}. Use offset={nextOffset} to continue.]"
                : $"\n\n[Showing lines {startLine}-{endLine} of {lines.Length} ({ToolOutputTruncator.FormatSize(ToolOutputTruncator.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]";
            detailTruncation = truncation;
        }
        else if (limit.HasValue && startIndex + requestedLineCount < lines.Length)
        {
            var remaining = lines.Length - (startIndex + requestedLineCount);
            var nextOffset = startLine + requestedLineCount;
            content = $"{truncation.Content}{(truncation.Content.Length == 0 ? string.Empty : "\n\n")}[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
        }
        else
        {
            content = truncation.Content;
        }

        var outputLines = detailTruncation?.OutputLines ?? requestedLineCount;
        var endLineForDetails = outputLines <= 0 ? startLine : startLine + outputLines - 1;
        var hasMore = detailTruncation?.Truncated == true || startIndex + requestedLineCount < lines.Length;
        var details = ReadFileToolDetails.ForText(
            path,
            GuessLanguageFromPath(path),
            startLine,
            Math.Min(endLineForDetails, lines.Length),
            lines.Length,
            hasMore,
            detailTruncation);

        return new ToolResult([new TextContent(content)], Details: details);
    }

    private static string GetPathArgument(JsonElement args)
    {
        if (args.TryGetProperty("path", out var pathProperty) &&
            pathProperty.ValueKind == JsonValueKind.String)
        {
            return pathProperty.GetString()!;
        }

        if (args.TryGetProperty("file_path", out var filePathProperty) &&
            filePathProperty.ValueKind == JsonValueKind.String)
        {
            return filePathProperty.GetString()!;
        }

        throw new InvalidOperationException("Missing required path argument.");
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static long EstimateBase64ByteCount(long byteCount) =>
        ((byteCount + 2) / 3) * 4;

    private static async Task<string?> DetectImageMimeTypeAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[12];
        var bytesRead = await stream.ReadAsync(header, ct).ConfigureAwait(false);
        return DetectImageMimeType(header.AsSpan(0, bytesRead));
    }

    private static string? DetectImageMimeType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 &&
            header[0] == 0x89 &&
            header[1] == 0x50 &&
            header[2] == 0x4E &&
            header[3] == 0x47 &&
            header[4] == 0x0D &&
            header[5] == 0x0A &&
            header[6] == 0x1A &&
            header[7] == 0x0A)
        {
            return "image/png";
        }

        if (header.Length >= 2 &&
            header[0] == 0xFF &&
            header[1] == 0xD8)
        {
            return "image/jpeg";
        }

        if (header.Length >= 6 &&
            header[0] == 0x47 &&
            header[1] == 0x49 &&
            header[2] == 0x46 &&
            header[3] == 0x38 &&
            (header[4] == 0x37 || header[4] == 0x39) &&
            header[5] == 0x61)
        {
            return "image/gif";
        }

        if (header.Length >= 12 &&
            header[0] == 0x52 &&
            header[1] == 0x49 &&
            header[2] == 0x46 &&
            header[3] == 0x46 &&
            header[8] == 0x57 &&
            header[9] == 0x45 &&
            header[10] == 0x42 &&
            header[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static string? GuessLanguageFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" => "javascript",
            ".py" => "python",
            ".csproj" or ".props" or ".targets" or ".xml" or ".xaml" or ".html" or ".htm" => "xml",
            ".json" or ".jsonl" => "json",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            ".sh" or ".bash" or ".zsh" => "shell",
            ".diff" or ".patch" => "diff",
            ".md" or ".markdown" => "markdown",
            _ => null
        };
}

public sealed record ReadFileToolDetails(
    string Path,
    string Kind,
    ToolOutputTruncationResult? Truncation = null,
    string? Language = null,
    int? StartLine = null,
    int? EndLine = null,
    int? TotalLines = null,
    bool HasMore = false,
    string? MimeType = null,
    long? ImageBytes = null,
    long? EstimatedBase64Bytes = null,
    bool ImageOmitted = false,
    bool ImageResized = false,
    int? OriginalWidth = null,
    int? OriginalHeight = null,
    int? Width = null,
    int? Height = null)
{
    public static ReadFileToolDetails ForText(
        string path,
        string? language,
        int startLine,
        int endLine,
        int totalLines,
        bool hasMore,
        ToolOutputTruncationResult? truncation) =>
        new(
            path,
            "text",
            truncation,
            Language: language,
            StartLine: startLine,
            EndLine: endLine,
            TotalLines: totalLines,
            HasMore: hasMore);

    public static ReadFileToolDetails ForImage(
        string path,
        string mimeType,
        long imageBytes,
        long estimatedBase64Bytes,
        bool imageOmitted,
        bool imageResized = false,
        int? originalWidth = null,
        int? originalHeight = null,
        int? width = null,
        int? height = null) =>
        new(
            path,
            "image",
            MimeType: mimeType,
            ImageBytes: imageBytes,
            EstimatedBase64Bytes: estimatedBase64Bytes,
            ImageOmitted: imageOmitted,
            ImageResized: imageResized,
            OriginalWidth: originalWidth,
            OriginalHeight: originalHeight,
            Width: width,
            Height: height);
}
