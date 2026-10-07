// 作者：xxx
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>扩展输入处理结果。</summary>
/// <param name="Handled">扩展已接管输入，不再提交给 Agent。</param>
/// <param name="Text">后续技能和模板展开使用的文本。</param>
/// <param name="Images">保留或替换后的图片。</param>
public sealed record CodingAgentInputResult(bool Handled, string Text, IReadOnlyList<ImageContent> Images);

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【输入钩子】按模块顺序转换原始输入，接管结果立即终止后续处理器。</summary>
    /// <param name="text">未展开的输入文本。</param>
    /// <param name="images">输入图片。</param>
    /// <param name="source">interactive、rpc 或 extension。</param>
    /// <param name="streamingBehavior">排队输入的 steer 或 followUp，空闲输入为空。</param>
    /// <param name="reportError">处理器错误报告回调。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>扩展接管状态及最终输入。</returns>
    public CodingAgentInputResult TransformInput(string text, IReadOnlyList<ImageContent> images, string source,
        string? streamingBehavior, Action<CodingAgentExtensionLifecycleEventError> reportError, CancellationToken token)
    {
        var current = new CodingAgentInputResult(false, text, images);
        foreach (var module in _modules.Where(module => module.EventTypes.Contains("input", StringComparer.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToElement(new
            {
                type = "input", text = current.Text, source, streamingBehavior,
                images = current.Images.Count == 0 ? null : current.Images.Select(image => new { type = "image", data = image.Data, mimeType = image.MimeType }).ToArray()
            }, PromptJsonOptions);
            var transformed = EmitRequestHook(module, payload, reportError, token);
            if (transformed is not { } result) continue;
            if (result.GetProperty("action").GetString() == "handled") return current with { Handled = true };
            try
            {
                var updated = result.GetProperty("text").GetString() ?? throw new JsonException("Missing transformed text.");
                var updatedImages = current.Images;
                if (result.TryGetProperty("images", out var returned) && returned.ValueKind != JsonValueKind.Null)
                    updatedImages = returned.EnumerateArray().Select(image => new ImageContent(
                        image.GetProperty("data").GetString() ?? throw new JsonException("Missing image data."),
                        image.GetProperty("mimeType").GetString() ?? throw new JsonException("Missing image mimeType."))).ToArray();
                current = new(false, updated, updatedImages);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                reportError(new(module.FilePath, module.Scope, module.Runtime, "input", ex.Message));
            }
        }
        return current;
    }
}
