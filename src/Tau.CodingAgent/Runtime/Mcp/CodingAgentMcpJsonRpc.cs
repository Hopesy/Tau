// 作者：xxx
using System.Globalization;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【MCP】【JSON-RPC 边界】统一管道、HTTP 和客户端对报文类型及标识的判断。</summary>
public static class CodingAgentMcpJsonRpc
{
    /// <summary>【MCP】【标识校验】字符串和有限数字可作为请求标识，空值、对象及无穷大均不可。</summary>
    /// <param name="value">候选标识。</param><returns>是否符合标识类型。</returns>
    public static bool IsId(JsonElement value) => value.ValueKind == JsonValueKind.String ||
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);

    /// <summary>【MCP】【请求校验】检查协议、标识和方法，附加字段按上游规则保留。</summary><param name="message">候选报文。</param><returns>是否为请求。</returns>
    public static bool IsRequest(JsonElement message) => HasVersion(message) && message.TryGetProperty("id", out var id) && IsId(id) &&
        message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;

    /// <summary>【MCP】【通知校验】通知有字符串方法且不能包含 id 字段，即使字段值为空也拒绝。</summary><param name="message">候选报文。</param><returns>是否为通知。</returns>
    public static bool IsNotification(JsonElement message) => HasVersion(message) && !message.TryGetProperty("id", out _) &&
        message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;

    /// <summary>【MCP】【响应校验】result 和 error 互斥，错误必须包含数字 code 及字符串 message。</summary><param name="message">候选报文。</param><returns>是否为响应。</returns>
    public static bool IsResponse(JsonElement message)
    {
        if (!HasVersion(message) || !message.TryGetProperty("id", out var id) || !IsId(id)) return false;
        if (message.TryGetProperty("result", out _)) return !message.TryGetProperty("error", out _);
        return message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
            error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String;
    }

    /// <summary>【MCP】【报文校验】拒绝不属于三类协议报文的 JSON，调用方保留原文档生命周期。</summary>
    /// <param name="message">报文。</param><returns>原始元素。</returns>
    public static JsonElement Validate(JsonElement message) => IsResponse(message) || IsRequest(message) || IsNotification(message)
        ? message : throw new CodingAgentMcpException(-32600, "Invalid JSON-RPC message");

    /// <summary>【MCP】【标识归一】数值相等的 1、1.0 和 1e0 共享键，字符串标识与数字严格区分。</summary>
    /// <param name="id">有效协议标识。</param><returns>字典键。</returns>
    internal static string IdKey(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.String) return "s:" + id.GetString();
        var number = id.GetDouble();
        return "n:" + (number == 0 ? "0" : number.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>【MCP】【本机请求匹配】按数值匹配宿主生成的整数请求标识，支持科学计数法和小数写法。</summary>
    /// <param name="id">远端返回的标识。</param><param name="number">匹配成功后的整数。</param><returns>是否可能对应本机请求。</returns>
    internal static bool TryRequestId(JsonElement id, out long number)
    {
        number = 0;
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetDouble(out var value) || !double.IsFinite(value) ||
            value < 0 || value >= 9223372036854775808d || Math.Truncate(value) != value) return false;
        number = (long)value; return true;
    }

    /// <summary>【MCP】【协议版本】只接受对象中的字符串版本 2.0。</summary><param name="message">报文。</param><returns>版本是否有效。</returns>
    private static bool HasVersion(JsonElement message) => message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0";
}
