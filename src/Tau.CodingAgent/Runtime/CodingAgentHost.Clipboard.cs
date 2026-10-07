// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentHost
{
    internal Func<string, string> ClipboardImagePathFactory { get; set; } = extension =>
        Path.Combine(Path.GetTempPath(), $"tau-clipboard-{Guid.NewGuid():N}.{extension}");

    /// <summary>【CodingAgent】【剪贴板粘贴】按文件、图片、文本优先级插入当前草稿，等待用户后续提交。</summary>
    /// <param name="token">宿主取消信号。</param><returns>处理结果或可显示错误。</returns>
    private async Task<CodingAgentCommandResult> PasteClipboardAsync(CancellationToken token)
    {
        try
        {
            // 1. 【CodingAgent】【复制文件】在完整草稿的光标前后补充必要分隔符，bash 使用独立路径引用
            token.ThrowIfCancellationRequested();
            var files = await _clipboard.ReadFilePathsAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (files is { Count: > 0 })
            {
                if (files.Any(path => path.Any(char.IsControl)))
                    return CodingAgentCommandResult.Error("Failed to paste from clipboard: Clipboard file path contains control characters");
                var draft = _ui.GetDraft();
                var cursor = Math.Clamp(_ui.GetDraftCursorIndex(), 0, draft.Length);
                var paths = draft.TrimStart().StartsWith('!')
                    ? string.Join(" ", files.Select(QuoteClipboardShellPath)) : string.Join("\n", files);
                var leading = cursor > 0 && !char.IsWhiteSpace(draft[cursor - 1]) ? " " : string.Empty;
                var trailing = cursor < draft.Length && !char.IsWhiteSpace(draft[cursor]) ? " " : string.Empty;
                _ui.InsertTextAtCursor(leading + paths + trailing);
                return new(true, false, null);
            }

            // 2. 【CodingAgent】【图片路径】图片持久保留在临时目录供随后读取，只将路径插入编辑器
            var image = await _clipboard.ReadImageAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (image is not null)
            {
                var path = ClipboardImagePathFactory(SystemCodingAgentClipboard.ExtensionForImageMimeType(image.MimeType) ?? "png");
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await stream.WriteAsync(image.Bytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _ui.InsertTextAtCursor(path);
                return new(true, false, null);
            }

            // 3. 【CodingAgent】【文本回退】保留原始内容，由编辑器统一处理换行和制表符
            var text = await _clipboard.ReadTextAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(text)) _ui.InsertTextAtCursor(text);
            return new(true, false, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return CodingAgentCommandResult.Error($"Failed to paste from clipboard: {ex.Message}"); }
    }

    /// <summary>【CodingAgent】【Shell 路径引用】安全字符直接使用，其他路径采用 POSIX 单引号并转义内嵌单引号。</summary>
    /// <param name="path">原始文件路径。</param><returns>可放入 bash 命令的路径。</returns>
    private static string QuoteClipboardShellPath(string path) => path.Length > 0 &&
        path.All(character => char.IsAsciiLetterOrDigit(character) || "_-./~:@".Contains(character))
            ? path : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
