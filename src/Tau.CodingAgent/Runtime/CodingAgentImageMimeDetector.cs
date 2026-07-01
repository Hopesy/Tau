namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【图片MIME嗅探】对齐上游 <c>utils/mime.ts</c>，根据文件头字节判断 CodingAgent 支持的内联图片 MIME。
/// 支持 JPEG、PNG、GIF、WebP，并保留上游的严格规则：拒绝 <c>0xF7</c> JPEG 标记、无效 IHDR 和动态 PNG。
/// </summary>
public static class CodingAgentImageMimeDetector
{
    /// <summary>
    /// 从文件头读取用于 MIME 嗅探的最大字节数，足够扫描 PNG 前置 chunk 中的 <c>acTL</c> 动画控制标记。
    /// </summary>
    public const int ImageTypeSniffBytes = 4100;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>
    /// 根据文件头字节检测 CodingAgent 支持的图片 MIME 类型。
    /// </summary>
    /// <param name="buffer">待检测的文件头字节缓冲区。</param>
    /// <returns>检测成功时返回 MIME 字符串；不是受支持图片或属于被拒绝格式时返回 <see langword="null"/>。</returns>
    public static string? DetectSupportedImageMimeType(ReadOnlySpan<byte> buffer)
    {
        // 1. JPEG 只接受普通 SOI 标记，保留上游拒绝 0xF7 标记的规则
        if (StartsWith(buffer, [0xff, 0xd8, 0xff]))
        {
            return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        }

        // 2. PNG 必须有有效 IHDR，并且不能在 IDAT 前出现 acTL 动画控制 chunk
        if (StartsWith(buffer, PngSignature))
        {
            return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        }

        // 3. GIF 和 WebP 使用上游相同的魔数前缀检查
        if (StartsWithAscii(buffer, 0, "GIF"))
        {
            return "image/gif";
        }

        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP"))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// 从文件读取头部字节并检测 CodingAgent 支持的图片 MIME 类型。
    /// </summary>
    /// <param name="filePath">需要检测的文件路径。</param>
    /// <param name="cancellationToken">取消异步读取的令牌。</param>
    /// <returns>检测成功时返回 MIME 字符串；不是受支持图片或属于被拒绝格式时返回 <see langword="null"/>。</returns>
    public static async Task<string?> DetectSupportedImageMimeTypeFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var buffer = new byte[ImageTypeSniffBytes];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return DetectSupportedImageMimeType(buffer.AsSpan(0, totalRead));
    }

    /// <summary>
    /// 判断 PNG 字节是否包含有效的首个 IHDR chunk。
    /// </summary>
    /// <param name="buffer">待检查的 PNG 文件头缓冲区。</param>
    /// <returns>首个 chunk 是长度 13 的 IHDR 时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool IsPng(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 16 &&
        ReadUint32BE(buffer, PngSignature.Length) == 13 &&
        StartsWithAscii(buffer, 12, "IHDR");

    /// <summary>
    /// 判断 PNG 是否在首个 IDAT 前包含 acTL 动画控制 chunk。
    /// </summary>
    /// <param name="buffer">待检查的 PNG 文件头缓冲区。</param>
    /// <returns>检测到动态 PNG 时返回 <see langword="true"/>；普通 PNG 或无法继续解析时返回 <see langword="false"/>。</returns>
    private static bool IsAnimatedPng(ReadOnlySpan<byte> buffer)
    {
        var offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUint32BE(buffer, offset);
            var chunkTypeOffset = offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL"))
            {
                return true;
            }

            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT"))
            {
                return false;
            }

            var nextOffset = offset + 8L + chunkLength + 4L;
            if (nextOffset <= offset || nextOffset > buffer.Length)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return false;
    }

    /// <summary>
    /// 从指定偏移读取大端序 32 位无符号整数。
    /// </summary>
    /// <param name="buffer">提供原始字节的缓冲区。</param>
    /// <param name="offset">开始读取的字节偏移。</param>
    /// <returns>解析得到的整数；越界字节按 0 处理以保持嗅探逻辑容错。</returns>
    private static long ReadUint32BE(ReadOnlySpan<byte> buffer, int offset) =>
        (long)(offset < buffer.Length ? buffer[offset] : 0) * 0x1000000 +
        ((offset + 1 < buffer.Length ? buffer[offset + 1] : 0) << 16) +
        ((offset + 2 < buffer.Length ? buffer[offset + 2] : 0) << 8) +
        (offset + 3 < buffer.Length ? buffer[offset + 3] : 0);

    /// <summary>
    /// 判断缓冲区开头是否匹配指定字节序列。
    /// </summary>
    /// <param name="buffer">待检查的字节缓冲区。</param>
    /// <param name="bytes">期望匹配的字节序列。</param>
    /// <returns>缓冲区以指定字节序列开头时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> bytes)
    {
        if (buffer.Length < bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            if (buffer[index] != bytes[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判断缓冲区指定偏移处是否匹配 ASCII 文本。
    /// </summary>
    /// <param name="buffer">待检查的字节缓冲区。</param>
    /// <param name="offset">开始匹配的字节偏移。</param>
    /// <param name="text">期望匹配的 ASCII 文本。</param>
    /// <returns>指定位置完整匹配文本时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (buffer[offset + index] != (byte)text[index])
            {
                return false;
            }
        }

        return true;
    }
}
