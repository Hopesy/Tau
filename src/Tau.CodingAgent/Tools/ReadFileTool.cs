using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tools;

public sealed class ReadFileTool : IAgentTool
{
    internal const int DefaultMaxImageBase64Bytes = CodingAgentImagePreprocessor.DefaultMaxBase64Bytes;
    private readonly bool _autoResizeImages;
    private readonly string _workingDirectory;
    private readonly ModelImageResizeOptions? _resizeOptions;
    private Func<Model>? CurrentModel { get; init; }
    private Func<bool>? CurrentAutoResize { get; init; }

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的文件读取工具</summary>
    /// <param name="autoResizeImages">是否自动缩放图片</param>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    /// <param name="resizeOptions">没有会话模型限制时采用的缩放策略。</param>
    public ReadFileTool(bool autoResizeImages = true, string? workingDirectory = null, ModelImageResizeOptions? resizeOptions = null)
    {
        _autoResizeImages = autoResizeImages;
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        _resizeOptions = resizeOptions;
    }

    /// <summary>【CodingAgent】【读取模型上下文】为会话复制工具，读取实时模型及缩放设置，避免修改调用方共用的实例。</summary>
    /// <param name="model">有效模型读取器。</param><param name="autoResize">缩放设置读取器。</param><returns>本会话工具副本。</returns>
    internal ReadFileTool WithSessionContext(Func<Model> model, Func<bool> autoResize) =>
        new(_autoResizeImages, _workingDirectory, _resizeOptions) { CurrentModel = model, CurrentAutoResize = autoResize };

    public string Name => "read";
    /// <summary>工具在系统提示中的一行简介。</summary>
    public string PromptSnippet => "Read file contents";
    /// <summary>启用工具时加入系统提示的使用规则。</summary>
    public IReadOnlyList<string> PromptGuidelines => ["Use read to examine files instead of cat or sed."];
    public string Label => "Read File";
    public string Description => "Read the contents of a file at the given path. Supports text files and images (jpg, png, gif, webp, bmp). Text output is truncated to 2000 lines or 50KB; use offset/limit for large text files.";

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

    /// <summary>【CodingAgent】【工具执行】在所属会话目录中执行工具请求</summary>
    /// <param name="toolCallId">工具调用标识</param>
    /// <param name="args">工具参数</param>
    /// <param name="ct">取消信号</param>
    /// <param name="onUpdate">增量结果回调</param>
    /// <returns>工具执行结果</returns>
    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = CodingAgentToolPaths.Resolve(GetPathArgument(args), _workingDirectory);
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 1;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : (int?)null;

        if (!File.Exists(path))
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);

        var imageMimeType = await DetectImageMimeTypeAsync(path, ct).ConfigureAwait(false);
        if (imageMimeType is not null)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var model = CurrentModel?.Invoke();
            var processed = await CodingAgentImageProcessing.ProcessAsync(new(Convert.ToBase64String(bytes), imageMimeType),
                CurrentAutoResize?.Invoke() ?? _autoResizeImages, model?.InputLimits?.Images?.Resize ?? _resizeOptions, ct).ConfigureAwait(false);
            var visionNote = model is not null && !model.InputModalities.Contains("image") ? "\n[Current model does not support images. The image will be omitted from this request.]" : "";
            var originalEncodedSize = EstimateBase64ByteCount(bytes.Length);
            if (processed.Image is null)
            {
                return new ToolResult(
                    [
                        new TextContent(
                            $"Read image file [{imageMimeType}]\n{string.Join("\n", processed.Hints)}{visionNote}")
                    ],
                    Details: ReadFileToolDetails.ForImage(
                        path,
                        imageMimeType,
                        bytes.Length,
                        originalEncodedSize,
                        imageOmitted: true));
            }

            var imageText = $"Read image file [{processed.Image.MimeType}]";
            if (processed.Hints.Count > 0) imageText += "\n" + string.Join("\n", processed.Hints);
            imageText += visionNote;
            var metadata = processed.ResizeResult!;

            return new ToolResult(
                [
                    new TextContent(imageText),
                    processed.Image
                ],
                Details: ReadFileToolDetails.ForImage(
                    path,
                    processed.Image.MimeType,
                    bytes.Length,
                    metadata.EstimatedBase64Bytes,
                    imageOmitted: false,
                    imageResized: metadata.WasResized,
                    originalWidth: metadata.OriginalWidth,
                    originalHeight: metadata.OriginalHeight,
                    width: metadata.Width,
                    height: metadata.Height));
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

    private static Task<string?> DetectImageMimeTypeAsync(string path, CancellationToken ct) =>
        CodingAgentImageMimeDetector.DetectSupportedImageMimeTypeFromFileAsync(path, ct);

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
