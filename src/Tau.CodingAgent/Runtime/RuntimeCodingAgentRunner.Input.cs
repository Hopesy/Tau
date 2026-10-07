// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentSkillStore? _inputSkills;
    private CodingAgentPromptTemplateStore? _inputTemplates;
    public string InputSource { get; set; } = "interactive";

    /// <summary>【CodingAgent】【输入资源】绑定技能和模板存储，所有输入入口在扩展转换后使用同一展开顺序。</summary>
    /// <param name="skills">技能存储，空值保留已有绑定。</param>
    /// <param name="templates">模板存储，空值保留已有绑定。</param>
    /// <param name="source">当前宿主的输入来源。</param>
    public void ConfigureInputResources(CodingAgentSkillStore? skills, CodingAgentPromptTemplateStore? templates, string source = "interactive")
    {
        if (source is not ("interactive" or "rpc" or "extension")) throw new ArgumentException("Invalid input source.", nameof(source));
        _inputSkills = skills ?? _inputSkills;
        _inputTemplates = templates ?? _inputTemplates;
        InputSource = source;
    }

    /// <summary>【CodingAgent】【输入准备】先让扩展转换原始输入，再展开技能与模板；接管时不写入会话。</summary>
    /// <param name="message">待提交或排队的消息。</param>
    /// <param name="streamingBehavior">排队方式，普通输入为空。</param>
    /// <param name="token">取消信号。</param>
    /// <param name="source">可选来源覆盖，用于扩展主动发送的消息。</param>
    /// <param name="expandTemplates">是否展开技能和模板。</param>
    /// <returns>处理后的消息，扩展接管时为空。</returns>
    private ChatMessage? PrepareInputMessage(ChatMessage message, string? streamingBehavior, CancellationToken token,
        string? source = null, bool expandTemplates = true)
    {
        if (message is not UserMessage user) return message;
        var text = string.Join("\n", user.Content.OfType<TextContent>().Select(block => block.Text));
        var images = user.Content.OfType<ImageContent>().ToArray();
        var result = _extensionLifecycleEventSink?.TransformInput(text, images, source ?? InputSource, streamingBehavior,
            error => LogExtensionEventError(error, CreateRunLogContext()), token) ?? new(false, text, images);
        if (result.Handled) return null;
        var expanded = result.Text;
        if (expandTemplates && _inputSkills?.TryExpand(expanded, out var skill, out _) == true) expanded = skill;
        if (expandTemplates && _inputTemplates?.TryExpand(expanded, out var template, out _) == true) expanded = template;
        if (expanded == text && result.Images.SequenceEqual(images)) return message;
        return user with { Content = [new TextContent(expanded), .. result.Images] };
    }

    /// <summary>【CodingAgent】【排队输入】处理输入事件后放入指定队列，扩展接管的输入不增加队列长度。</summary>
    /// <param name="message">未展开的输入消息。</param>
    /// <param name="behavior">steer 或 followUp。</param>
    /// <param name="token">输入处理取消信号。</param><returns>扩展接管或排队结果。</returns>
    private CodingAgentQueuedInputDisposition QueueInput(ChatMessage message, string behavior, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is UserMessage user && IsExtensionCommand(string.Join("\n", user.Content.OfType<TextContent>().Select(block => block.Text))))
            throw new InvalidOperationException("Extension commands cannot be queued. Use RunAsync to execute the command.");
        if (PrepareInputMessage(message, IsStreaming ? behavior : null, token) is not { } prepared) return CodingAgentQueuedInputDisposition.Handled;
        EnqueuePreparedInput(prepared, behavior != "steer");
        return CodingAgentQueuedInputDisposition.Queued;
    }
}
