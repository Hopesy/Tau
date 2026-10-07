// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private bool _autoResizeImages = true;
    private bool AutoResizeInputImages => _sessionSettings?.Load().ImagesAutoResize ?? _autoResizeImages;

    /// <summary>【CodingAgent】【读取图片模型】为文件工具提供当前实体模型或虚拟模型最近使用的实体限制。</summary>
    /// <returns>有效模型。</returns>
    private Model GetEffectiveImageModel() => GetEffectiveContextModel();

    /// <summary>【CodingAgent】【图片入口】在启动扩展选定模型之后处理新提示图片，历史和排队输入不重复改写。</summary>
    /// <param name="messages">本轮待写入的新消息。</param><param name="token">取消信号。</param><returns>处理完成任务。</returns>
    private async Task NormalizePromptImagesAsync(List<ChatMessage> messages, CancellationToken token)
    {
        var limits = GetEffectiveContextModel().InputLimits?.Images?.Resize;
        var resize = AutoResizeInputImages;
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not UserMessage user || !user.Content.Any(block => block is ImageContent)) continue;
            var content = new List<ContentBlock>(); var hints = new List<string>(); var changed = false;
            foreach (var block in user.Content)
            {
                if (block is not ImageContent image) { content.Add(block); continue; }
                var result = await CodingAgentImageProcessing.ProcessAsync(image, resize, limits, token).ConfigureAwait(false);
                if (result.Image is not null) content.Add(result.Image);
                hints.AddRange(result.Hints);
                changed |= !ReferenceEquals(result.Image, image) || result.Hints.Count > 0;
            }
            if (!changed) continue;
            // 1. 【CodingAgent】【图片说明】转换、缩放及省略说明进入用户文本，便于坐标映射并持久化
            if (hints.Count > 0)
                content = [new TextContent(string.Join("\n", user.Content.OfType<TextContent>().Select(block => block.Text)) + "\n\n" + string.Join("\n", hints)), .. content.OfType<ImageContent>()];
            messages[index] = user with { Content = content };
        }
    }

    /// <summary>【CodingAgent】【工具图片顺序】保证图片处理位于所有扩展结果钩子之后，并覆盖嵌套工具调用。</summary>
    private void InstallToolImageNormalization() => _config = _config with
    { Interceptors = [.. _config.Interceptors.Where(item => item is not ImageToolInterceptor), new ImageToolInterceptor(this)] };

    /// <summary>【CodingAgent】【结果图片拦截】读取实时模型和设置，保留工具结果的用量及其他元数据。</summary>
    /// <param name="runner">所属会话运行器。</param>
    private sealed class ImageToolInterceptor(RuntimeCodingAgentRunner runner) : IToolInterceptor
    {
        /// <summary>【CodingAgent】【结果图片规范化】在工具结果发布和历史写入之前处理图片。</summary>
        /// <param name="context">调用上下文。</param><param name="result">前序钩子处理后的结果。</param>
        /// <param name="ct">取消信号。</param><returns>只在内容变化时复制的工具结果。</returns>
        public async Task<ToolResult> AfterToolCallAsync(ToolCallContext context, ToolResult result, CancellationToken ct = default)
        {
            var content = await CodingAgentImageProcessing.NormalizeToolResultAsync(result.Content, runner.AutoResizeInputImages,
                runner.GetEffectiveContextModel().InputLimits?.Images?.Resize, ct).ConfigureAwait(false);
            return ReferenceEquals(content, result.Content) ? result : result with { Content = content };
        }
    }
}
