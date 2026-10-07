// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展会话】传递当前会话的只读树快照。</summary>
internal sealed class CodingAgentExtensionSessionSnapshot
{
    public required CodingAgentTreeSessionHeader Header { get; init; }
    public required IReadOnlyList<CodingAgentTreeSessionEntry> Entries { get; init; }
    public string? LeafId { get; init; }
    public string? Name { get; init; }
    public string? SessionFile { get; init; }
    public string? SessionDir { get; init; }
}

/// <summary>【CodingAgent】【扩展会话】为 JavaScript 提供树元数据读写，扩展状态不进入 LLM 消息。</summary>
public sealed partial class CodingAgentTreeSessionStore
{
    internal object SyncRoot { get; } = new();

    /// <summary>【CodingAgent】【扩展读取】读取全部树条目及当前分支位置。</summary>
    /// <returns>供扩展读取的序列化快照。</returns>
    internal CodingAgentExtensionSessionSnapshot ReadExtensionSnapshot()
    {
        var state = ReadState();
        return new()
        {
            Header = state.Header, Entries = state.Entries.ToArray(), LeafId = state.LeafId,
            Name = BuildSnapshot(state, state.GetBranch(state.LeafId)).Name,
            SessionFile = Path, SessionDir = System.IO.Path.GetDirectoryName(Path)
        };
    }

    /// <summary>【CodingAgent】【扩展写入】把扩展元数据追加到当前叶节点，保留扩展生成的稳定条目标识。</summary>
    /// <param name="entry">已校验的自定义、会话名称或标签条目。</param>
    internal void AppendExtensionEntry(CodingAgentTreeSessionEntry entry)
    {
        var state = ReadState();
        if (state.EntryIds.Contains(entry.Id)) throw new InvalidOperationException("Duplicate extension session entry ID.");
        if (entry.Type == "label" && !state.EntryIds.Contains(entry.TargetId!))
            throw new InvalidOperationException("Session label target does not exist.");
        AppendEntry(entry.Clone(entry.Id, state.LeafId));
    }
}

/// <summary>【CodingAgent】【扩展桥接】绑定运行器、持久树或临时内存会话，串行应用扩展元数据操作。</summary>
internal sealed partial class CodingAgentExtensionSessionBridge
{
    private readonly object _gate = new();
    private readonly ICodingAgentRunner _runner;
    private CodingAgentTreeSessionController? _tree;
    private readonly CodingAgentSessionStore? _flat;
    private readonly List<CodingAgentTreeSessionEntry> _memoryEntries = [];
    private string? _memoryLeafId;
    private CodingAgentTreeSessionHeader _memoryHeader;
    private int _memoryMessageCount;
    private ChatMessage? _memoryFirstMessage;
    private CodingAgentJavaScriptExtensionRuntime? _extensions;
    internal CodingAgentTreeSessionController? TreeController => _tree;

    /// <summary>【CodingAgent】【替换绑定】连接扩展模块生命周期，允许命令切换实际会话。</summary>
    /// <param name="extensions">所属 JavaScript 运行时。</param>
    internal void ConfigureReplacement(CodingAgentJavaScriptExtensionRuntime extensions) => _extensions = extensions;

    /// <summary>【CodingAgent】【扩展绑定】创建一次会话绑定；没有存储时只在内存中维护元数据。</summary>
    /// <param name="runner">当前运行器。</param>
    /// <param name="tree">可选树会话控制器。</param>
    /// <param name="flat">可选旧式平面存储。</param>
    /// <param name="cwd">扩展工作目录。</param>
    public CodingAgentExtensionSessionBridge(ICodingAgentRunner runner, CodingAgentTreeSessionController? tree, CodingAgentSessionStore? flat, string cwd)
    {
        _runner = runner;
        _tree = tree;
        _flat = flat;
        _memoryHeader = NewMemoryHeader(cwd);
        if (runner is RuntimeCodingAgentRunner runtime)
        {
            runtime.ConfigureBoundarySession(this);
            runtime.BackgroundEvent += PersistBackgroundEventAsync;
            runtime.ConfigureCompactionSession(ReadCompactionContext, CommitPreparedCompaction,
                id => Snapshot().Entries.First(entry => entry.Id == id));
            runtime.ConfigureSessionStatistics(ReadSessionStatistics);
            runtime.ConfigureRecoverySession(OmitRecoveryMessages);
            runtime.ConfigureSessionReplacement(ReplaceSessionAsync, () => _tree);
            runtime.SessionId = tree?.GetSessionHeader().Id ?? _memoryHeader.Id;
        }
    }

    /// <summary>【CodingAgent】【扩展绑定】判断是否仍绑定相同会话，防止宿主重复绑定时清除临时状态。</summary>
    /// <param name="runner">待绑定运行器。</param>
    /// <param name="tree">待绑定树控制器；为空时保留已有绑定。</param>
    /// <param name="flat">待绑定平面存储；为空时保留已有绑定。</param>
    /// <returns>是否为现有绑定。</returns>
    public bool Matches(ICodingAgentRunner runner, CodingAgentTreeSessionController? tree, CodingAgentSessionStore? flat = null) =>
        ReferenceEquals(_runner, runner) && (tree is null || ReferenceEquals(_tree, tree)) && (flat is null || ReferenceEquals(_flat, flat));

    /// <summary>【CodingAgent】【扩展读取】提交已有消息后生成当前会话快照。</summary>
    /// <returns>可序列化的会话快照。</returns>
    public CodingAgentExtensionSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            // 1. 【CodingAgent】【扩展读取】树会话先同步已提交消息，再读取最新叶节点和元数据
            if (_tree is not null)
            {
                lock (_tree.Store.SyncRoot)
                {
                    _tree.SyncFromRunner(_runner);
                    return _tree.Store.ReadExtensionSnapshot();
                }
            }
            // 2. 【CodingAgent】【扩展读取】无树存储时维护独立内存状态，禁止隐式创建会话文件
            SyncMemoryMessages();
            return new()
            {
                Header = _memoryHeader, Entries = _memoryEntries.ToArray(),
                LeafId = _memoryLeafId, Name = _runner.SessionName,
                SessionFile = _flat?.Path, SessionDir = _flat is null ? null : Path.GetDirectoryName(_flat.Path)
            };
        }
    }

    /// <summary>【CodingAgent】【扩展写入】校验会话身份并持久化单条元数据，错误交由调用通道报告。</summary>
    /// <param name="action">包含会话标识及条目的协议对象。</param>
    public void Apply(JsonElement action)
    {
        lock (_gate)
        {
            // 1. 【CodingAgent】【扩展写入】核对真实会话身份，拒绝切换会话前仍未完成的旧调用
            var snapshot = Snapshot();
            if (action.GetProperty("sessionId").GetString() != snapshot.Header.Id)
                throw new InvalidOperationException("Extension session changed while the request was running.");
            if (action.TryGetProperty("operation", out var operation))
            {
                ApplyRuntimeOperation(operation.GetString()!, action);
                _tree?.SyncFromRunner(_runner);
                _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
                return;
            }
            var entry = JsonSerializer.Deserialize(action.GetProperty("entry"), CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)
                ?? throw new InvalidOperationException("Missing extension session entry.");
            var setup = action.TryGetProperty("setup", out var setupValue) && setupValue.ValueKind == JsonValueKind.True;
            Validate(entry, snapshot, setup);
            // 2. 【CodingAgent】【扩展写入】持久树按实际当前叶节点追加，写入成功后再更新运行器名称
            if (_tree is not null)
            {
                lock (_tree.Store.SyncRoot)
                {
                    _tree.Store.AppendExtensionEntry(entry);
                    if (entry.Type == "session_info") _runner.SessionName = entry.Name;
                    if (!setup) _tree.LoadSnapshot();
                }
            }
            else
            {
                if (_flat is not null && entry.Type != "session_info")
                    throw new InvalidOperationException("Persistent custom entries and labels require a JSONL session.");
                AppendMemoryEntry(entry.Clone(entry.Id, snapshot.LeafId));
                if (entry.Type == "session_info") _runner.SessionName = entry.Name;
            }
            // 3. 【CodingAgent】【扩展写入】兼容 SDK 的平面副本，只同步消息与名称等已支持字段
            _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            if (entry.Type == "session_info" && !setup && _runner is RuntimeCodingAgentRunner runtime) runtime.NotifySessionInfoChanged();
            if (entry.Type == "custom" && !setup && _runner is RuntimeCodingAgentRunner entryRunner)
                entryRunner.NotifyCustomEntryAppended(Snapshot().Entries.First(item => item.Id == entry.Id));
        }
    }

    /// <summary>【CodingAgent】【扩展校验】限制协议操作类型，并校验条目标识、名称和标签目标。</summary>
    /// <param name="entry">扩展条目。</param>
    /// <param name="snapshot">当前实际快照。</param>
    /// <param name="setup">是否为新会话初始化回调允许的消息或配置追加。</param>
    private static void Validate(CodingAgentTreeSessionEntry entry, CodingAgentExtensionSessionSnapshot snapshot, bool setup = false)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 64 ||
            entry.Id.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') || snapshot.Entries.Any(existing => existing.Id == entry.Id))
            throw new InvalidOperationException("Invalid extension session entry ID.");
        if (entry.Type == "custom" && string.IsNullOrWhiteSpace(entry.CustomType))
            throw new InvalidOperationException("Custom session entry type is required.");
        if (entry.Type == "label" && !snapshot.Entries.Any(existing => existing.Id == entry.TargetId))
            throw new InvalidOperationException("Session label target does not exist.");
        if (entry.Type is not ("custom" or "session_info" or "label") && !(setup && entry.Type is "message" or "custom_message" or "model_change" or "thinking_level_change"))
            throw new InvalidOperationException("Unsupported extension session operation.");
    }

    /// <summary>【CodingAgent】【临时会话】把已提交消息映射为内存条目，不创建磁盘文件。</summary>
    private void SyncMemoryMessages()
    {
        // 1. 【CodingAgent】【临时会话】历史缩短或更换首条消息时清除旧会话元数据
        if (_runner.Messages.Count < _memoryMessageCount ||
            (_memoryMessageCount > 0 && !ReferenceEquals(_memoryFirstMessage, _runner.Messages.FirstOrDefault())))
        {
            _memoryEntries.Clear();
            _memoryLeafId = null;
            _memoryMessageCount = 0;
            _memoryHeader = NewMemoryHeader(_memoryHeader.Cwd);
        }
        // 2. 【CodingAgent】【临时会话】只追加尚未镜像的消息，保持扩展元数据与消息的先后顺序
        _memoryFirstMessage = _runner.Messages.FirstOrDefault();
        for (; _memoryMessageCount < _runner.Messages.Count; _memoryMessageCount++)
            AppendMemoryEntry(CodingAgentTreeSessionStore.CreateMessageEntry(_runner.Messages[_memoryMessageCount], Guid.NewGuid().ToString("N"), _memoryLeafId));
    }

    /// <summary>【CodingAgent】【内存父链】追加条目并推进当前分支位置，保留其他分支条目。</summary>
    /// <param name="entry">已设置父节点的条目。</param>
    private void AppendMemoryEntry(CodingAgentTreeSessionEntry entry)
    {
        _memoryEntries.Add(entry);
        _memoryLeafId = entry.Id;
    }

    /// <summary>【CodingAgent】【临时会话】创建内存会话头。</summary>
    /// <param name="cwd">工作目录。</param>
    /// <param name="parentSession">可选来源会话路径。</param>
    /// <returns>新的会话头。</returns>
    private static CodingAgentTreeSessionHeader NewMemoryHeader(string cwd, string? parentSession = null) => new()
    {
        Type = "session", Version = CodingAgentTreeSessionStore.CurrentVersion,
        Id = UuidV7.Create(), Cwd = cwd, Timestamp = DateTimeOffset.UtcNow, ParentSession = parentSession
    };
}
