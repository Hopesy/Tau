// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本状态快照】绑定脚本开始时的会话和分支，值均为独立 JSON。</summary>
internal sealed record CodingAgentCodeModeStore(string SessionId, string? LeafId, JsonObject Values);

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【脚本状态读取】只重放当前分支上的成功写入，删除操作按条目顺序生效。</summary>
    /// <returns>会话身份和合并状态。</returns>
    internal CodingAgentCodeModeStore ReadCodeModeStore()
    {
        lock (_gate)
        {
            var snapshot = Snapshot(); var values = new JsonObject();
            foreach (var entry in GetSnapshotBranch(snapshot))
            {
                if (entry.CustomType != "codemode-store" || entry.Data is not { ValueKind: JsonValueKind.Object } data ||
                    !data.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("delete", out var deleted) || deleted.ValueKind != JsonValueKind.Array || deleted.EnumerateArray().Any(key => key.ValueKind != JsonValueKind.String)) continue;
                foreach (var key in deleted.EnumerateArray()) values.Remove(key.GetString()!);
                foreach (var pair in set.EnumerateObject()) values[pair.Name] = JsonNode.Parse(pair.Value.GetRawText());
            }
            return new(snapshot.Header.Id, snapshot.LeafId, values);
        }
    }

    /// <summary>【CodingAgent】【脚本状态提交】成功脚本追加自定义条目，阻止会话替换或切换到其他分支后提交旧状态。</summary>
    /// <param name="original">脚本开始时的快照。</param><param name="result">脚本结果。</param>
    internal void CommitCodeModeStore(CodingAgentCodeModeStore original, CodingAgentCodeModeResult result)
    {
        if (!result.Success || result.Set.Count == 0 && result.Delete.Count == 0) return;
        lock (_gate)
        {
            var snapshot = Snapshot();
            if (snapshot.Header.Id != original.SessionId || original.LeafId is { } leaf && !GetSnapshotBranch(snapshot).Any(entry => entry.Id == leaf))
                throw new InvalidOperationException("Session branch changed during codemode execution");
            var entry = new CodingAgentTreeSessionEntry
            {
                Id = Guid.NewGuid().ToString("N"), ParentId = snapshot.LeafId, Type = "custom", Timestamp = DateTimeOffset.UtcNow, CustomType = "codemode-store",
                Data = JsonSerializer.SerializeToElement(new JsonObject { ["set"] = result.Set.DeepClone(), ["delete"] = new JsonArray(result.Delete.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) })
            };
            if (_tree is not null) { _tree.Store.AppendExtensionEntry(entry); _tree.LoadSnapshot(); } else AppendMemoryEntry(entry);
            (_runner as RuntimeCodingAgentRunner)?.NotifyVirtualModelState(entry);
        }
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【脚本状态桥接】读取当前实际绑定的分支，避免会话替换后持有旧存储。</summary><returns>当前状态。</returns>
    internal CodingAgentCodeModeStore ReadCodeModeStore() => (_sessionBridge ?? throw new InvalidOperationException("Session is not bound")).ReadCodeModeStore();
    /// <summary>【CodingAgent】【脚本提交桥接】把成功结果写入当前分支并核验开始时的身份。</summary><param name="store">开始快照。</param><param name="result">结果。</param>
    internal void CommitCodeModeStore(CodingAgentCodeModeStore store, CodingAgentCodeModeResult result) =>
        (_sessionBridge ?? throw new InvalidOperationException("Session is not bound")).CommitCodeModeStore(store, result);
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【脚本状态入口】向内置工具提供当前分支状态。</summary><returns>当前状态。</returns>
    internal CodingAgentCodeModeStore ReadCodeModeStore() => _javaScriptRuntime.ReadCodeModeStore();
    /// <summary>【CodingAgent】【脚本状态保存】提交成功脚本的写入。</summary><param name="store">快照。</param><param name="result">结果。</param>
    internal void CommitCodeModeStore(CodingAgentCodeModeStore store, CodingAgentCodeModeResult result) => _javaScriptRuntime.CommitCodeModeStore(store, result);
}
