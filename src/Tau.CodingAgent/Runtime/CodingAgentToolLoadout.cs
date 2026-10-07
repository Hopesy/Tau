// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【工具组合】保留原始描述的声明、可调用工具与完整注册表。</summary>
public sealed record CodingAgentToolLoadout(IReadOnlyList<IAgentTool> Declared, IReadOnlyList<IAgentTool> Callable, IReadOnlyList<IAgentTool> Registered)
{
    /// <summary>【CodingAgent】【工具组合】读取工具访问策略，未知工具默认为 direct。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>访问策略。</returns>
    public string GetExposure(string name) => Registered.FirstOrDefault(tool => tool.Name == name) is { } tool
        ? RuntimeCodingAgentRunner.GetToolExposure(tool) : "direct";

    /// <summary>【CodingAgent】【工具组合】读取工具命名组。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>命名组，未定义时为空。</returns>
    public JsonElement? GetNamespace(string name) => (Registered.FirstOrDefault(tool => tool.Name == name) as ICodingAgentToolDefinition)?.Namespace;
}

/// <summary>【CodingAgent】【工具组合】仅改变模型呈现，不移除活动工具或历史声明。</summary>
public sealed record CodingAgentToolLoadoutChanges
{
    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> HiddenDeclarations { get; init; } = [];
}

public sealed partial class RuntimeCodingAgentRunner
{
    private HashSet<string> _hiddenDeclarations = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【工具组合】记录 Node 同步组合操作中被隔离的钩子错误。</summary>
    /// <param name="changes">包含可选错误列表的组合结果。</param>
    internal void ReportLoadoutErrors(JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object || !changes.TryGetProperty("errors", out var errors)) return;
        foreach (var error in errors.EnumerateArray())
            LogExtensionEventError(new(error.GetProperty("filePath").GetString()!, "extension", "javascript", "prepare_loadout",
                error.GetProperty("error").GetString()!), CreateRunLogContext());
    }

    /// <summary>【CodingAgent】【工具组合】按活动顺序运行准备钩子，合并说明和隐藏集合，隔离单个钩子失败。</summary>
    /// <param name="names">所需活动工具名称。</param>
    /// <param name="prepared">Node 同步操作已计算的结果，防止输出管道重入等待。</param>
    /// <returns>保留执行能力的工具列表。</returns>
    private IReadOnlyList<IAgentTool> ApplyToolLoadout(IReadOnlyList<string> names, CodingAgentToolLoadoutChanges? prepared = null)
    {
        var tools = names.Select(name => CodingAgentToolNames.ResolveRegistered(name, _registeredTools)).Distinct(StringComparer.Ordinal).Select(name => _registeredTools.FirstOrDefault(tool => tool.Name == name))
            .OfType<IAgentTool>().Where(tool => GetToolExposure(tool) != "hidden").ToArray();
        var active = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var loadout = new CodingAgentToolLoadout(tools, _registeredTools.Where(tool => GetToolExposure(tool) is "codemode" or "deferred" ||
            GetToolExposure(tool) == "direct" && active.Contains(tool.Name)).ToArray(), _registeredTools);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        // 1. 【CodingAgent】【工具组合】每个钩子看到相同原始定义，后面的描述覆盖前面的描述
        foreach (var tool in tools)
        {
            // 2. 【CodingAgent】【内置组合】Node 已准备扩展钩子时仍计算宿主脚本工具的呈现规则，该钩子不回调 Node
            if (prepared is not null && tool is not CodingAgentCodeModeTool) continue;
            try
            {
                if ((tool as ICodingAgentToolDefinition)?.PrepareLoadout(loadout) is not { } changes) continue;
                foreach (var pair in changes.Descriptions) descriptions[pair.Key] = pair.Value;
                hidden.UnionWith(changes.HiddenDeclarations);
            }
            catch (Exception error)
            {
                LogExtensionEventError(new(tool is CodingAgentExtensionToolAdapter adapter ? adapter.FilePath : "<sdk:" + tool.Name + ">",
                    "extension", "tool", "prepare_loadout", error.Message), CreateRunLogContext());
            }
        }
        if (prepared is not null)
        {
            foreach (var pair in prepared.Descriptions) descriptions[pair.Key] = pair.Value;
            hidden.UnionWith(prepared.HiddenDeclarations);
        }
        _hiddenDeclarations = hidden;
        return tools.Select(tool => descriptions.TryGetValue(tool.Name, out var description)
            ? (IAgentTool)new LoadoutTool(tool, description) : tool).ToArray();
    }

    /// <summary>【CodingAgent】【工具呈现】在上下文钩子之后过滤所有系统声明，原始历史保持可恢复。</summary>
    /// <param name="messages">已经转换的请求消息。</param>
    /// <returns>供模型读取的独立消息列表。</returns>
    private IReadOnlyList<ChatMessage> ProjectHiddenDeclarations(IReadOnlyList<ChatMessage> messages)
    {
        var hidden = _hiddenDeclarations;
        if (hidden.Count == 0) return messages;
        return messages.Select(message => message is SystemMessage system ? system with
        {
            ToolsAdded = system.ToolsAdded?.Where(tool => !hidden.Contains(tool.Name)).ToArray() is { Length: > 0 } added ? added : null,
            ToolsRemoved = system.ToolsRemoved?.Where(tool => !hidden.Contains(tool.Name)).ToArray() is { Length: > 0 } removed ? removed : null
        } : message).ToArray();
    }

    /// <summary>【CodingAgent】【工具提示】去除隐藏工具的提示简介，保留选择集与独立使用规则。</summary>
    /// <param name="options">本轮提示选项。</param>
    /// <returns>与模型声明一致的提示选项。</returns>
    private CodingAgentSystemPromptOptions FilterHiddenToolSnippets(CodingAgentSystemPromptOptions options) => options with
    {
        ToolSnippets = options.ToolSnippets.Where(pair => !_hiddenDeclarations.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    /// <summary>【CodingAgent】【工具呈现】只覆盖说明的委托包装器，保留参数准备、元数据和执行语义。</summary>
    private sealed class LoadoutTool(IAgentTool inner, string description) : ICodingAgentToolDefinition
    {
        public string Name => inner.Name;
        public string Label => inner.Label;
        public string Description => description;
        public JsonElement ParameterSchema => inner.ParameterSchema;
        public JsonElement? OutputSchema => inner.OutputSchema;
        public string? PromptSnippet => inner.PromptSnippet;
        public IReadOnlyList<string> PromptGuidelines => inner.PromptGuidelines;
        public ConstrainedSamplingConfig? ConstrainedSampling => inner.ConstrainedSampling;
        public ToolExecutionMode ExecutionMode => inner.ExecutionMode;
        public string Exposure => GetToolExposure(inner);
        public bool? DefaultActive => (inner as ICodingAgentToolDefinition)?.DefaultActive;
        public JsonElement? Namespace => (inner as ICodingAgentToolDefinition)?.Namespace;
        public JsonElement? Annotations => (inner as ICodingAgentToolDefinition)?.Annotations;
        public CodingAgentSourceInfo? SourceInfo => CodingAgentSourceInfo.ForTool(inner);
        /// <summary>【CodingAgent】【工具参数】委托给原始工具进行参数准备。</summary>
        /// <param name="rawArgs">原始参数。</param><param name="ct">取消信号。</param>
        /// <returns>准备后的参数。</returns>
        public ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default) => inner.PrepareArgumentsAsync(rawArgs, ct);
        /// <summary>【CodingAgent】【工具执行】委托原始工具执行，保留回调和结果。</summary>
        /// <param name="toolCallId">调用标识。</param><param name="args">结构化参数。</param>
        /// <param name="ct">取消信号。</param><param name="onUpdate">进度回调。</param>
        /// <returns>工具执行结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) =>
            inner.ExecuteAsync(toolCallId, args, ct, onUpdate);
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【工具组合】在持久扩展实例上执行同步准备钩子。</summary>
    /// <param name="filePath">扩展文件。</param><param name="name">工具名称。</param>
    /// <param name="loadout">完整工具组合。</param><returns>可选模型呈现变更。</returns>
    internal CodingAgentToolLoadoutChanges? PrepareToolLoadout(string filePath, string name, CodingAgentToolLoadout loadout)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            declared = loadout.Declared.Select(tool => tool.Name), callable = loadout.Callable.Select(tool => tool.Name),
            registered = loadout.Registered.Select(SerializeLoadoutTool)
        });
        var execution = Execute(BuildPayload("prepareToolLoadout", filePath, _cwd, toolName: name, toolArgs: args));
        if (!execution.Success) throw new InvalidOperationException(execution.Error);
        using var document = JsonDocument.Parse(execution.ResultJson);
        var root = document.RootElement;
        if (!ReadBool(root, "ok")) throw new InvalidOperationException(ReadString(root, "error"));
        return root.TryGetProperty("changes", out var changes) ? ReadLoadoutChanges(changes) : null;
    }

    /// <summary>【CodingAgent】【工具组合】复制钩子所需元数据，不序列化 .NET 执行委托。</summary>
    /// <param name="tool">原始工具。</param><returns>JavaScript 工具信息。</returns>
    private static object SerializeLoadoutTool(IAgentTool tool) => new
    {
        name = tool.Name, label = tool.Label, description = tool.Description, parameters = tool.ParameterSchema,
        outputSchema = tool.OutputSchema, exposure = RuntimeCodingAgentRunner.GetToolExposure(tool),
        @namespace = (tool as ICodingAgentToolDefinition)?.Namespace, annotations = (tool as ICodingAgentToolDefinition)?.Annotations,
        executionMode = tool.ExecutionMode.ToString().ToLowerInvariant(), constrainedSampling = tool.ConstrainedSampling
    };

    /// <summary>【CodingAgent】【工具组合】读取呈现变更，忽略没有定义的结果。</summary>
    /// <param name="value">扩展返回值。</param><returns>说明替换及隐藏集合。</returns>
    internal static CodingAgentToolLoadoutChanges ReadLoadoutChanges(JsonElement value) => new()
    {
        Descriptions = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("descriptions", out var descriptions) && descriptions.ValueKind == JsonValueKind.Object
            ? descriptions.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String).ToDictionary(property => property.Name, property => property.Value.GetString()!) : new Dictionary<string, string>(),
        HiddenDeclarations = value.ValueKind == JsonValueKind.Object ? ReadStringArray(value, "hiddenDeclarations") : []
    };
}
