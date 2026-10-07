// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentTreeSessionStore
{
    private bool _hasNativeLeaf;
    private string? _nativeLeaf;

    /// <summary>【CodingAgent】【原生导航】只移动内存中的叶位置，后续追加使用新父链，不写入额外的伪会话条目。</summary>
    /// <param name="leaf">目标完整标识，空值表示根之前。</param>
    internal void NavigateNativeLeaf(string? leaf)
    {
        lock (SyncRoot)
        {
            if (leaf is not null && !ReadState().ById.ContainsKey(leaf)) throw new ArgumentException("Session entry not found.", nameof(leaf));
            _nativeLeaf = leaf;
            _hasNativeLeaf = true;
        }
    }

    /// <summary>【CodingAgent】【原生导航追加】按准备好的父链写入摘要或标签，保留原始来源标识。</summary>
    /// <param name="entry">完整的新条目。</param>
    internal void AppendNavigationEntry(CodingAgentTreeSessionEntry entry)
    {
        lock (SyncRoot) AppendEntry(entry);
    }
    /// <summary>【CodingAgent】【独立新会话】在相同会话目录建立新的文件与标识，原会话文件保持原样。</summary>
    /// <param name="parentSession">可选父会话路径。</param>
    /// <returns>已经写入新会话头的独立存储。</returns>
    public CodingAgentTreeSessionStore CreateNewSession(string? parentSession = null)
    {
        lock (SyncRoot) return WriteReplacementSession(ReadState().Header.Cwd, parentSession, []);
    }

    /// <summary>【CodingAgent】【独立分叉】复制根到目标节点的分支，保留消息标识、上下文编辑及最新有效标签。</summary>
    /// <param name="leafId">要保留的最后一个条目的完整标识。</param>
    /// <returns>新的分叉文件，原文件及其叶节点不变。</returns>
    public CodingAgentTreeSessionStore CreateBranchedSession(string leafId)
    {
        lock (SyncRoot)
        {
            var state = ReadState();
            if (!state.ById.ContainsKey(leafId)) throw new ArgumentException("Invalid entry ID for forking", nameof(leafId));
            var entries = BuildReplacementBranch(state.Entries, state.GetBranch(leafId));
            return WriteReplacementSession(state.Header.Cwd, _path, entries);
        }
    }

    /// <summary>【CodingAgent】【分叉标签】重新串联移除标签后的父链，并把最新有效标签放到分支末尾。</summary>
    /// <param name="allEntries">包含其他分支标签更新的完整会话记录。</param>
    /// <param name="branch">待复制分支。</param>
    /// <returns>保留来源标识和有效引用的新条目列表。</returns>
    internal static IReadOnlyList<CodingAgentTreeSessionEntry> BuildReplacementBranch(
        IReadOnlyList<CodingAgentTreeSessionEntry> allEntries, IReadOnlyList<CodingAgentTreeSessionEntry> branch)
    {
        var result = new List<CodingAgentTreeSessionEntry>();
        var replacedLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingLabels = new List<string>();
        string? parent = null;
        // 1. 【CodingAgent】【分叉父链】原生分叉保留消息 ID，只重建标签条目，不重映射工具或自定义元数据中的引用
        foreach (var original in branch)
        {
            if (original.Type == "label") { pendingLabels.Add(original.Id); continue; }
            foreach (var labelId in pendingLabels) replacedLabels[labelId] = original.Id;
            pendingLabels.Clear();
            var firstKept = original.FirstKeptEntryId is { } first && replacedLabels.TryGetValue(first, out var replacement) ? replacement : original.FirstKeptEntryId;
            result.Add(original.Clone(original.Id, parent, firstKeptEntryId: firstKept));
            parent = original.Id;
        }
        // 2. 【CodingAgent】【分叉标签】采用整张会话树的最新标签值，删除标签不会在新分支复活
        var latestLabels = new Dictionary<string, CodingAgentTreeSessionEntry>(StringComparer.Ordinal);
        foreach (var entry in allEntries)
            if (entry.Type == "label" && entry.TargetId is { } target)
            {
                if (string.IsNullOrEmpty(entry.Label)) latestLabels.Remove(target);
                else latestLabels[target] = entry;
            }
        var ids = result.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var label in latestLabels.Values)
        {
            if (label.TargetId is null || !ids.Contains(label.TargetId) || string.IsNullOrEmpty(label.Label)) continue;
            var id = CreateEntryId(ids);
            result.Add(new() { Type = "label", Id = id, ParentId = parent, Timestamp = label.Timestamp, TargetId = label.TargetId, Label = label.Label });
            ids.Add(id);
            parent = id;
        }
        return result;
    }

    /// <summary>【CodingAgent】【替换文件】使用新建文件模式写入独立会话，禁止覆盖碰巧同名的已有文件。</summary>
    /// <param name="cwd">会话工作目录。</param>
    /// <param name="parentSession">父会话路径。</param>
    /// <param name="entries">已准备的分支条目。</param>
    /// <returns>新存储。</returns>
    private CodingAgentTreeSessionStore WriteReplacementSession(string cwd, string? parentSession, IReadOnlyList<CodingAgentTreeSessionEntry> entries)
        => CreateReplacementSession(System.IO.Path.GetDirectoryName(_path)!, cwd, parentSession, entries, _secretRedactor);

    /// <summary>【CodingAgent】【兼容迁移】从平面或内存分支创建独立原生文件，不覆盖原始来源。</summary>
    /// <param name="directory">新文件所在目录。</param>
    /// <param name="cwd">会话工作目录。</param>
    /// <param name="parentSession">可选来源文件。</param>
    /// <param name="entries">已准备的原生分支。</param>
    /// <param name="secretRedactor">继承来源的敏感值清理配置。</param>
    /// <returns>独立的原生会话存储。</returns>
    internal static CodingAgentTreeSessionStore CreateReplacementSession(string directory, string cwd, string? parentSession,
        IReadOnlyList<CodingAgentTreeSessionEntry> entries, TauSecretRedactor? secretRedactor = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var id = CreateSessionId();
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, timestamp.ToString("yyyy-MM-ddTHH-mm-ss-fff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "_" + id + ".jsonl");
        var header = new CodingAgentTreeSessionHeader
        {
            Type = "session", Version = CurrentVersion, Id = id, Timestamp = timestamp,
            Cwd = cwd, ParentSession = string.IsNullOrWhiteSpace(parentSession) ? null : parentSession
        };
        secretRedactor ??= TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.CodingAgentEnvironmentVariable);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonlSecretRedactor.RedactLine(JsonSerializer.Serialize(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader), secretRedactor));
            foreach (var entry in entries)
                writer.WriteLine(JsonlSecretRedactor.RedactLine(JsonSerializer.Serialize(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry), secretRedactor));
        }
        return new CodingAgentTreeSessionStore(path, cwd, secretRedactor);
    }
}
