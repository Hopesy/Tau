// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers.Mistral;

public sealed partial class MistralProvider
{
    /// <summary>【Mistral】【思考映射】按模型等级表选择 effort，没有等级表时使用 prompt_mode。</summary>
    /// <param name="model">模型及能力。</param><param name="requested">用户思考等级。</param><returns>模式与原生等级。</returns>
    internal static (string? PromptMode, string? ReasoningEffort) ResolveThinkingOptions(Model model, ThinkingLevel? requested)
    {
        if (!model.Reasoning) return (null, null);
        var level = requested is null ? null : ModelCatalog.ClampThinkingLevel(model,
            requested == ThinkingLevel.ExtraHigh ? "xhigh" : requested.Value.ToString().ToLowerInvariant());
        if (level == "off") level = null;
        if (model.ThinkingLevelMap is not { } mapping) return (level is null ? null : "reasoning", null);
        return (null, level is null ? mapping.GetValueOrDefault("off") : mapping.GetValueOrDefault(level) ?? "high");
    }

    /// <summary>【Mistral】【工具选择】统一直接简化入口与配置入口的字符串、命名函数和 JSON 选择。</summary>
    /// <param name="choice">通用工具选择。</param><returns>原生选择或 null。</returns>
    internal static MistralToolChoice? ConvertToolChoice(object? choice) => choice switch
    {
        MistralToolChoice typed => typed,
        string value when value.Length > 0 => MistralToolChoice.FromString(value),
        JsonElement { ValueKind: JsonValueKind.String } value => MistralToolChoice.FromString(value.GetString()!),
        JsonElement { ValueKind: JsonValueKind.Object } value when value.TryGetProperty("type", out var type) && type.GetString() == "function" &&
            value.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object &&
            function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String => MistralToolChoice.Function(name.GetString()!),
        _ => null
    };

    /// <summary>【Mistral】【会话缓存】有会话 ID 且未显式关闭缓存时启用 affinity 和请求缓存键。</summary>
    /// <param name="options">请求配置。</param><returns>是否启用缓存关联。</returns>
    private static bool UsesPromptCaching(StreamOptions options) => !string.IsNullOrEmpty(options.SessionId) &&
        (!options.HasExplicitCacheRetention || options.CacheRetention != CacheRetention.None);

    /// <summary>【Mistral】【请求头覆盖】显式 affinity 包括 null 删除都优先于自动会话关联。</summary>
    /// <param name="headers">模型或请求头。</param><returns>是否显式声明 affinity。</returns>
    private static bool HasAffinityOverride(IDictionary<string, string>? headers) =>
        headers?.Keys.Any(key => key.Equals("x-affinity", StringComparison.OrdinalIgnoreCase)) == true;
}
