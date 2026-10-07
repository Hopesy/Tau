// 作者：xxx
namespace Tau.Ai.Providers.OpenAi;

/// <summary>【AI】【工具标识】保留 Responses 复合调用标识的 item 差异，并满足 Completions 长度限制。</summary>
internal static class OpenAiToolCallIds
{
    /// <summary>【AI】【跨协议ID】规范化复合 ID，超长值使用确定性摘要；普通 OpenAI ID 截至 40 字符。</summary>
    /// <param name="id">来源工具 ID。</param><param name="model">目标模型。</param>
    /// <param name="source">来源助手，与共享转换器签名保持一致。</param><returns>可用于调用及结果的 ID。</returns>
    internal static string Normalize(string id, Model model, AssistantMessage source)
    {
        var separator = id.IndexOf('|');
        if (separator < 0) return model.Provider == "openai" && id.Length > 40 ? id[..40] : id;
        var callId = Sanitize(id[..separator]);
        var itemId = Sanitize(id[(separator + 1)..]);
        var combined = itemId.Length > 0 ? callId + "_" + itemId : callId;
        if (combined.Length <= 40) return combined;
        var hash = ShortHash.Compute(id);
        hash = hash[..Math.Min(8, hash.Length)];
        var prefixLength = Math.Min(callId.Length, Math.Max(1, 40 - hash.Length - 1));
        return callId[..prefixLength] + "_" + hash;
    }

    /// <summary>【AI】【ID字符】按 UTF-16 逐字符保留 ASCII 字母、数字、下划线及连字符。</summary>
    /// <param name="value">原始片段。</param><returns>非法字符替换为下划线后的片段。</returns>
    private static string Sanitize(string value) => new(value.Select(character =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? character : '_').ToArray());
}
