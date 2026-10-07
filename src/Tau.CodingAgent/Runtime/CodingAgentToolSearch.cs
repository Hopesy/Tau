// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【工具搜索】可搜索的工具文档。</summary>
public sealed record CodingAgentToolSearchDocument(string Name, string Text);
/// <summary>【CodingAgent】【搜索命中】保留匹配名称及 BM25 分数。</summary>
public sealed record CodingAgentToolSearchMatch(string Name, double Score);

/// <summary>【CodingAgent】【BM25 搜索】与上游一致的词拆分、词干及工具元数据排名。</summary>
public static class CodingAgentToolSearch
{
    private static readonly HashSet<string> StopWords = new("a an and are as at be by for from in is it of on or that the this to with".Split(' '), StringComparer.Ordinal);

    /// <summary>【CodingAgent】【搜索分词】按驼峰和非字母数字切分，移除停用词并处理常见复数。</summary>
    /// <param name="text">查询或文档。</param><returns>保留重复次数的词列表。</returns>
    public static IReadOnlyList<string> Tokenize(string text) => Regex.Split(Regex.Replace(Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2"), "([A-Z]+)([A-Z][a-z])", "$1 $2").ToLowerInvariant(), "[^a-z0-9]+")
        .Where(term => term.Length > 0 && !StopWords.Contains(term)).Select(Stem).ToArray();

    /// <summary>【CodingAgent】【词干处理】按上游轻量规则把复数还原为搜索词。</summary><param name="term">小写词。</param><returns>词干。</returns>
    private static string Stem(string term) => term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal) ? term[..^3] + "y" :
        term.Length > 4 && Regex.IsMatch(term, "(ches|shes|sses|xes|zes)$") ? term[..^2] :
        term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal) ? term[..^1] : term;

    /// <summary>【CodingAgent】【搜索文档】合并工具名称、说明、参数属性和命名组指令。</summary><param name="tool">工具定义。</param><returns>搜索文档。</returns>
    public static CodingAgentToolSearchDocument CreateDocument(IAgentTool tool) => CreateDocument(tool, (tool as ICodingAgentToolDefinition)?.Namespace);

    /// <summary>【CodingAgent】【宿主搜索文档】采用宿主指定的命名组参与索引，空值不回退到工具元数据。</summary>
    /// <param name="tool">工具定义。</param><param name="toolNamespace">宿主解析的命名组。</param><returns>搜索文档。</returns>
    public static CodingAgentToolSearchDocument CreateDocument(IAgentTool tool, JsonElement? toolNamespace)
    {
        var parts = new List<string> { tool.Name, tool.Name.Replace('_', ' '), tool.Description };
        SchemaText(tool.ParameterSchema, parts);
        if (toolNamespace is { ValueKind: JsonValueKind.Object } ns)
            foreach (var key in new[] { "name", "description", "instructions" })
                if (ns.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) parts.Add(value.GetString()!);
        return new(tool.Name, string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part))));
    }

    /// <summary>【CodingAgent】【参数索引】递归提取 Schema 的说明、对象属性、数组项与组合分支。</summary>
    /// <param name="schema">JSON Schema。</param><param name="parts">输出文本片段。</param>
    private static void SchemaText(JsonElement schema, List<string> parts)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String) parts.Add(description.GetString()!);
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject()) { parts.Add(property.Name); SchemaText(property.Value, parts); }
        if (schema.TryGetProperty("items", out var items)) SchemaText(items, parts);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (schema.TryGetProperty(key, out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray()) SchemaText(variant, parts);
    }

    /// <summary>【CodingAgent】【搜索排序】计算 Okapi BM25，分数相同保留目录顺序，忽略零分项。</summary>
    /// <param name="query">查询。</param><param name="documents">工具文档。</param><param name="limit">数量上限。</param>
    /// <param name="k1">词频饱和参数。</param><param name="b">长度归一参数。</param><returns>按分数排列的命中。</returns>
    public static IReadOnlyList<CodingAgentToolSearchMatch> Rank(string query, IReadOnlyList<CodingAgentToolSearchDocument> documents, int limit, double k1 = 1.2, double b = 0.75)
    {
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0 || documents.Count == 0 || limit <= 0) return [];
        var counts = documents.Select(document => Tokenize(document.Text).GroupBy(term => term, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)).ToArray();
        var lengths = counts.Select(count => count.Values.Sum()).ToArray();
        var average = lengths.Average(); if (average == 0) average = 1;
        var idf = terms.ToDictionary(term => term, term => { var frequency = counts.Count(count => count.ContainsKey(term)); return Math.Log(1 + (documents.Count - frequency + 0.5) / (frequency + 0.5)); }, StringComparer.Ordinal);
        return documents.Select((document, index) => new CodingAgentToolSearchMatch(document.Name, terms.Sum(term =>
        {
            if (!counts[index].TryGetValue(term, out var count)) return 0;
            var norm = k1 * (1 - b + b * lengths[index] / average);
            return idf[term] * count * (k1 + 1) / (count + norm);
        }))).Where(match => match.Score > 0).OrderByDescending(match => match.Score).Take(limit).ToArray();
    }
}

/// <summary>【CodingAgent】【工具发现】搜索尚未声明的工具，并通过会话活动集合让下一次模型调用使用它们。</summary>
public sealed class CodingAgentToolSearchTool : ICodingAgentToolDefinition
{
    private readonly RuntimeCodingAgentRunner _runner;
    private readonly Func<CancellationToken, Task>? _wait;
    public string Name => "tool_search";
    public string Label => Name;
    public string Exposure => "model-only";
    public bool? DefaultActive => false;
    public CodingAgentSourceInfo? SourceInfo => new("builtin:tool_search", "builtin");
    public string Description => "# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\nSome of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`tool_search`) to search for the required tools. For MCP tool discovery, always use `tool_search`.";
    public string? PromptSnippet => "Search for tools that are not loaded yet and load the matches";
    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search query for deferred tools." },
            ["limit"] = new JsonObject { ["type"] = "number", ["description"] = "Maximum number of tools to return. Defaults to 8." } }, ["required"] = new JsonArray("query")
    });

    /// <summary>【CodingAgent】【工具发现创建】绑定会话并可选等待后台工具发现完成。</summary>
    /// <param name="runner">目标会话。</param><param name="wait">查询前等待回调。</param>
    public CodingAgentToolSearchTool(RuntimeCodingAgentRunner runner, Func<CancellationToken, Task>? wait = null) { _runner = runner; _wait = wait; }

    /// <summary>【CodingAgent】【搜索并加载】只搜索未激活的 codemode/deferred 工具，不暴露 hidden、direct 或 model-only。</summary>
    /// <param name="toolCallId">调用标识。</param><param name="args">query 和可选 limit。</param><param name="ct">取消信号。</param>
    /// <param name="onUpdate">未使用的进度回调。</param><returns>加载工具名及首行说明。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var query = args.TryGetProperty("query", out var input) && input.ValueKind == JsonValueKind.String ? input.GetString() : null;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query must not be empty");
        var limit = 8;
        if (args.TryGetProperty("limit", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetDouble(out var value) || !double.IsFinite(value) || value <= 0 || value != Math.Truncate(value)) throw new ArgumentException("limit must be a positive integer");
            limit = (int)Math.Min(value, int.MaxValue);
        }
        if (_wait is not null) await _wait(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var active = _runner.GetActiveToolNames();
        var candidates = _runner.GetRegisteredTools().Where(tool => RuntimeCodingAgentRunner.GetToolExposure(tool) is "codemode" or "deferred" && !active.Contains(tool.Name, StringComparer.Ordinal)).ToArray();
        var matches = CodingAgentToolSearch.Rank(query, candidates.Select(CodingAgentToolSearch.CreateDocument).ToArray(), limit);
        if (matches.Count > 0) _runner.SetActiveTools([.. active, .. matches.Select(match => match.Name)]);
        var text = matches.Count == 0 ? "No matching tools found." : $"Loaded {matches.Count} tool{(matches.Count == 1 ? "" : "s")}. They are available from your next call:\n" +
            string.Join('\n', matches.Select(match => "- " + match.Name + ": " + candidates.First(tool => tool.Name == match.Name).Description.Trim().Split('\n')[0].TrimEnd('\r')));
        return new([new TextContent(text)], Details: JsonSerializer.SerializeToElement(new JsonObject { ["loaded"] = new JsonArray(matches.Select(match => (JsonNode?)JsonValue.Create(match.Name)).ToArray()) }));
    }
}
