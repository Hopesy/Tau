// 作者：xxx
using System.Text.Json;
using Tau.Ai.Serialization;

namespace Tau.Ai;

/// <summary>【AI】【会话重放】按系统消息恢复工具与提示词，并适配现有 provider 请求接口。</summary>
public static class Transcript
{
    /// <summary>复制可持久化的工具定义，隔离 JSON 与可变约束配置。</summary>
    /// <param name="tool">源工具。</param>
    /// <returns>不包含运行时或显示字段的声明快照。</returns>
    public static Tool ToToolDeclaration(Tool tool) => new(tool.Name, tool.Description, tool.ParameterSchema.Clone())
    {
        ConstrainedSampling = tool.ConstrainedSampling is { } sampling ? sampling with
        {
            Variants = sampling.Variants?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        } : null
    };

    /// <summary>比较模型可见的工具接口，忽略格式化空白，保留 JSON 字段顺序。</summary>
    /// <param name="left">旧工具。</param>
    /// <param name="right">新工具。</param>
    /// <returns>声明相同返回 true。</returns>
    public static bool DeclarationsEqual(Tool left, Tool right) =>
        JsonSerializer.Serialize(ToToolDeclaration(left), TauAiJsonContext.Default.Tool) ==
        JsonSerializer.Serialize(ToToolDeclaration(right), TauAiJsonContext.Default.Tool);

    /// <summary>依次应用移除与新增，保留首次声明及删除后重新加入的顺序。</summary>
    /// <param name="messages">包含任意消息角色的会话。</param>
    /// <returns>当前可见的工具集合。</returns>
    public static IReadOnlyList<Tool> GetCurrentTools(IEnumerable<ChatMessage> messages)
    {
        var tools = new List<Tool>();
        foreach (var message in messages.OfType<SystemMessage>())
        {
            foreach (var removed in message.ToolsRemoved ?? []) tools.RemoveAll(tool => tool.Name == removed.Name);
            foreach (var added in message.ToolsAdded ?? [])
            {
                var index = tools.FindIndex(tool => tool.Name == added.Name);
                if (index < 0) tools.Add(added);
                else tools[index] = added;
            }
        }
        return tools.ToArray();
    }

    /// <summary>【AI】【工具历史】收集所有曾声明的工具，同名保留最后定义，移除操作不删除历史声明。</summary>
    /// <param name="messages">规范化的会话消息。</param>
    /// <returns>用于历史调用解释的声明集合。</returns>
    public static IReadOnlyList<Tool> GetDeclaredTools(IEnumerable<ChatMessage> messages)
    {
        var declarations = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<SystemMessage>())
            foreach (var tool in message.ToolsAdded ?? []) declarations[tool.Name] = tool;
        return declarations.Values.ToArray();
    }

    /// <summary>计算两个完整工具集合之间的变化。</summary>
    /// <param name="previous">已经声明的集合。</param>
    /// <param name="current">当前可执行集合对应的声明。</param>
    /// <returns>按各自输入顺序排列的新增和移除。</returns>
    public static ToolStateChanges GetToolStateChanges(IReadOnlyList<Tool> previous, IReadOnlyList<Tool> current)
    {
        var before = previous.GroupBy(tool => tool.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var after = current.GroupBy(tool => tool.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return new(
            current.Where(tool => !before.TryGetValue(tool.Name, out var old) || !DeclarationsEqual(old, tool)).Select(ToToolDeclaration).ToArray(),
            previous.Where(tool => !after.TryGetValue(tool.Name, out var next) || !DeclarationsEqual(tool, next)).Select(tool => new ToolReference(tool.Name)).ToArray());
    }

    /// <summary>重放系统指令和命名段落，null 删除段落，替换不改变其顺序。</summary>
    /// <param name="messages">会话消息。</param>
    /// <returns>完整系统提示文本。</returns>
    public static string GetCurrentSystemPrompt(IEnumerable<ChatMessage> messages)
    {
        var current = GetCurrentSystemMessage(messages);
        return current is null ? "" : GetSystemMessageText(current);
    }

    /// <summary>【AI】【会话基线】重放完整系统状态，保留命名段落、首条时间戳和当前工具。</summary>
    /// <param name="messages">待重放的会话。</param>
    /// <returns>独立的系统声明快照；没有系统消息时返回 null。</returns>
    public static SystemMessage? GetCurrentSystemMessage(IEnumerable<ChatMessage> messages)
    {
        var systems = messages.OfType<SystemMessage>().ToArray();
        if (systems.Length == 0) return null;
        var content = new List<string>();
        var sections = new List<KeyValuePair<string, string>>();
        foreach (var message in systems)
        {
            if (message.Content.Length > 0) content.Add(message.Content);
            foreach (var (name, value) in message.Sections ?? new Dictionary<string, string?>())
            {
                var index = sections.FindIndex(section => section.Key == name);
                if (value is null) { if (index >= 0) sections.RemoveAt(index); }
                else if (index < 0) sections.Add(new(name, value));
                else sections[index] = new(name, value);
            }
        }
        var tools = GetCurrentTools(systems);
        return new(string.Join("\n\n", content))
        {
            Sections = sections.Count == 0 ? null : sections.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
            ToolsAdded = tools.Count == 0 ? null : tools.Select(ToToolDeclaration).ToArray(),
            Timestamp = systems[0].Timestamp
        };
    }

    /// <summary>【AI】【会话基线】从旧提示和工具创建开场声明，空配置不产生消息。</summary>
    /// <param name="systemPrompt">初始提示。</param>
    /// <param name="tools">初始工具定义。</param>
    /// <returns>毫秒时间戳为零的声明快照；提示和工具均为空时返回 null。</returns>
    public static SystemMessage? CreateInitialSystemMessage(string? systemPrompt, IReadOnlyList<Tool>? tools) =>
        string.IsNullOrEmpty(systemPrompt) && tools is not { Count: > 0 } ? null : new(systemPrompt ?? "")
        {
            ToolsAdded = tools is { Count: > 0 } ? tools.Select(ToToolDeclaration).ToArray() : null,
            Timestamp = DateTimeOffset.UnixEpoch
        };

    /// <summary>【AI】【会话规范化】把旧顶层提示和工具转换为首条系统声明，重复调用不重复插入。</summary>
    /// <param name="context">可混用旧字段和系统消息的上下文。</param>
    /// <returns>仅通过消息携带声明的上下文。</returns>
    public static LlmContext NormalizeContext(LlmContext context)
    {
        if (string.IsNullOrEmpty(context.SystemPrompt) && context.Tools is not { Count: > 0 })
            return context with { SystemPrompt = null, Tools = null };

        var initial = new SystemMessage(context.SystemPrompt ?? "")
        {
            ToolsAdded = context.Tools is { Count: > 0 } ? context.Tools : null,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        return new(null, [initial, .. context.Messages], null);
    }

    /// <summary>【AI】【协议适配】按模型能力保留系统消息位置，或重放为单条开场声明。</summary>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <param name="supportsMidConvoSystemMessages">是否支持会话中途的系统消息。</param>
    /// <returns>可直接用于原生协议转换的消息式上下文。</returns>
    public static LlmContext ResolveTranscript(LlmContext context, bool supportsMidConvoSystemMessages)
    {
        // 1. 【AI】【协议适配】统一声明来源，避免旧工具字段重复覆盖历史
        context = NormalizeContext(context);
        if (supportsMidConvoSystemMessages || !context.Messages.Any(message => message is SystemMessage)) return context;

        // 2. 【AI】【协议适配】不支持中途指令时，把当前提示和工具放到消息开头
        var initial = GetCurrentSystemMessage(context.Messages)!;
        return new(null, [initial, .. context.Messages.Where(message => message is not SystemMessage)], null);
    }

    /// <summary>渲染开场声明，依次拼接正文和非空命名段落。</summary>
    /// <param name="message">开场系统消息。</param>
    /// <returns>协议中的系统提示文本。</returns>
    public static string GetSystemMessageText(SystemMessage message) => string.Join("\n\n",
        new[] { message.Content }.Concat(message.Sections?.Values ?? []).Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>渲染中途更新，明确表达命名段落的替换和删除。</summary>
    /// <param name="message">中途系统消息。</param>
    /// <returns>与上游一致的增量指令文本。</returns>
    public static string RenderSystemMessageUpdate(SystemMessage message)
    {
        var parts = new List<string>();
        if (message.Content.Length > 0) parts.Add(message.Content);
        foreach (var (name, value) in message.Sections ?? new Dictionary<string, string?>())
            parts.Add(value is null
                ? $"Removed system prompt section \"{name}\"."
                : $"Updated system prompt section \"{name}\":\n\n{value}");
        return string.Join("\n\n", parts);
    }

    /// <summary>检查仅支持新增的协议无法重放的移除或同名再次声明。</summary>
    /// <param name="messages">规范化后的会话消息。</param>
    /// <returns>有移除或重复声明时返回 true，即使同名声明内容相同。</returns>
    public static bool HasNonAdditiveToolChanges(IEnumerable<ChatMessage> messages)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<SystemMessage>())
        {
            if (message.ToolsRemoved is { Count: > 0 }) return true;
            foreach (var tool in message.ToolsAdded ?? [])
                if (!names.Add(tool.Name)) return true;
        }
        return false;
    }

    /// <summary>【AI】【工具传输】划分顶层工具和随系统消息发送的新增工具。</summary>
    /// <param name="messages">规范化并按模型能力处理后的消息。</param>
    /// <param name="supportsToolAdditions">协议是否支持在原位置声明新增工具。</param>
    /// <returns>顶层工具集合，以及是否在消息原位置发送后续新增工具。</returns>
    public static (IReadOnlyList<Tool> RequestTools, bool AnchorsAdditions) ResolveTranscriptTools(
        IReadOnlyList<ChatMessage> messages, bool supportsToolAdditions)
    {
        var anchors = supportsToolAdditions && !HasNonAdditiveToolChanges(messages);
        var initialTools = messages.Count > 0 && messages[0] is SystemMessage initial ? initial.ToolsAdded : null;
        return (anchors ? initialTools ?? [] : GetCurrentTools(messages), anchors);
    }

    /// <summary>【AI】【请求适配】把系统消息重放为现有 provider 的顶层提示和工具字段。</summary>
    /// <param name="context">可混用旧顶层字段和新系统消息的输入。</param>
    /// <returns>移除系统增量后的请求上下文；输入不被修改。</returns>
    public static LlmContext ResolveContext(LlmContext context)
    {
        if (!context.Messages.Any(message => message is SystemMessage)) return context;
        // 1. 【AI】【请求适配】旧字段视为最早的声明，后续会话增量拥有覆盖权
        var initial = new SystemMessage(context.SystemPrompt ?? "") { ToolsAdded = context.Tools, Timestamp = DateTimeOffset.UnixEpoch };
        ChatMessage[] transcript = [initial, .. context.Messages];
        return new(GetCurrentSystemPrompt(transcript), context.Messages.Where(message => message is not SystemMessage).ToArray(), GetCurrentTools(transcript));
    }
}
