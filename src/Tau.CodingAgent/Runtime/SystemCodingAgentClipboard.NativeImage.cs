// 作者：xxx
using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed partial class SystemCodingAgentClipboard
{
    internal Func<CancellationToken, Task<byte[]?>>? NativeImageReader { get; set; }

    /// <summary>【CodingAgent】【原生图片】读取可选原生后端，macOS 默认通过 AppKit 获取 PNG 或将 TIFF 转换为 PNG。</summary>
    /// <param name="token">取消信号。</param><returns>经 MIME 嗅探的图片，空内容或不支持的平台返回空引用。</returns>
    private async Task<CodingAgentClipboardImage?> ReadNativeImageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[]? bytes;
        if (NativeImageReader is not null) bytes = await NativeImageReader(token).ConfigureAwait(false);
        else if (_platform == CodingAgentClipboardPlatform.MacOS)
        {
            // 1. 【CodingAgent】【AppKit 图片】复用上游原生实现的 PNG 优先及 TIFF 转换顺序，空结果不启动后续读取
            const string script = "ObjC.import('AppKit'); const board=$.NSPasteboard.generalPasteboard; let result=''; " +
                "let png=board.dataForType($.NSPasteboardTypePNG); if(!(png.length>0)){ const image=$.NSImage.alloc.initWithPasteboard(board); " +
                "const tiff=image.TIFFRepresentation; if(tiff.length>0){ const bitmap=$.NSBitmapImageRep.imageRepWithData(tiff); " +
                "png=bitmap.representationUsingTypeProperties($.NSBitmapImageFileTypePNG,$({})); }} " +
                "if(png.length>0) result=ObjC.unwrap(png.base64EncodedStringWithOptions(0)); result;";
            var result = await _runner.RunAsync("osascript", ["-l", "JavaScript", "-e", script], null,
                PowerShellTimeoutMs, MaxBufferBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.Ok || result.Stdout.Length == 0) return null;
            try { bytes = Convert.FromBase64String(Encoding.UTF8.GetString(result.Stdout).Trim()); }
            catch (FormatException) { return null; }
        }
        else return null;
        token.ThrowIfCancellationRequested();
        return bytes is { Length: > 0 } ? new(bytes, CodingAgentImageMimeDetector.DetectSupportedImageMimeType(bytes) ?? "application/octet-stream") : null;
    }
}
