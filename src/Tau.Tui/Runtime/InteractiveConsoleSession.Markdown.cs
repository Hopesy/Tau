// 作者：xxx
using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

/// <summary>【TUI】【Markdown 上下文】显示用途的消息类型、流式状态和扣除外框后的可用列数。</summary>
public sealed record TuiMarkdownTransformContext(string MessageType, bool IsStreaming, int AvailableWidth);

public sealed partial class InteractiveConsoleSession
{
    private Func<string, TuiMarkdownTransformContext, string>? _markdownTransform;
    private bool _streamingApplyMarkdown = true;
    private bool _streamingIsLive = true;
    private int _streamingContentWidth = 75;

    /// <summary>【TUI】【Markdown 注册】替换显示链并通知重绘，保留全部原始 transcript 文本。</summary>
    /// <param name="transform">同步转换函数；空值恢复默认显示。</param>
    public void SetMarkdownTransform(Func<string, TuiMarkdownTransformContext, string>? transform)
    {
        lock (_stateSync)
        {
            EnsureStreamingLineClosed();
            _markdownTransform = transform;
            NotifyTranscriptChanged();
        }
    }

    /// <summary>【TUI】【显示快照】创建原始消息与宽度感知的转换函数，流式快照每次使用完整累计正文。</summary>
    /// <param name="kind">消息类别。</param><param name="text">原始正文。</param><param name="streaming">是否仍在输出。</param>
    /// <param name="apply">隐藏提示等纯标签是否允许转换。</param><returns>供消息区域渲染的快照。</returns>
    private TuiMessage CreateDisplayMessage(TranscriptEntryKind kind, string text, bool streaming, bool apply)
    {
        var transform = _markdownTransform;
        var type = MarkdownMessageType(kind);
        return new(ToMessageRole(kind), text)
        {
            MarkdownTransform = !apply || transform is null || type is null ? null
                : (markdown, width) => ApplyDisplayTransform(transform, markdown, new(type, streaming, Math.Max(1, width)))
        };
    }

    /// <summary>【TUI】【兼容显示】对传统终端的完整正文应用与组合界面一致的转换规则。</summary>
    /// <param name="markdown">原始正文。</param><param name="kind">消息类别。</param><param name="streaming">流式状态。</param>
    /// <param name="width">正文宽度。</param><returns>显示正文。</returns>
    private string TransformDisplayMarkdown(string markdown, TranscriptEntryKind kind, bool streaming, int width) =>
        _markdownTransform is { } transform && MarkdownMessageType(kind) is { } type
            ? ApplyDisplayTransform(transform, markdown, new(type, streaming, Math.Max(1, width))) : markdown;

    /// <summary>【TUI】【转换隔离】显示转换失败不影响消息保存和后续会话操作。</summary>
    /// <param name="transform">转换函数。</param><param name="markdown">原始正文。</param><param name="context">显示上下文。</param>
    /// <returns>有效结果或原文，允许显式空字符串隐藏正文。</returns>
    private static string ApplyDisplayTransform(Func<string, TuiMarkdownTransformContext, string> transform, string markdown, TuiMarkdownTransformContext context)
    {
        try { return transform(markdown, context) ?? markdown; }
        catch (Exception) { return markdown; }
    }

    /// <summary>【TUI】【转换范围】仅用户、助手正文和可见思考参与 Markdown 转换。</summary>
    /// <param name="kind">消息类别。</param><returns>原生消息类别或空值。</returns>
    private static string? MarkdownMessageType(TranscriptEntryKind kind) => kind switch
    { TranscriptEntryKind.User => "user", TranscriptEntryKind.Assistant => "assistant", TranscriptEntryKind.Thinking => "assistant-thinking", _ => null };
}
