// 作者：xxx
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Google;

internal static partial class GoogleMessageConverter
{
    /// <summary>【Google】【历史重放】统一三种传输的来源转换、签名保留及工具结果分组。</summary>
    /// <param name="model">目标模型及能力。</param><param name="messages">已折叠系统声明的会话。</param>
    /// <returns>Google contents 数组。</returns>
    public static List<object> ConvertMessages(Model model, IReadOnlyList<ChatMessage> messages)
    {
        // 1. 【Google】【来源转换】仅跨模型改写工具 ID，并同步结果、过滤失败消息和补齐遗漏结果
        var transformed = MessageTransformer.TransformMessages(messages, model, (id, _, _) => NormalizeToolCallId(model.Id, id));
        var result = new List<object>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in transformed)
        {
            switch (message)
            {
                case UserMessage user:
                    var userParts = user.Content.Select(ConvertUserPart).Where(part => part is not null).Cast<object>().ToList();
                    if (userParts.Count > 0) result.Add(BuildContent("user", userParts));
                    break;
                case AssistantMessage assistant:
                    var parts = ConvertAssistantParts(model, assistant);
                    if (parts.Count > 0) result.Add(BuildContent("model", parts));
                    toolNames.Clear();
                    foreach (var call in assistant.Content.OfType<ToolCallContent>()) toolNames[call.Id] = call.Name;
                    break;
                case ToolResultMessage tool:
                    AddToolResult(result, model, tool, tool.ToolName ?? toolNames.GetValueOrDefault(tool.ToolCallId));
                    break;
            }
        }
        return result;
    }

    /// <summary>【Google】【内容容器】构造指定角色的内容数组。</summary>
    /// <param name="role">用户或模型角色。</param><param name="parts">协议内容块。</param><returns>内容对象。</returns>
    private static Dictionary<string, object> BuildContent(string role, List<object> parts) => new() { ["role"] = role, ["parts"] = parts };

    /// <summary>【Google】【用户内容】保留文字和内嵌图片的原始顺序。</summary>
    /// <param name="block">消息内容块。</param><returns>协议内容或 null。</returns>
    private static object? ConvertUserPart(ContentBlock block) => block switch
    {
        TextContent text => new Dictionary<string, object> { ["text"] = SanitizeText(text.Text) },
        ImageContent image => ImagePart(image),
        _ => null
    };

    /// <summary>【Google】【图片回放】构造内嵌图片内容。</summary>
    /// <param name="image">原始 MIME 和 Base64 数据。</param><returns>图片协议对象。</returns>
    private static Dictionary<string, object> ImagePart(ImageContent image) => new()
    {
        ["inlineData"] = new Dictionary<string, string> { ["mimeType"] = image.MimeType, ["data"] = image.Data }
    };

    /// <summary>【Google】【助手回放】保持思考、正文和函数边界，仅携带同模型的有效签名。</summary>
    /// <param name="model">目标模型。</param><param name="message">经过共享转换的助手消息。</param>
    /// <returns>可回放的协议内容块。</returns>
    private static List<object> ConvertAssistantParts(Model model, AssistantMessage message)
    {
        var parts = new List<object>();
        var sameModel = message.Provider == model.Provider && message.Model == model.Id;
        foreach (var block in message.Content)
        {
            Dictionary<string, object>? part = null;
            var signature = sameModel ? ValidSignature(block switch
            {
                TextContent text => text.TextSignature,
                ThinkingContent thinking => thinking.ThinkingSignature,
                ToolCallContent call => call.ThoughtSignature,
                _ => null
            }) : null;
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text) || signature is not null:
                    part = new() { ["text"] = SanitizeText(text.Text) };
                    break;
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking) || signature is not null:
                    part = new() { ["text"] = SanitizeText(thinking.Thinking) };
                    if (sameModel) part["thought"] = true;
                    break;
                case ToolCallContent call:
                    var function = new Dictionary<string, object> { ["name"] = call.Name, ["args"] = ParseArgs(call.Arguments) };
                    if (RequiresToolCallId(model.Id)) function["id"] = call.Id;
                    part = new() { ["functionCall"] = function };
                    break;
            }
            if (part is null) continue;
            if (signature is not null) part["thoughtSignature"] = signature;
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>【Google】【工具结果】合并相邻函数响应，按模型版本内嵌图片或追加独立图片轮次。</summary>
    /// <param name="contents">正在构建的会话。</param><param name="model">目标模型。</param>
    /// <param name="message">工具结果。</param><param name="toolName">显式或从调用恢复的工具名。</param>
    private static void AddToolResult(List<object> contents, Model model, ToolResultMessage message, string? toolName)
    {
        // 1. 【Google】【工具正文】串联全部文字，错误和成功使用不同响应键
        var text = string.Join("\n", message.Content.OfType<TextContent>().Select(part => part.Text));
        var images = message.Content.OfType<ImageContent>().Select(image => (object)ImagePart(image)).ToList();
        var inlineImages = GetGeminiMajorVersion(model.Id) is not { } major || major >= 3;
        var response = new Dictionary<string, object>
        {
            ["response"] = new Dictionary<string, object>
            {
                [message.IsError ? "error" : "output"] = text.Length > 0 ? SanitizeText(text) : images.Count > 0 ? "(see attached image)" : ""
            }
        };
        if (toolName is not null) response["name"] = toolName;
        if (RequiresToolCallId(model.Id)) response["id"] = message.ToolCallId;
        if (images.Count > 0 && inlineImages) response["parts"] = images;
        var part = new Dictionary<string, object> { ["functionResponse"] = response };
        // 2. 【Google】【结果分组】只有上一个用户轮次已经包含函数响应时才并入
        if (contents.LastOrDefault() is Dictionary<string, object> last && (string)last["role"] == "user" &&
            last["parts"] is List<object> parts && parts.OfType<Dictionary<string, object>>().Any(item => item.ContainsKey("functionResponse")))
            parts.Add(part);
        else contents.Add(BuildContent("user", [part]));
        if (images.Count > 0 && !inlineImages)
            contents.Add(BuildContent("user", [new Dictionary<string, object> { ["text"] = "Tool result image:" }, .. images]));
    }

    /// <summary>【Google】【签名验证】只验证主线 Base64 格式，保持原签名字节表示。</summary>
    /// <param name="signature">候选签名。</param><returns>有效签名或 null。</returns>
    private static string? ValidSignature(string? signature) => !string.IsNullOrEmpty(signature) && signature.Length % 4 == 0 &&
        Regex.IsMatch(signature, "\\A[A-Za-z0-9+/]+={0,2}\\z", RegexOptions.CultureInvariant) ? signature : null;

    /// <summary>【Google】【模型版本】提取普通及 live Gemini 名称中的主版本号。</summary>
    /// <param name="modelId">模型 ID。</param><returns>Gemini 主版本，其他模型为 null。</returns>
    private static double? GetGeminiMajorVersion(string modelId)
    {
        var match = Regex.Match(modelId.ToLowerInvariant(), "^gemini(?:-live)?-([0-9]+)", RegexOptions.CultureInvariant);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>【Google】【调用标识】判断后端是否要求函数调用和结果具有显式 ID。</summary>
    /// <param name="modelId">模型 ID。</param><returns>是否需要 ID。</returns>
    private static bool RequiresToolCallId(string modelId) => modelId.StartsWith("claude-", StringComparison.Ordinal) ||
        modelId.StartsWith("gpt-oss-", StringComparison.Ordinal) || GetGeminiMajorVersion(modelId) >= 3;

    /// <summary>【Google】【调用标识】跨模型回放时按后端约束替换字符并截断。</summary>
    /// <param name="modelId">目标模型 ID。</param><param name="id">原调用 ID。</param><returns>兼容的调用 ID。</returns>
    private static string NormalizeToolCallId(string modelId, string id)
    {
        if (!RequiresToolCallId(modelId)) return id;
        var normalized = Regex.Replace(id, "[^a-zA-Z0-9_-]", "_");
        return normalized[..Math.Min(64, normalized.Length)];
    }

    /// <summary>【Google】【函数参数】解析已保存的 JSON 参数，空值和损坏旧数据退回空对象。</summary>
    /// <param name="arguments">参数 JSON。</param><returns>参数对象。</returns>
    private static object ParseArgs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new Dictionary<string, object>();
        try
        {
            var value = JsonSerializer.Deserialize(arguments, GoogleJsonContext.Default.JsonElement);
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new Dictionary<string, object>() : value;
        }
        catch (JsonException) { return new Dictionary<string, object>(); }
    }
}
