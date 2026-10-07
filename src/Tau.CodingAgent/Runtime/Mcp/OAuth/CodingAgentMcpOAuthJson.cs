// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP OAuth 数据校验】验证发现文档、令牌与动态客户端，并保留协议允许的扩展元数据。</summary>
public static class CodingAgentMcpOAuthJson
{
    /// <summary>【CodingAgent】【资源元数据】校验资源与授权服务器 URL，保留未知字段。</summary><param name="value">服务端 JSON。</param><returns>独立且已验证的资源元数据。</returns>
    public static JsonObject ProtectedResource(JsonNode? value)
    {
        var result = Object(value, "OAuth protected resource metadata");
        Url(result["resource"], "resource");
        Strings(result, "authorization_servers");
        Strings(result, "scopes_supported");
        if (result["authorization_servers"] is JsonArray servers)
            foreach (var server in servers) Url(server, "authorization server URL");
        return result;
    }

    /// <summary>【CodingAgent】【授权服务器元数据】要求发行方、授权和令牌端点及响应类型，兼容可选标志缺省。</summary><param name="value">发现文档。</param><returns>独立且已验证的文档。</returns>
    public static JsonObject AuthorizationServer(JsonNode? value)
    {
        var result = Object(value, "authorization server metadata");
        foreach (var key in new[] { "issuer", "authorization_endpoint", "token_endpoint" }) Url(result[key], key);
        if (Absent(result["registration_endpoint"])) result.Remove("registration_endpoint");
        else Url(result["registration_endpoint"], "registration_endpoint");
        foreach (var key in new[] { "scopes_supported", "response_types_supported", "grant_types_supported", "token_endpoint_auth_methods_supported", "code_challenge_methods_supported" })
            Strings(result, key);
        if (result["response_types_supported"] is not JsonArray) throw Invalid("response_types_supported");
        foreach (var key in new[] { "client_id_metadata_document_supported", "authorization_response_iss_parameter_supported" })
            if (result[key] is not JsonValue flag || !flag.TryGetValue<bool>(out _)) result.Remove(key);
        return result;
    }

    /// <summary>【CodingAgent】【令牌响应】仅返回标准令牌字段，将有效数字字符串转换为 expires_in，并忽略空可选字段。</summary><param name="value">令牌响应。</param><returns>已验证的令牌。</returns>
    public static JsonObject Tokens(JsonNode? value)
    {
        var input = Object(value, "OAuth token response");
        var result = new JsonObject { ["access_token"] = Required(input["access_token"], "access_token"), ["token_type"] = Required(input["token_type"], "token_type") };
        if (!Absent(input["expires_in"]))
        {
            var number = Number(input["expires_in"]);
            if (!double.IsFinite(number)) throw Invalid("expires_in");
            result["expires_in"] = number;
        }
        foreach (var key in new[] { "scope", "refresh_token", "id_token" })
            if (!Absent(input[key])) result[key] = Required(input[key], key);
        return result;
    }

    /// <summary>【CodingAgent】【动态客户端响应】要求客户端标识，保留注册扩展字段，并补齐重定向地址数组。</summary><param name="value">注册响应。</param><returns>独立的客户端信息。</returns>
    public static JsonObject ClientInformation(JsonNode? value)
    {
        var result = Object(value, "OAuth client registration response");
        Required(result["client_id"], "client_id");
        if (Absent(result["client_secret"])) result.Remove("client_secret"); else Required(result["client_secret"], "client_secret");
        foreach (var key in new[] { "client_id_issued_at", "client_secret_expires_at" })
            if (result[key]?.GetValueKind() != JsonValueKind.Number) result.Remove(key);
        Strings(result, "redirect_uris");
        result["redirect_uris"] ??= new JsonArray();
        return result;
    }

    /// <summary>【CodingAgent】【OAuth 字符串】读取可选字符串，类型不同则返回空值。</summary><param name="value">对象。</param><param name="key">键。</param><returns>字符串或空值。</returns>
    internal static string? Text(JsonObject? value, string key) => value?[key] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
    /// <summary>【CodingAgent】【OAuth 标志】只有布尔 true 才启用协议能力。</summary><param name="value">对象。</param><param name="key">键。</param><returns>是否启用。</returns>
    internal static bool Flag(JsonObject? value, string key) => value?[key] is JsonValue item && item.TryGetValue<bool>(out var flag) && flag;
    /// <summary>【CodingAgent】【OAuth 字符串数组】读取已验证的元数据列表。</summary><param name="value">对象。</param><param name="key">键。</param><returns>字符串序列。</returns>
    internal static string[] List(JsonObject? value, string key) => value?[key] is JsonArray items ? items.Select(item => item!.GetValue<string>()).ToArray() : [];
    /// <summary>【CodingAgent】【OAuth 对象】拒绝数组和标量，复制原始对象以避免修改调用方。</summary><param name="value">输入。</param><param name="name">错误字段。</param><returns>对象副本。</returns>
    private static JsonObject Object(JsonNode? value, string name) => value is JsonObject obj ? obj.DeepClone().AsObject() : throw Invalid(name);
    /// <summary>【CodingAgent】【OAuth 必填文本】只允许非空字符串，保留空白字符串的原始协议语义。</summary><param name="value">输入。</param><param name="name">错误字段。</param><returns>原文本。</returns>
    private static string Required(JsonNode? value, string name) => value is JsonValue item && item.TryGetValue<string>(out var text) && text.Length > 0 ? text : throw Invalid(name);
    /// <summary>【CodingAgent】【OAuth 地址】拒绝相对地址及可执行脚本和内嵌数据协议。</summary><param name="value">输入。</param><param name="name">错误字段。</param>
    private static void Url(JsonNode? value, string name)
    {
        var text = Required(value, name);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is "javascript" or "data" or "vbscript") throw Invalid(name);
    }
    /// <summary>【CodingAgent】【OAuth 可选数组】移除 null，可选数组内只允许字符串。</summary><param name="value">待校验对象。</param><param name="key">字段。</param>
    private static void Strings(JsonObject value, string key)
    {
        if (value[key] is null) { value.Remove(key); return; }
        if (value[key] is not JsonArray items || items.Any(item => item is not JsonValue scalar || !scalar.TryGetValue<string>(out _))) throw Invalid(key);
    }
    /// <summary>【CodingAgent】【OAuth 空字段】null 和空文本视为可选值缺省。</summary><param name="value">输入。</param><returns>是否缺省。</returns>
    private static bool Absent(JsonNode? value) => value is null || value is JsonValue item && item.TryGetValue<string>(out var text) && text.Length == 0;
    /// <summary>【CodingAgent】【OAuth 数字转换】对齐 JSON 值的 JavaScript Number 转换，非有限值由调用方拒绝。</summary><param name="value">JSON 值。</param><returns>数字或 NaN。</returns>
    private static double Number(JsonNode? value)
    {
        if (value is null) return 0;
        if (value is JsonArray array) return Number(JsonValue.Create(string.Join(",", array.Select(ArrayText))));
        if (value is not JsonValue scalar) return double.NaN;
        if (scalar.GetValueKind() == JsonValueKind.Number) return double.Parse(scalar.ToJsonString(), CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        if (!scalar.TryGetValue<string>(out var text)) return double.NaN;
        text = text.Trim().Trim('\uFEFF').Trim();
        if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0' && "xXbBoO".Contains(text[1]))
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, _ => 8 };
            double result = 0;
            foreach (var character in text.AsSpan(2))
            {
                var digit = character is >= '0' and <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
                if (digit < 0 || digit >= radix) return double.NaN;
                result = result * radix + digit;
            }
            return result;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
    /// <summary>【CodingAgent】【OAuth 数组文本】按 JavaScript 数组拼接规则支持 expires_in 的兼容数值输入。</summary><param name="value">数组项。</param><returns>转换文本。</returns>
    private static string ArrayText(JsonNode? value) => value switch
    {
        null => "",
        JsonArray array => string.Join(",", array.Select(ArrayText)),
        JsonObject => "[object Object]",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString()
    };
    /// <summary>【CodingAgent】【OAuth 校验失败】创建不含响应敏感内容的字段错误。</summary><param name="name">字段名称。</param><returns>校验异常。</returns>
    private static InvalidDataException Invalid(string name) => new("Invalid " + name);
}
