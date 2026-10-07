// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本宿主选项】覆盖动态设置，并控制脚本能否访问会话模型目录及非聊天能力。</summary>
public sealed record CodingAgentCodeModeOptions
{
    public bool Models { get; init; } = true;
    /// <summary>【CodingAgent】【脚本呈现模式】on 保留直接工具声明，only 将直接调用入口收敛到脚本；空值跟随设置。</summary>
    public string? Mode { get; init; }
    /// <summary>【CodingAgent】【脚本声明预算】非负有限 token 估算预算；空值跟随设置。</summary>
    public double? InlineBudget { get; init; }
    /// <summary>【CodingAgent】【动态脚本模式】每次准备声明时读取，返回空值则采用静态模式或会话设置。</summary>
    public Func<string?>? GetMode { get; init; }
    /// <summary>【CodingAgent】【动态脚本预算】每次准备声明时读取，返回空值则采用静态预算或会话设置。</summary>
    public Func<double?>? GetInlineBudget { get; init; }
    /// <summary>【CodingAgent】【宿主命名组】传入工具原名并返回命名组；配置此回调后空返回值表示该工具无命名组。</summary>
    public Func<string, JsonElement?>? GetToolNamespace { get; init; }
    /// <summary>【CodingAgent】【宿主状态写入】成功脚本有状态变更时接收自定义条目类型及独立数据；配置后由宿主负责持久化。</summary>
    public Action<string, CodingAgentCodeModeStoreEntryData>? AppendEntry { get; init; }
    /// <summary>【CodingAgent】【内置状态保存】无写入回调时是否保存到会话，关闭后仍读取当前分支已有状态。</summary>
    public bool PersistStore { get; init; } = true;

    /// <summary>【CodingAgent】【脚本选项校验】在注册前拒绝无效模式与预算，避免留下不可用工具。</summary>
    internal void Validate()
    {
        if (Mode is not (null or "on" or "only")) throw new ArgumentException("Codemode mode must be on or only", nameof(Mode));
        if (InlineBudget is { } value && (!double.IsFinite(value) || value < 0))
            throw new ArgumentOutOfRangeException(nameof(InlineBudget), "Codemode inline budget must be finite and non-negative");
    }
}

/// <summary>【CodingAgent】【脚本目录选项】不创建会话即可生成工具提示，默认不声明模型能力且不限制声明预算。</summary>
public sealed record CodingAgentCodeModeDescriptionOptions
{
    public bool Models { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Namespaces { get; init; }
    public IReadOnlySet<string>? Deferred { get; init; }
    public double? InlineBudget { get; init; }
}

/// <summary>【CodingAgent】【脚本工具声明】只包含脚本可见元数据，不暴露宿主执行委托。</summary>
/// <param name="Name">原工具名。</param><param name="Description">工具说明。</param><param name="InputSchema">独立输入 Schema。</param>
/// <param name="OutputSchema">独立输出 Schema，未声明时使用文本类型。</param>
public sealed record CodingAgentCodeModeDeclaration(string Name, string Description, JsonElement InputSchema, JsonElement OutputSchema);

/// <summary>【CodingAgent】【脚本状态条目】一个成功脚本的写入与删除集合，宿主可保存为 codemode-store 自定义条目。</summary>
/// <param name="Set">独立 JSON 对象，包含写入值。</param><param name="Delete">需要删除的键。</param>
public sealed record CodingAgentCodeModeStoreEntryData(JsonObject Set, IReadOnlyList<string> Delete);
