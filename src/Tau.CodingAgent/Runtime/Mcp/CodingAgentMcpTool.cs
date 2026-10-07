// 作者：xxx
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 工具】保留服务器元数据，将 Agent 调用代理到当前 MCP 客户端。</summary>
public sealed class CodingAgentMcpTool : ICodingAgentToolDefinition
{
    private readonly string _server;
    private readonly string _tool;
    private readonly Func<CancellationToken, Task<CodingAgentMcpClient>> _getClient;
    private readonly Func<bool>? _readableResources;
    private readonly TimeSpan? _timeout;
    private readonly ICodingAgentMcpRequestExecutor? _executor;
    public string Name { get; }
    public string Label => _server + "/" + _tool;
    public string Description { get; }
    public JsonElement ParameterSchema { get; }
    public JsonElement? OutputSchema { get; }
    public JsonElement? Namespace { get; }
    public JsonElement? Annotations { get; }
    public string Exposure { get; }
    /// <summary>【CodingAgent】【MCP 原始策略】保留 codemode 与 deferred 的差别，工具框架中的两者均采用 deferred。</summary>
    public string McpExposure { get; }
    internal long DeclarationResetVersion { get; init; }
    public CodingAgentSourceInfo? SourceInfo { get; }

    /// <summary>【CodingAgent】【MCP 定义】规范输入 Schema、输出包络和布尔行为提示。</summary>
    /// <param name="server">服务器名。</param><param name="tool">服务器提供的工具元数据。</param><param name="name">安全且唯一的 Agent 工具名。</param>
    /// <param name="exposure">MCP 暴露策略。</param><param name="toolNamespace">命名组元数据。</param><param name="getClient">按需取得连接。</param>
    /// <param name="timeout">单次调用超时。</param><param name="readableResources">当前资源可读取能力。</param><param name="source">配置来源。</param><param name="executor">可选连接恢复执行器。</param>
    public CodingAgentMcpTool(string server, JsonElement tool, string name, string exposure, JsonElement toolNamespace,
        Func<CancellationToken, Task<CodingAgentMcpClient>> getClient, TimeSpan? timeout = null, Func<bool>? readableResources = null, CodingAgentSourceInfo? source = null, ICodingAgentMcpRequestExecutor? executor = null)
    {
        _server = server; _tool = tool.GetProperty("name").GetString()!; Name = name; _getClient = getClient; _timeout = timeout; _readableResources = readableResources; _executor = executor;
        McpExposure = exposure; Exposure = exposure == "codemode" ? "deferred" : exposure; Namespace = toolNamespace.Clone(); SourceInfo = source ?? new("builtin:mcp/" + server, "builtin");
        var annotations = tool.TryGetProperty("annotations", out var hints) ? hints : default;
        var description = CodingAgentMcpToolResult.String(tool, "description")?.Trim();
        Description = !string.IsNullOrEmpty(description) ? description : CodingAgentMcpToolResult.String(tool, "title") ?? CodingAgentMcpToolResult.String(annotations, "title") ?? $"MCP tool {_tool} from server {server}";
        var input = JsonNode.Parse(tool.GetProperty("inputSchema").GetRawText())!.AsObject();
        input["type"] ??= "object";
        if (!input.ContainsKey("properties")) input["properties"] = new JsonObject();
        ParameterSchema = JsonSerializer.SerializeToElement(input);
        var properties = new JsonObject { ["content"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
            ["isError"] = new JsonObject { ["type"] = "boolean" }, ["_meta"] = new JsonObject { ["type"] = "object" } };
        if (tool.TryGetProperty("outputSchema", out var output)) properties["structuredContent"] = JsonNode.Parse(output.GetRawText());
        OutputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("content") });
        var filtered = new JsonObject();
        foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
            if (annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty(hint, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) filtered[hint] = value.GetBoolean();
        Annotations = filtered.Count > 0 ? JsonSerializer.SerializeToElement(filtered) : null;
    }

    /// <summary>【CodingAgent】【MCP 命名】清理名称，并在超长或冲突时附加稳定 SHA-256 摘要。</summary>
    /// <param name="server">服务器名。</param><param name="tool">原始工具名。</param><param name="isTaken">检查其他工具是否占用名称。</param><returns>最多 64 字符的工具名。</returns>
    public static string CreateName(string server, string tool, Func<string, bool>? isTaken = null)
    {
        var name = Regex.Replace($"mcp__{server}__{tool}", "[^A-Za-z0-9_]", "_");
        if (name.Length <= 64 && isTaken?.Invoke(name) != true) return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool)))[..8];
        return name[..Math.Min(name.Length, 55)] + "_" + hash;
    }

    /// <summary>【CodingAgent】【MCP 执行】传递参数、取消和超时，按顺序交付异步进度，再转换最终结果。</summary>
    /// <param name="toolCallId">Agent 调用标识。</param><param name="args">输入参数。</param><param name="ct">取消信号。</param>
    /// <param name="onUpdate">进度回调。</param><returns>保留结构化内容的工具结果。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var gate = new object(); Task progress = Task.CompletedTask;
        try
        {
            Func<CodingAgentMcpClient, CancellationToken, Task<JsonElement>> request = (client, token) => client.CallToolAsync(_tool, args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new() : JsonNode.Parse(args.GetRawText())!.AsObject(),
                update =>
                {
                    if (onUpdate is null) return;
                    var text = CodingAgentMcpToolResult.String(update, "message") ?? "Progress " + update.GetProperty("progress").GetRawText() +
                        (update.TryGetProperty("total", out var total) ? "/" + total.GetRawText() : "");
                    var details = JsonSerializer.SerializeToElement(new JsonObject { ["server"] = _server, ["tool"] = _tool });
                    lock (gate) progress = DeliverProgressAsync(progress, onUpdate, new(text, Details: details));
                }, token, _timeout);
            var result = _executor is null ? await request(await _getClient(ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                : await _executor.ExecuteAsync(request, false, ct).ConfigureAwait(false);
            return await CodingAgentMcpToolResult.ConvertAsync(_server, _tool, result, _readableResources?.Invoke() == true, token: ct).ConfigureAwait(false);
        }
        finally { Task pending; lock (gate) pending = progress; await pending.ConfigureAwait(false); }
    }

    /// <summary>【CodingAgent】【MCP 进度】等待前一条进度完成，防止 UI 更新乱序。</summary>
    /// <param name="previous">前一条任务。</param><param name="callback">宿主回调。</param><param name="update">当前进度。</param><returns>完成交付的任务。</returns>
    private static async Task DeliverProgressAsync(Task previous, Func<ToolUpdate, Task> callback, ToolUpdate update)
    { await previous.ConfigureAwait(false); await callback(update).ConfigureAwait(false); }
}
