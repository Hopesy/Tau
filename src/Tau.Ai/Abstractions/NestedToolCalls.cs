// 作者：xxx
using System.Text.Json;

namespace Tau.Ai;

/// <summary>【Ai】【嵌套调用】父工具运行期间发起的单次调用记录，不包含返回内容。</summary>
public sealed record NestedToolCallRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JsonElement? Arguments { get; init; }
    public int? ArgumentsBytes { get; init; }
    public required string Status { get; init; }
    public long? DurationMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>【Ai】【嵌套调用】有界的调用记录，只保存在会话中，不作为模型输入。</summary>
/// <param name="Calls">按开始顺序排列的调用。</param>
/// <param name="Complete">是否无遗漏、无参数裁剪且全部完成。</param>
public sealed record NestedToolCalls(IReadOnlyList<NestedToolCallRecord> Calls, bool Complete);
