// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>【AI】【思考明细】验证主线支持的结构化明细和旧工具加密签名。</summary>
internal static class OpenAiReasoningDetails
{
    /// <summary>【AI】【明细校验】只接受三种明细及类型合法的公共字段，保留未知扩展字段。</summary>
    /// <param name="value">待校验对象。</param><returns>是否属于可重放明细。</returns>
    internal static bool IsValid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
        if (value.TryGetProperty("format", out var format) && format.ValueKind != JsonValueKind.String) return false;
        if (value.TryGetProperty("index", out var index) && index.ValueKind != JsonValueKind.Number) return false;
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        return type.GetString() switch
        {
            "reasoning.summary" => HasString(value, "summary"),
            "reasoning.encrypted" => HasString(value, "data"),
            "reasoning.text" => HasString(value, "text") &&
                (!value.TryGetProperty("signature", out var signature) || signature.ValueKind is JsonValueKind.Null or JsonValueKind.String),
            _ => false
        };
    }

    /// <summary>【AI】【签名数组】读取非空且全部有效的明细数组，单对象及损坏签名不参与重放。</summary>
    /// <param name="signature">思考签名 JSON。</param><returns>独立 JSON 数组或空值。</returns>
    internal static JsonElement? ParseArray(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            using var document = JsonDocument.Parse(signature);
            var value = document.RootElement;
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && value.EnumerateArray().All(IsValid)
                ? value.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>【AI】【旧工具签名】仅将具有非空 ID 和密文的旧 encrypted 对象恢复为明细。</summary>
    /// <param name="signature">工具携带的旧签名。</param><returns>独立 JSON 明细或空值。</returns>
    internal static JsonElement? ParseLegacyEncrypted(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            using var document = JsonDocument.Parse(signature);
            var value = document.RootElement;
            return IsValid(value) && value.GetProperty("type").GetString() == "reasoning.encrypted" &&
                HasString(value, "id") && value.GetProperty("id").GetString()!.Length > 0 &&
                value.GetProperty("data").GetString()!.Length > 0 ? value.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>【AI】【明细字段】验证必需文本字段。</summary>
    /// <param name="value">对象。</param><param name="name">属性名。</param><returns>是否为字符串。</returns>
    private static bool HasString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String;
}
