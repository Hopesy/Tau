// 作者：xxx
using System.Text.Json;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【Markdown 转换】在所属模块执行显示转换，失败或无效输出时保留原文。</summary>
    /// <param name="filePath">扩展模块路径。</param><param name="markdown">上一个转换器的输出。</param>
    /// <param name="context">消息类型、流式标记及实际内容宽度。</param><returns>用于渲染的 Markdown。</returns>
    public string TransformMarkdown(string filePath, string markdown, TuiMarkdownTransformContext context)
    {
        var arguments = JsonSerializer.SerializeToElement(new { markdown, context = new
        { messageType = context.MessageType, isStreaming = context.IsStreaming, availableWidth = context.AvailableWidth } });
        var execution = Execute(BuildPayload("transformMarkdown", filePath, _cwd, toolArgs: arguments));
        if (!execution.Success) return markdown;
        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            return ReadBool(document.RootElement, "ok") ? ReadString(document.RootElement, "markdown") ?? markdown : markdown;
        }
        catch (JsonException) { return markdown; }
    }
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【Markdown 链】按模块加载顺序冻结转换器列表，重载时重新创建，渲染期间无需重复扫描文件。</summary>
    /// <returns>显示转换函数；没有转换器时为空。</returns>
    public Func<string, TuiMarkdownTransformContext, string>? CreateMarkdownTransform()
    {
        var modules = LoadStatus().Modules.Where(module => module.HasMarkdownTransformer).ToArray();
        if (modules.Length == 0) return null;
        var cache = new Dictionary<(string Markdown, TuiMarkdownTransformContext Context), string>();
        var order = new Queue<(string Markdown, TuiMarkdownTransformContext Context)>();
        var gate = new object();
        var generation = _javaScriptRuntime.ResetGeneration;
        return (markdown, context) =>
        {
            // 1. 【CodingAgent】【渲染缓存】消息区域重绘会重复提交历史，相同正文、宽度和流式状态复用结果
            var key = (markdown, context);
            lock (gate)
            {
                if (generation != _javaScriptRuntime.ResetGeneration)
                { cache.Clear(); order.Clear(); generation = _javaScriptRuntime.ResetGeneration; }
                if (cache.TryGetValue(key, out var cached)) return cached;
            }
            foreach (var module in modules) markdown = _javaScriptRuntime.TransformMarkdown(module.FilePath, markdown, context);
            lock (gate)
            {
                if (!cache.ContainsKey(key)) order.Enqueue(key);
                cache[key] = markdown;
                while (order.Count > 128) cache.Remove(order.Dequeue());
            }
            return markdown;
        };
    }
}

public sealed partial class CodingAgentHost
{
    /// <summary>【CodingAgent】【Markdown 显示】在启动与扩展重载后更新显示链，不改写模型消息或持久历史。</summary>
    private void RefreshMarkdownTransformers() => _ui.SetMarkdownTransform(_extensionCommandStore?.CreateMarkdownTransform());
}
