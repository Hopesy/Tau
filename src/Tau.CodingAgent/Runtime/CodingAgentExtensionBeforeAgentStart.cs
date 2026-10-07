// 作者：xxx
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>启动前钩子生成的本轮提示配置及持久化自定义消息。</summary>
/// <param name="Options">修改后的本轮配置。</param>
/// <param name="Messages">处理器返回的自定义消息。</param>
public sealed record CodingAgentBeforeAgentStartResult(CodingAgentSystemPromptOptions Options, IReadOnlyList<ChatMessage> Messages);

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool HasBeforeAgentStartHandlers => _modules.Any(module => module.EventTypes.Contains("before_agent_start", StringComparer.Ordinal));

    /// <summary>【CodingAgent】【启动前钩子】按模块顺序传递可变提示配置，并收集返回的自定义消息。</summary>
    /// <param name="prompt">本轮完整用户文本。</param>
    /// <param name="images">本轮用户图片。</param>
    /// <param name="options">不会被原位修改的基础提示配置。</param>
    /// <param name="reportError">处理器错误接收器。</param>
    /// <param name="token">本轮取消信号。</param>
    /// <returns>下一模型请求的提示选项和待写入会话的消息。</returns>
    public CodingAgentBeforeAgentStartResult BeforeAgentStart(string prompt, IReadOnlyList<ImageContent> images,
        CodingAgentSystemPromptOptions options, Action<CodingAgentExtensionLifecycleEventError> reportError, CancellationToken token)
    {
        var current = CodingAgentSystemPrompt.Normalize(options);
        var messages = new List<ChatMessage>();
        foreach (var module in _modules.Where(module => module.EventTypes.Contains("before_agent_start", StringComparer.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToElement(new
            {
                type = "before_agent_start", prompt,
                images = images.Count == 0 ? null : images.Select(image => new { type = "image", data = image.Data, mimeType = image.MimeType }).ToArray(),
                systemPromptOptions = current,
                promptDefaults = new { preamble = CodingAgentSystemPrompt.DefaultPreamble, docs = CodingAgentSystemPrompt.BuildDocumentation() }
            }, PromptJsonOptions);
            var result = EmitRequestHook(module, payload, reportError, token);
            if (result is not { } transformed) continue;
            try
            {
                // 1. 【CodingAgent】【启动前钩子】验证完整选项后才交给下一模块，错误不会污染基础配置
                var next = transformed.GetProperty("systemPromptOptions").Deserialize<CodingAgentSystemPromptOptions>(PromptJsonOptions)
                    ?? throw new JsonException("Missing systemPromptOptions.");
                CodingAgentSystemPrompt.BuildSections(next);
                current = next;
                if (transformed.TryGetProperty("messages", out var returned))
                {
                    foreach (var message in returned.EnumerateArray())
                    {
                        var stored = message.Deserialize(CodingAgentSessionJsonContext.Default.CodingAgentSessionMessage)
                            ?? throw new JsonException("Invalid before_agent_start message.");
                        if (CodingAgentSessionStore.ToMessage(stored) is { Role: "custom" } custom) messages.Add(custom);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
            {
                reportError(new(module.FilePath, module.Scope, module.Runtime, "before_agent_start", ex.Message));
            }
        }
        return new(current, messages);
    }
}
