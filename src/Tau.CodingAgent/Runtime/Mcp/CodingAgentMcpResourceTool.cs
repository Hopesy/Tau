// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 资源服务器】提供按需连接的可读取资源来源。</summary>
internal sealed record CodingAgentMcpResourceServer(string Name, ICodingAgentMcpRequestExecutor Executor);

/// <summary>【CodingAgent】【MCP 资源工具】支持跨服务器列表、单服务器分页、资源模板和资源读取。</summary>
public sealed class CodingAgentMcpResourceTool : ICodingAgentToolDefinition
{
    private readonly CodingAgentMcpService _service;
    public string Name { get; }
    public string Label => Name;
    public string Exposure { get; }
    public CodingAgentSourceInfo? SourceInfo => new("builtin:mcp/resources", "builtin");
    public JsonElement? Annotations { get; } = Parse("""{"readOnlyHint":true}""");
    public JsonElement ParameterSchema { get; }
    public JsonElement? OutputSchema { get; }
    public string Description => Name switch
    {
        "list_mcp_resources" => "Lists resources provided by MCP servers. Resources allow servers to share data that provides context to language models, such as files, database schemas, or application-specific information. Prefer resources over web search when possible.",
        "list_mcp_resource_templates" => "Lists resource templates provided by MCP servers. Parameterized resource templates allow servers to share data that takes parameters and provides context to language models, such as files, database schemas, or application-specific information. Prefer resource templates over web search when possible.",
        _ => "Read a specific resource from an MCP server given the server name and resource URI."
    };

    /// <summary>【CodingAgent】【资源定义】创建资源工具并使用相关服务器中最宽的暴露策略。</summary>
    /// <param name="service">连接服务。</param><param name="name">三个标准资源工具名之一。</param><param name="exposure">服务器聚合策略。</param>
    internal CodingAgentMcpResourceTool(CodingAgentMcpService service, string name, string exposure)
    {
        _service = service; Name = name; Exposure = exposure == "codemode" ? "deferred" : exposure;
        ParameterSchema = name == "read_mcp_resource" ? Parse("""
            {"type":"object","properties":{"server":{"type":"string","description":"MCP server name exactly as configured. Must match the 'server' field returned by list_mcp_resources."},"uri":{"type":"string","description":"Resource URI to read. Must be one of the URIs returned by list_mcp_resources."}},"required":["server","uri"],"additionalProperties":false}
            """) : Parse("""
            {"type":"object","properties":{"server":{"type":"string","description":"MCP server name. Omit to list every server with resources."},"cursor":{"type":"string","description":"Opaque cursor from a previous call with the same server; omit for the first page."}},"additionalProperties":false}
            """);
        var item = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
        {
            ["server"] = new JsonObject { ["type"] = "string" }, [name == "list_mcp_resource_templates" ? "uriTemplate" : "uri"] = new JsonObject { ["type"] = "string" },
            ["name"] = new JsonObject { ["type"] = "string" }, ["title"] = new JsonObject { ["type"] = "string" }, ["description"] = new JsonObject { ["type"] = "string" }, ["mimeType"] = new JsonObject { ["type"] = "string" }
        }, ["required"] = new JsonArray("server", name == "list_mcp_resource_templates" ? "uriTemplate" : "uri", "name") };
        if (name == "list_mcp_resources") item["properties"]!["size"] = new JsonObject { ["type"] = "number" };
        var key = name == "list_mcp_resource_templates" ? "resourceTemplates" : "resources";
        OutputSchema = name == "read_mcp_resource" ? Parse("""
            {"type":"object","properties":{"server":{"type":"string"},"uri":{"type":"string"},"contents":{"type":"array","items":{"anyOf":[{"type":"object","properties":{"uri":{"type":"string"},"mimeType":{"type":"string"},"text":{"type":"string"}},"required":["uri","text"]},{"type":"object","properties":{"uri":{"type":"string"},"mimeType":{"type":"string"},"blob":{"type":"string"}},"required":["uri","blob"]}]}}},"required":["server","uri","contents"]}
            """) : JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["server"] = new JsonObject { ["type"] = "string" }, [key] = new JsonObject { ["type"] = "array", ["items"] = item },
                ["nextCursor"] = new JsonObject { ["type"] = "string" }, ["errors"] = JsonNode.Parse("""{"type":"array","description":"Servers that could not be listed","items":{"type":"object","properties":{"server":{"type":"string"},"error":{"type":"string"}},"required":["server","error"]}}""")
            }, ["required"] = new JsonArray(key) });
    }

    /// <summary>【CodingAgent】【资源调用】等待服务器发现，转换内容并为程序调用返回原始资源包络。</summary>
    /// <param name="toolCallId">调用标识。</param><param name="args">服务器、游标或 URI。</param><param name="ct">取消信号。</param>
    /// <param name="onUpdate">未使用的进度回调。</param><returns>资源工具结果。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        await _service.WaitForServersAsync(ct).ConfigureAwait(false);
        var server = Argument(args, "server");
        JsonObject payload; var blocks = new JsonArray();
        if (Name == "read_mcp_resource")
        {
            if (string.IsNullOrEmpty(server)) throw new ArgumentException("server must be provided");
            var uri = Argument(args, "uri"); if (string.IsNullOrEmpty(uri)) throw new ArgumentException("uri must be provided");
            var selected = FindServer(server);
            var result = await selected.Executor.ExecuteAsync((client, token) => client.ReadResourceAsync(uri, token), true, ct).ConfigureAwait(false);
            var contents = result.GetProperty("contents").EnumerateArray().ToArray();
            var normalized = new JsonArray();
            foreach (var resource in contents)
            {
                if (contents.Length > 1) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = resource.GetProperty("uri").GetString() + ":" });
                var value = JsonNode.Parse(resource.GetRawText())!.AsObject(); value.Remove("_meta");
                blocks.Add(new JsonObject { ["type"] = "resource", ["resource"] = value.DeepClone() }); normalized.Add(value);
            }
            if (contents.Length == 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = $"Resource {uri} is empty." });
            payload = new() { ["server"] = server, ["uri"] = uri, ["contents"] = normalized };
        }
        else
        {
            payload = await ListAsync(server, Argument(args, "cursor"), Name == "list_mcp_resource_templates", ct).ConfigureAwait(false);
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() });
        }
        var converted = await CodingAgentMcpToolResult.ConvertAsync(server ?? "", Name, JsonSerializer.SerializeToElement(new JsonObject { ["content"] = blocks }), token: ct).ConfigureAwait(false);
        return converted with { StructuredContent = JsonSerializer.SerializeToElement(payload) };
    }

    /// <summary>【CodingAgent】【资源分页聚合】指定服务器时保留游标，否则并行读取所有页并保留单服务器错误。</summary>
    /// <param name="server">服务器过滤。</param><param name="cursor">页游标。</param><param name="templates">模板列表。</param><param name="token">取消信号。</param><returns>列表包络。</returns>
    private async Task<JsonObject> ListAsync(string? server, string? cursor, bool templates, CancellationToken token)
    {
        var key = templates ? "resourceTemplates" : "resources";
        if (!string.IsNullOrEmpty(server))
        {
            var selected = FindServer(server);
            JsonElement page;
            try { page = await selected.Executor.ExecuteAsync((client, signal) => client.ListResourcesPageAsync(templates, cursor, signal), true, token).ConfigureAwait(false); }
            catch (CodingAgentMcpException error) when (templates && error.Code == -32601) { page = Parse("{\"resourceTemplates\":[]}"); }
            var payload = new JsonObject { ["server"] = server, [key] = VisibleItems(server, page.GetProperty(key).EnumerateArray()) };
            if (page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String) payload["nextCursor"] = next.GetString();
            return payload;
        }
        if (!string.IsNullOrEmpty(cursor)) throw new ArgumentException("cursor can only be used when a server is specified");
        var servers = _service.GetResourceServers().OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        var results = await Task.WhenAll(servers.Select(selected => ListAllAsync(selected, templates, token))).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var items = new JsonArray(); var errors = new JsonArray();
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            if (result.Error is not null) errors.Add(new JsonObject { ["server"] = servers[index].Name, ["error"] = result.Error });
            else foreach (var item in VisibleItems(servers[index].Name, result.Items)) items.Add(item?.DeepClone());
        }
        var all = new JsonObject { [key] = items }; if (errors.Count > 0) all["errors"] = errors;
        return all;
    }

    /// <summary>【CodingAgent】【资源全量读取】隔离单服务器错误，不支持模板的方法返回空数组。</summary>
    /// <param name="server">服务器。</param><param name="templates">模板开关。</param><param name="token">取消信号。</param><returns>资源和错误二选一。</returns>
    private static async Task<(IReadOnlyList<JsonElement> Items, string? Error)> ListAllAsync(CodingAgentMcpResourceServer server, bool templates, CancellationToken token)
    {
        try { return (await server.Executor.ExecuteAsync((client, signal) => client.ListResourcesAsync(templates, signal), true, token).ConfigureAwait(false), null); }
        catch (CodingAgentMcpException error) when (templates && error.Code == -32601) { return ([], null); }
        catch (Exception error) { return ([], error.Message); }
    }

    /// <summary>【CodingAgent】【资源可见性】过滤 MCP App 界面资源并移除私有元数据与图标。</summary>
    /// <param name="server">来源服务器。</param><param name="items">资源。</param><returns>带服务器名称的可见资源。</returns>
    private static JsonArray VisibleItems(string server, IEnumerable<JsonElement> items)
    {
        var result = new JsonArray();
        foreach (var item in items)
        {
            if (IsAppResource(item)) continue;
            var value = JsonNode.Parse(item.GetRawText())!.AsObject(); value.Remove("_meta"); value.Remove("icons"); value["server"] = server; result.Add(value);
        }
        return result;
    }

    /// <summary>【CodingAgent】【MCP 界面过滤】资源列表、模板和状态统计共享上游 MCP App 判定规则。</summary>
    /// <param name="item">资源或模板元数据。</param><returns>是否仅供界面宿主显示。</returns>
    internal static bool IsAppResource(JsonElement item)
    {
        var uri = CodingAgentMcpToolResult.String(item, "uri") ?? CodingAgentMcpToolResult.String(item, "uriTemplate") ?? "";
        var mime = CodingAgentMcpToolResult.String(item, "mimeType") ?? "";
        return uri.StartsWith("ui://", StringComparison.Ordinal) || Regex.IsMatch(mime, ";\\s*profile\\s*=\\s*\"?mcp-app\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>【CodingAgent】【资源定位】仅允许访问启用且非 hidden 的资源服务器。</summary><param name="name">配置服务器名。</param><returns>匹配服务器。</returns>
    private CodingAgentMcpResourceServer FindServer(string name)
    {
        var servers = _service.GetResourceServers();
        return servers.FirstOrDefault(server => server.Name == name) ?? throw new InvalidOperationException($"MCP server \"{name}\" has no resources" +
            (servers.Count > 0 ? ". Servers with resources: " + string.Join(", ", servers.Select(server => server.Name)) : ""));
    }

    /// <summary>【CodingAgent】【资源参数】读取字符串参数，省略时返回空值。</summary><param name="args">参数对象。</param><param name="name">字段名。</param><returns>字符串或空值。</returns>
    private static string? Argument(JsonElement args, string name) => CodingAgentMcpToolResult.String(args, name);
    /// <summary>【CodingAgent】【资源 Schema】解析常量 JSON 并取得独立副本。</summary><param name="json">JSON 文本。</param><returns>独立元素。</returns>
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
