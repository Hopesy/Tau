// 作者：xxx
using System.Text.Json;
using System.Text.RegularExpressions;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCodeModeTool
{
    /// <summary>【CodingAgent】【脚本目录】按命名组轮流选取最短声明，控制提示预算且始终排除延迟工具。</summary>
    /// <param name="tools">需要呈现的工具。</param><param name="budget">声明部分的估算 token 上限。</param><returns>完整模型说明。</returns>
    private string DescribeCatalog(IReadOnlyList<IAgentTool> tools, double budget) => CreateDescription(tools, new()
    {
        Models = ModelsEnabled, InlineBudget = budget,
        Namespaces = tools.Select(tool => (tool.Name, Namespace: ResolveNamespace(tool))).Where(entry => entry.Namespace is not null)
            .ToDictionary(entry => entry.Name, entry => entry.Namespace!.Value, StringComparer.Ordinal)
    });

    /// <summary>【CodingAgent】【公开脚本目录】无需启动会话即可生成说明，延迟工具完全排除，未设置预算时展示全部声明。</summary>
    /// <param name="tools">候选工具。</param><param name="options">模型声明、命名组、延迟工具及预算。</param><returns>完整脚本工具说明。</returns>
    public static string CreateDescription(IReadOnlyList<IAgentTool> tools, CodingAgentCodeModeDescriptionOptions? options = null)
    {
        options ??= new();
        if (options.InlineBudget is { } requested && (!double.IsFinite(requested) || requested < 0))
            throw new ArgumentOutOfRangeException(nameof(options), "Codemode inline budget must be finite and non-negative");
        var budget = options.InlineBudget ?? double.PositiveInfinity;
        tools = GetCallableTools(tools).Where(tool => options.Deferred?.Contains(tool.Name) != true).ToArray();
        var groups = tools.GroupBy(tool => NamespaceName(NamespaceFor(tool, options)) ?? "", StringComparer.Ordinal)
            .OrderBy(group => group.Key.Length == 0 ? 0 : 1).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Select(tool => new CatalogEntry(tool, ToolSection(tool))).ToArray()).ToArray();
        var queues = groups.Select(group => new Queue<CatalogEntry>(group.OrderBy(entry => entry.Cost))).ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        // 1. 【CodingAgent】【目录公平分配】每轮每组最多加入一个工具，预算不足的组退出后其余组继续
        while (queues.Count > 0)
        {
            for (var index = 0; index < queues.Count;)
            {
                var queue = queues[index];
                if (queue.Count == 0 || queue.Peek().Cost > budget) { queues.RemoveAt(index); continue; }
                var next = queue.Dequeue(); budget -= next.Cost; shown.Add(next.Tool.Name); index++;
            }
        }
        var sections = new List<string> { BaseDescription(options.Models) };
        if (tools.Any(tool => shown.Contains(tool.Name) && CodingAgentCodeModeDeclarations.OutputType(tool.OutputSchema).StartsWith("CallToolResult", StringComparison.Ordinal)))
            sections.Add("Shared MCP Types:\n```ts\n" + CodingAgentCodeModeDeclarations.McpTypeScriptPreamble + "\n```");
        if (tools.Count == 0) return string.Join("\n\n", sections);
        sections.Add("Nested tools:");
        foreach (var group in groups)
        {
            var visible = group.Where(entry => shown.Contains(entry.Tool.Name)).ToArray();
            if (NamespaceName(NamespaceFor(group[0].Tool, options)) is { } name)
            {
                var description = NamespaceFor(group[0].Tool, options) is { } ns && ns.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()?.Trim() : null;
                var listing = visible.Length == group.Length ? "" : visible.Length == 0 ? " (tools not listed)" : " (some tools not listed)";
                sections.Add("## " + name + listing + (string.IsNullOrEmpty(description) ? "" : "\n" + description));
            }
            sections.AddRange(visible.Select(entry => entry.Section));
        }
        return string.Join("\n\n", sections);
    }

    /// <summary>【CodingAgent】【公开脚本声明】返回元数据副本，未声明输出结构的工具在脚本中返回字符串。</summary>
    /// <param name="tool">原始工具。</param><returns>不包含执行委托的脚本声明。</returns>
    public static CodingAgentCodeModeDeclaration ToDeclaration(IAgentTool tool)
    {
        using var text = JsonDocument.Parse("{\"type\":\"string\"}");
        return new(tool.Name, tool.Description, tool.ParameterSchema.Clone(), tool.OutputSchema?.Clone() ?? text.RootElement.Clone());
    }

    /// <summary>【CodingAgent】【公开可调用集合】移除 codemode 自身，暴露策略由调用方的工具装载逻辑决定。</summary>
    /// <param name="tools">已选候选集合。</param><returns>不包含脚本工具的稳定列表。</returns>
    public static IReadOnlyList<IAgentTool> GetCallableTools(IEnumerable<IAgentTool> tools) => tools.Where(tool => tool.Name != "codemode").ToArray();

    /// <summary>【CodingAgent】【目录命名组】只采用显式提供的有效命名组对象。</summary>
    /// <param name="tool">工具。</param><param name="options">公开目录选项。</param><returns>命名组对象或空值。</returns>
    private static JsonElement? NamespaceFor(IAgentTool tool, CodingAgentCodeModeDescriptionOptions options) =>
        options.Namespaces?.TryGetValue(tool.Name, out var value) == true && value.ValueKind == JsonValueKind.Object ? value : null;

    /// <summary>【CodingAgent】【工具标题】同时标注规范 JavaScript 标识和需要转义的原始工具名。</summary>
    /// <param name="tool">工具定义。</param><returns>标题和完整声明。</returns>
    private static string ToolSection(IAgentTool tool)
    {
        var id = CodingAgentCodeModeSandbox.Identifier(tool.Name);
        return $"### `{id}`" + (id == tool.Name ? "" : $" (`{tool.Name}`)") + "\n" + CodingAgentCodeModeDeclarations.Sample(tool).Trim();
    }

    /// <summary>【CodingAgent】【返回值简介】对象只列出字段，其余类型压缩到单行，文本工具明确返回字符串。</summary>
    /// <param name="schema">可选输出 Schema。</param><returns>直接声明中的脚本调用返回值说明。</returns>
    private static string DescribeOutput(JsonElement? schema)
    {
        var type = CodingAgentCodeModeDeclarations.OutputType(schema);
        if (type == "string") return "a string";
        if (!type.StartsWith("CallToolResult", StringComparison.Ordinal) && schema is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "object" &&
            obj.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var required = obj.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            return "`{ " + string.Join(", ", properties.EnumerateObject().Select(property => property.Name + (required.Contains(property.Name) ? "" : "?"))) + " }`";
        }
        return "`" + Regex.Replace(type, @"\s+", " ") + "`";
    }

    /// <summary>【CodingAgent】【命名组名称】读取有效对象中的名称。</summary><param name="value">命名组对象。</param><returns>名称或空值。</returns>
    private static string? NamespaceName(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } ns &&
        ns.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;

    /// <summary>【CodingAgent】【目录条目】缓存渲染文本和估算成本，避免多轮重新展开 Schema。</summary><param name="Tool">工具。</param><param name="Section">章节。</param>
    private sealed record CatalogEntry(IAgentTool Tool, string Section) { internal int Cost => (Section.Length + 3) / 4; }
}
