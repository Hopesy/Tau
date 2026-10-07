// 作者：xxx
namespace Tau.Ai;

/// <summary>【AI】【系统消息】记录系统指令、命名段落及工具声明的增量，按会话顺序重放。</summary>
/// <param name="Content">新增指令文本；空字符串表示只更新段落或工具。</param>
public sealed record SystemMessage(string Content) : ChatMessage("system")
{
    /// <summary>命名段落更新，null 表示删除该段落。</summary>
    public IReadOnlyDictionary<string, string?>? Sections { get; init; }
    /// <summary>新增或重定义的工具，不包含执行委托。</summary>
    public IReadOnlyList<Tool>? ToolsAdded { get; init; }
    /// <summary>移除的工具，先移除再应用新增声明。</summary>
    public IReadOnlyList<ToolReference>? ToolsRemoved { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>仅通过名称引用已声明工具。</summary>
/// <param name="Name">区分大小写的工具名称。</param>
public sealed record ToolReference(string Name);

/// <summary>完整工具集合之间的声明差异，同名重定义同时产生移除和新增。</summary>
/// <param name="ToolsAdded">新增定义。</param>
/// <param name="ToolsRemoved">移除引用。</param>
public sealed record ToolStateChanges(IReadOnlyList<Tool> ToolsAdded, IReadOnlyList<ToolReference> ToolsRemoved);
