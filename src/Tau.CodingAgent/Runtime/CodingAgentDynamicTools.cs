// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly HashSet<string> _pendingToolNames = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【动态工具】将运行中注册的工具合入目录，仅自动激活首次获得默认启用资格的工具。</summary>
    /// <param name="tool">当前扩展中最新的工具定义。</param>
    /// <param name="changes">Node 按更新后的工具集合计算的呈现变更。</param>
    internal void RegisterExtensionTool(CodingAgentExtensionToolAdapter tool, CodingAgentToolLoadoutChanges changes)
    {
        if (!_extensionToolsEnabled || !IsToolAllowed(tool.Name)) return;
        var previous = _registeredTools.FirstOrDefault(item => item.Name == tool.Name);
        if (previous is not null && CodingAgentSourceInfo.ForTool(previous).Source == "sdk") return;
        var active = GetActiveToolNames().ToList();
        var tools = _registeredTools.ToList();
        var index = tools.FindIndex(item => item.Name == tool.Name);
        if (index < 0) tools.Add(tool); else tools[index] = tool;
        _registeredTools = tools;
        if (_baseSystemPromptOptions is { } options && previous?.PromptSnippet is not null && string.IsNullOrWhiteSpace(tool.PromptSnippet))
            _baseSystemPromptOptions = options with { ToolSnippets = options.ToolSnippets.Where(pair => pair.Key != tool.Name).ToDictionary(pair => pair.Key, pair => pair.Value) };
        // 1. 【CodingAgent】【动态工具】重注册不能重新启用用户已经关闭的默认工具，策略从隐藏变为直接时则视为新启用
        if (_allowedToolNames?.Contains(tool.Name) == true && GetToolExposure(tool) is "direct" or "model-only" ||
            IsToolActiveOnRegistration(tool) && (previous is null || !IsToolActiveOnRegistration(previous))) active.Add(tool.Name);
        active.AddRange(_pendingToolNames);
        _config = _config with { Tools = ApplyToolLoadout(active, changes) };
        _pendingToolNames.ExceptWith(GetActiveToolNames());
        RefreshToolPromptMetadata();
    }

    /// <summary>【CodingAgent】【工具恢复】重放完整声明，暂存尚未注册的名称，等待扩展启动时提供定义。</summary>
    /// <param name="messages">目标会话的原始消息。</param>
    private void RestoreToolsFromTranscript(IReadOnlyList<ChatMessage> messages)
    {
        _pendingToolNames.Clear();
        if (Transcript.GetCurrentSystemMessage(messages) is not { } system) return;
        var names = (system.ToolsAdded ?? []).Select(tool => CodingAgentToolNames.ResolveRegistered(tool.Name, _registeredTools)).ToArray();
        _pendingToolNames.UnionWith(names.Where(IsToolAllowed));
        // 1. 【CodingAgent】【工具恢复】恢复可发生在会话操作锁内，纯元数据投影后在请求准备阶段执行扩展钩子
        _config = _config with { Tools = ApplyToolLoadout(names, new()) };
        _pendingToolNames.ExceptWith(GetActiveToolNames());
        RefreshToolPromptMetadata();
    }

    /// <summary>【CodingAgent】【工具提示】更新新增或替换工具的简介与规则，不覆盖技能和用户提示配置。</summary>
    private void RefreshToolPromptMetadata()
    {
        if (_baseSystemPromptOptions is not { } options) return;
        var snippets = new Dictionary<string, string>(options.ToolSnippets, StringComparer.Ordinal);
        var guidelines = options.ToolGuidelines.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var tool in _registeredTools)
        {
            if (!string.IsNullOrWhiteSpace(tool.PromptSnippet)) snippets[tool.Name] = tool.PromptSnippet;
            guidelines[tool.Name] = tool.PromptGuidelines.ToList();
        }
        _baseSystemPromptOptions = options with { SelectedTools = GetActiveToolNames().ToList(), ToolSnippets = snippets, ToolGuidelines = guidelines };
    }

    /// <summary>【CodingAgent】【工具约束】向 Node 传递当前动态注册限制和待恢复的工具名称。</summary>
    /// <param name="writer">当前运行时对象的 JSON 写入器。</param>
    internal void WriteToolRegistrationPolicy(Utf8JsonWriter writer)
    {
        writer.WriteBoolean("extensionToolsEnabled", _extensionToolsEnabled);
        writer.WritePropertyName("allowedTools");
        if (_allowedToolNames is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var name in _allowedToolNames) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }
        writer.WritePropertyName("excludedTools"); writer.WriteStartArray();
        foreach (var name in _excludedExtensionTools) writer.WriteStringValue(name);
        writer.WriteEndArray();
        writer.WritePropertyName("pendingTools"); writer.WriteStartArray();
        foreach (var name in _pendingToolNames) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【动态工具】将 Node 注册协议转换为使用同一工作进程的执行适配器。</summary>
    /// <param name="filePath">定义所属扩展。</param><param name="value">完整工具元数据。</param>
    /// <returns>可立即调用的工具适配器。</returns>
    internal CodingAgentExtensionToolAdapter CreateDynamicTool(string filePath, JsonElement value)
    {
        var tool = ReadTools(JsonSerializer.SerializeToElement(new { tools = new[] { value } })).Single();
        var definition = new CodingAgentExtensionTool(tool.Name, tool.Label, tool.Description, tool.ParameterSchema,
            filePath, "extension", "javascript", tool.HasPrepareArguments, tool.ExecutionMode)
        {
            SourceInfo = GetExtensionSource(filePath),
            HasPrepareLoadout = tool.HasPrepareLoadout, PromptSnippet = tool.PromptSnippet, PromptGuidelines = tool.PromptGuidelines,
            Exposure = tool.Exposure, DefaultActive = tool.DefaultActive, OutputSchema = tool.OutputSchema,
            Namespace = tool.Namespace, Annotations = tool.Annotations, ConstrainedSampling = tool.ConstrainedSampling
        };
        return new(definition, this);
    }
}
