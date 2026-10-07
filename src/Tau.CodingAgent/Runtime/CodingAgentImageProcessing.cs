// 作者：xxx
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【图片处理结果】内联图片及供模型阅读的转换、尺寸或省略说明。</summary>
/// <param name="Image">可发送的图片，失败时为空。</param><param name="Hints">有序说明。</param>
internal sealed record CodingAgentProcessedImage(ImageContent? Image, IReadOnlyList<string> Hints)
{
    /// <summary>【CodingAgent】【图片元数据】提供文件工具展示尺寸和编码大小所需的缩放结果。</summary>
    internal CodingAgentImagePreprocessResult? ResizeResult { get; init; }
}

/// <summary>【CodingAgent】【内联图片】统一格式转换、模型缩放限制及失败提示。</summary>
internal static class CodingAgentImageProcessing
{
    internal const string ConversionFailure = "[Image omitted: could not be converted to a supported inline image format.]";
    internal const string ResizeFailure = "[Image omitted: could not be resized below the inline image size limit.]";

    /// <summary>【CodingAgent】【图片规范化】保留支持的格式，其他格式转换为 PNG，再应用模型尺寸和编码预算。</summary>
    /// <param name="image">输入图片。</param><param name="autoResize">是否执行自动缩放。</param>
    /// <param name="limits">模型限制，空值使用保守默认值。</param><param name="token">取消信号。</param><returns>图片及说明。</returns>
    internal static async Task<CodingAgentProcessedImage> ProcessAsync(ImageContent image, bool autoResize, ModelImageResizeOptions? limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = image.MimeType.Split(';')[0].Trim().ToLowerInvariant();
        var mime = source == "image/jpg" ? "image/jpeg" : source;
        var data = image.Data;
        string? convertedFrom = null;
        // 1. 【CodingAgent】【格式转换】禁用缩放仍须转换不支持的内联格式
        if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
        {
            var converted = await Task.Run(() => CodingAgentImageConverter.ConvertToPng(data, mime), token).WaitAsync(token).ConfigureAwait(false);
            if (converted is null) return new(null, [ConversionFailure]);
            data = converted.Data; mime = converted.MimeType; convertedFrom = source;
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return new(null, [ResizeFailure]); }
        var processed = await CodingAgentImageResizeWorker.Default.ProcessAsync(bytes, mime, autoResize,
            maxWidth: limits?.MaxWidth ?? CodingAgentImagePreprocessor.DefaultMaxWidth,
            maxHeight: limits?.MaxHeight ?? CodingAgentImagePreprocessor.DefaultMaxHeight,
            maxBase64Bytes: limits?.MaxBytes ?? CodingAgentImagePreprocessor.DefaultMaxBase64Bytes,
            cancellationToken: token, jpegQuality: limits?.JpegQuality ?? 80).ConfigureAwait(false);
        if (processed is null) return new(null, [ResizeFailure]);
        var hints = new List<string>();
        if (convertedFrom is not null && convertedFrom != processed.MimeType) hints.Add($"[Image converted from {convertedFrom} to {processed.MimeType}.]");
        if (CodingAgentImagePreprocessor.FormatDimensionNote(processed) is { } note) hints.Add(note);
        var normalized = processed.Data == image.Data && processed.MimeType == image.MimeType ? image : new ImageContent(processed.Data, processed.MimeType);
        return new(normalized, hints) { ResizeResult = processed };
    }

    /// <summary>【CodingAgent】【工具图片】保留失败的工具原图，成功转换时紧随图片补充提示，没有变化则返回原列表。</summary>
    /// <param name="content">工具内容。</param><param name="autoResize">是否缩放。</param><param name="limits">模型限制。</param>
    /// <param name="token">取消信号。</param><returns>规范化内容或原列表。</returns>
    internal static async Task<IReadOnlyList<ContentBlock>> NormalizeToolResultAsync(IReadOnlyList<ContentBlock> content,
        bool autoResize, ModelImageResizeOptions? limits, CancellationToken token)
    {
        if (!content.Any(block => block is ImageContent)) return content;
        var normalized = new List<ContentBlock>(); var changed = false;
        foreach (var block in content)
        {
            if (block is not ImageContent image) { normalized.Add(block); continue; }
            var result = await ProcessAsync(image, autoResize, limits, token).ConfigureAwait(false);
            if (result.Image is null) { normalized.Add(image); continue; }
            normalized.Add(result.Image);
            if (result.Hints.Count > 0) normalized.Add(new TextContent(string.Join("\n", result.Hints)));
            changed |= !ReferenceEquals(image, result.Image) || result.Hints.Count > 0;
        }
        return changed ? normalized : content;
    }
}
