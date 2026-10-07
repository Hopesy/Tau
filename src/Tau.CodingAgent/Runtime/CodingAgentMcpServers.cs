// 作者：xxx
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【MCP 配置】校验服务器配置、工具命名空间及访问策略。</summary>
public static class CodingAgentMcpServers
{
    private static readonly string[] Exposures = ["codemode", "deferred", "direct", "hidden"];

    /// <summary>【CodingAgent】【MCP 命名】构造服务器的工具命名空间。</summary>
    /// <param name="server">服务器名称。</param><returns>规范命名空间。</returns>
    public static string Namespace(string server) => "mcp__" + server.Replace('-', '_');

    /// <summary>【CodingAgent】【MCP 策略】精确工具名优先，其后按配置顺序匹配星号模式。</summary>
    /// <param name="config">已校验的服务器配置。</param><param name="toolName">服务器原始工具名。</param><returns>实际访问策略。</returns>
    public static string GetToolExposure(JsonObject config, string toolName)
    {
        if (config["toolExposure"] is JsonObject overrides)
        {
            if (overrides[toolName] is { } exact) return exact.GetValue<string>();
            foreach (var pair in overrides)
                if (pair.Key.Contains('*') && Regex.IsMatch(toolName, "\\A" + Regex.Escape(pair.Key).Replace("\\*", ".*") + "\\z", RegexOptions.CultureInvariant))
                    return pair.Value!.GetValue<string>();
        }
        return config["exposure"]?.GetValue<string>() ?? "codemode";
    }

    /// <summary>【CodingAgent】【MCP 校验】返回独立配置副本并规范化旧访问策略，不执行命令或解析凭据。</summary>
    /// <param name="name">服务器名称。</param><param name="raw">原始配置。</param><returns>已校验的配置副本。</returns>
    public static JsonObject Validate(string name, JsonNode? raw)
    {
        if (!Regex.IsMatch(name, "\\A[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException($"invalid server name \"{name}\" (use letters, digits, \"_\" and \"-\")");
        if (raw is not JsonObject) throw Invalid(name, "must be an object");
        var config = (JsonObject)JsonNode.Parse(raw.ToJsonString())!;
        // 1. 【CodingAgent】【MCP 策略】只规范化别名，不改变未知扩展字段
        if (config.ContainsKey("exposure")) config["exposure"] = NormalizeExposure(name, config["exposure"], "exposure");
        if (config.ContainsKey("toolExposure"))
        {
            if (config["toolExposure"] is not JsonObject overrides) throw Invalid(name, "toolExposure must map tool names to exposures");
            foreach (var pair in overrides.ToArray()) overrides[pair.Key] = NormalizeExposure(name, pair.Value, $"toolExposure \"{pair.Key}\"");
        }
        if (config.ContainsKey("enabled") && config["enabled"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw Invalid(name, "enabled must be a boolean");
        RequireOptionalString(name, config, "description");
        if (config.ContainsKey("timeout") && (config["timeout"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number ||
            config["timeout"]!.GetValue<double>() is var seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)))
            throw Invalid(name, "timeout must be a positive finite number of seconds within the supported duration range");
        var type = ReadString(config["type"]);
        if (type == "sse") throw Invalid(name, "legacy SSE transport is not supported; use the streamable HTTP URL");
        // 2. 【CodingAgent】【MCP 传输】HTTP 与 stdio 各自校验参数，提供方凭据只允许安全目标
        if (ReadString(config["url"]) is { } url && (!config.ContainsKey("type") || type is "http" or "streamable-http"))
        {
            if (!TryHttpUri(url, out var uri)) throw Invalid(name, "url must be an http or https URL");
            RequireStringMap(name, config, "headers");
            if (config.ContainsKey("oauth")) ValidateOAuth(name, config["oauth"]);
            if (config.ContainsKey("auth"))
            {
                if (config["auth"] is not JsonObject auth || string.IsNullOrEmpty(ReadString(auth["provider"])))
                    throw Invalid(name, "auth.provider must be a provider name");
                if (uri!.Scheme != "https" && !IsLoopback(uri)) throw Invalid(name, "auth requires an https URL, or http on localhost, 127.0.0.1, or [::1]");
            }
            return config;
        }
        if (ReadString(config["command"]) is not null && (!config.ContainsKey("type") || type == "stdio"))
        {
            if (config.ContainsKey("args") && (config["args"] is not JsonArray args || args.Any(item => ReadString(item) is null)))
                throw Invalid(name, "args must be an array of strings");
            RequireStringMap(name, config, "env");
            RequireOptionalString(name, config, "cwd");
            return config;
        }
        throw Invalid(name, "needs either \"command\" (stdio) or \"url\" (streamable HTTP)");
    }

    /// <summary>【CodingAgent】【MCP 回调】判断 OAuth 回调是否为无查询和片段的 HTTP 回环地址。</summary>
    /// <param name="value">回调地址。</param><returns>地址有效时返回真。</returns>
    public static bool IsLoopbackRedirectUri(string value) => TryHttpUri(value, out var uri) && uri!.Scheme == "http" &&
        IsLoopback(uri) && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>【CodingAgent】【MCP OAuth】校验客户端、回调、CIMD 及授权元数据配置。</summary>
    /// <param name="name">服务器名称。</param><param name="raw">OAuth 配置。</param>
    private static void ValidateOAuth(string name, JsonNode? raw)
    {
        if (raw is not JsonObject oauth) throw Invalid(name, "oauth must be an object");
        foreach (var field in new[] { "clientId", "clientSecret", "scope" }) RequireOptionalString(name, oauth, field, "oauth.");
        double? port = null;
        if (oauth.ContainsKey("callbackPort"))
        {
            if (oauth["callbackPort"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number ||
                (port = oauth["callbackPort"]!.GetValue<double>()) < 1 || port > 65535 || port != Math.Floor(port.Value))
                throw Invalid(name, "oauth.callbackPort must be a port number");
        }
        Uri? callback = null;
        if (oauth.ContainsKey("callbackUrl"))
        {
            if (ReadString(oauth["callbackUrl"]) is not { } url || !IsLoopbackRedirectUri(url))
                throw Invalid(name, "oauth.callbackUrl must be an http URI on localhost, 127.0.0.1, or [::1] without query or fragment");
            callback = new(url);
            if (!callback.IsDefaultPort && port is not null && callback.Port != port)
                throw Invalid(name, "oauth.callbackUrl and oauth.callbackPort name different ports");
        }
        if (oauth.ContainsKey("clientName") && string.IsNullOrWhiteSpace(ReadString(oauth["clientName"])))
            throw Invalid(name, "oauth.clientName must be a non-empty string");
        if (oauth.ContainsKey("clientRegistration") && ReadString(oauth["clientRegistration"]) != "dcr")
        {
            if (ReadString(oauth["clientRegistration"]) != "cimd") throw Invalid(name, "oauth.clientRegistration must be \"dcr\" or \"cimd\"");
            if (oauth.ContainsKey("clientId") || oauth.ContainsKey("clientName"))
                throw Invalid(name, "oauth.clientRegistration \"cimd\" cannot be combined with oauth.clientId or oauth.clientName");
            if (callback is not null && (callback.Host.Trim('[', ']') == "::1" || callback.AbsolutePath != "/callback"))
                throw Invalid(name, "oauth.clientRegistration \"cimd\" requires oauth.callbackUrl on localhost or 127.0.0.1 with path /callback");
        }
        if (oauth.ContainsKey("authServerMetadataUrl") && (ReadString(oauth["authServerMetadataUrl"]) is not { } metadata ||
            !TryHttpUri(metadata, out var metadataUri) || metadataUri!.Scheme != "https" && !IsLoopback(metadataUri)))
            throw Invalid(name, "oauth.authServerMetadataUrl must be an https URL, or http on localhost, 127.0.0.1, or [::1]");
    }

    /// <summary>【CodingAgent】【MCP 策略】转换旧别名并拒绝未知策略。</summary>
    /// <param name="name">服务器名称。</param><param name="node">策略值。</param><param name="field">诊断字段。</param><returns>规范策略。</returns>
    private static string NormalizeExposure(string name, JsonNode? node, string field)
    {
        var value = ReadString(node);
        if (value == "codemode-deferred") value = "codemode";
        if (value is null || !Exposures.Contains(value, StringComparer.Ordinal))
            throw Invalid(name, field + " must be one of \"codemode\", \"deferred\", \"direct\", \"hidden\"");
        return value;
    }

    /// <summary>【CodingAgent】【MCP 字符串】只读取 JSON 字符串。</summary>
    /// <param name="node">JSON 值。</param><returns>字符串或空值。</returns>
    internal static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>【CodingAgent】【MCP 字段】校验可选字符串字段。</summary>
    /// <param name="name">服务器名称。</param><param name="value">配置对象。</param><param name="field">字段。</param><param name="prefix">诊断前缀。</param>
    private static void RequireOptionalString(string name, JsonObject value, string field, string prefix = "")
    {
        if (value.ContainsKey(field) && ReadString(value[field]) is null) throw Invalid(name, prefix + field + " must be a string");
    }

    /// <summary>【CodingAgent】【MCP 字典】校验环境或请求头映射。</summary>
    /// <param name="name">服务器名称。</param><param name="value">配置对象。</param><param name="field">字段。</param>
    private static void RequireStringMap(string name, JsonObject value, string field)
    {
        if (value.ContainsKey(field) && (value[field] is not JsonObject map || map.Any(pair => ReadString(pair.Value) is null)))
            throw Invalid(name, field + " must map names to strings");
    }

    /// <summary>【CodingAgent】【MCP 地址】解析绝对 HTTP 或 HTTPS 地址。</summary>
    /// <param name="value">地址文本。</param><param name="uri">解析结果。</param><returns>是否有效。</returns>
    private static bool TryHttpUri(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

    /// <summary>【CodingAgent】【MCP 回环】仅接受上游列出的三个回环主机。</summary>
    /// <param name="uri">已解析地址。</param><returns>是否匹配。</returns>
    private static bool IsLoopback(Uri uri) => uri.Host.Trim('[', ']') is "localhost" or "127.0.0.1" or "::1";

    /// <summary>【CodingAgent】【MCP 诊断】建立包含服务器名称的验证异常。</summary>
    /// <param name="name">服务器名称。</param><param name="reason">失败原因。</param><returns>验证异常。</returns>
    private static ArgumentException Invalid(string name, string reason) => new($"server \"{name}\": {reason}");
}
