namespace Tau.AgentCore.Harness.Session;

public sealed class InMemorySessionStorage<TMetadata> : ISessionStorage<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly object _gate = new();
    private readonly TMetadata _metadata;
    private readonly List<SessionTreeEntry> _entries;
    private readonly List<LaneRecord> _records;
    private readonly Dictionary<string, SessionTreeEntry> _byId;
    private readonly Dictionary<string, long> _entrySequences;
    private readonly Dictionary<string, string> _labelsById;
    private readonly Dictionary<string, string?> _lanes;
    private readonly List<SessionLogItem> _log;
    private long _nextSequence;
    private string? _leafId;
    private string? _name;

    public InMemorySessionStorage(
        TMetadata? metadata = null,
        IEnumerable<SessionTreeEntry>? entries = null,
        IEnumerable<LaneRecord>? records = null)
    {
        _metadata = metadata ?? (TMetadata)(object)new SessionMetadata(UuidV7.Create(), CreateTimestamp());
        _entries = entries?.ToList() ?? [];
        _records = records?.ToList() ?? [];
        _byId = _entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        _entrySequences = [];
        _labelsById = [];
        _lanes = new Dictionary<string, string?>(StringComparer.Ordinal) { ["main"] = null };
        _log = [];

        foreach (var (entry, index) in _entries.Select((entry, index) => (entry, index)))
        {
            var sequence = entry.Sequence > 0 ? entry.Sequence : index + 1L;
            _entrySequences[entry.Id] = sequence;
            _nextSequence = Math.Max(_nextSequence, sequence);
            UpdateLabelCache(_labelsById, entry);
            _leafId = LeafIdAfterEntry(entry);
            if (entry.Lane is not null)
                _lanes[entry.Lane] = entry.Id;
            else
                _lanes["main"] = _leafId;
            _log.Add(new SessionEntryLogItem(sequence, entry));
        }

        foreach (var record in _records)
        {
            _nextSequence = Math.Max(_nextSequence, record.Sequence);
            _log.Add(new SessionRecordLogItem(record.Sequence, record));
        }
        _log.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        if (_leafId is not null && !_byId.ContainsKey(_leafId))
            throw new SessionException("invalid_session", $"Entry {_leafId} not found");
    }

    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_metadata);

    /// <summary>
    /// 返回下一条 entry 或 record 将使用的共享序号。
    /// </summary>
    /// <returns>下一个大于当前日志的序号。</returns>
    public long PeekNextSequence()
    {
        lock (_gate)
        {
            return _nextSequence + 1;
        }
    }

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leafId is not null && !_byId.ContainsKey(_leafId))
                throw new SessionException("invalid_session", $"Entry {_leafId} not found");

            return Task.FromResult(_leafId);
        }
    }

    public Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (leafId is not null && !_byId.ContainsKey(leafId))
                throw new SessionException("not_found", $"Entry {leafId} not found");

            var entry = new LeafSessionEntry(
                GenerateEntryId(_byId),
                _leafId,
                DateTimeOffset.UtcNow,
                leafId);
            AppendEntryCore(entry);
            _leafId = leafId;
            return Task.CompletedTask;
        }
    }

    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(GenerateEntryId(_byId));
        }
    }

    public Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AppendEntryCore(entry);
            return Task.CompletedTask;
        }
    }

    /// <summary>追加一条 lane record 并保留其序号顺序。</summary>
    /// <param name="record">待追加的 record。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task AppendRecordAsync(LaneRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_records.Any(item => item.Id.Equals(record.Id, StringComparison.Ordinal)) ||
                _byId.ContainsKey(record.Id))
                throw new SessionException("invalid_record", $"Duplicate record id '{record.Id}'.");
            if (record is OperationStartedRecord &&
                _records.OfType<OperationStartedRecord>().Any(started =>
                    started.Lane.Equals(record.Lane, StringComparison.Ordinal) &&
                    !_records.OfType<OperationFinishedRecord>().Any(finished =>
                        finished.Lane.Equals(record.Lane, StringComparison.Ordinal) &&
                        finished.RunId.Equals(started.Id, StringComparison.Ordinal))))
                throw new SessionException("storage", $"Lane {record.Lane} already has an open operation.");

            // 1. v4 storage 为每一条 entry/record 分配共享递增序号
            var sequence = record.Sequence > _nextSequence ? record.Sequence : _nextSequence + 1;
            _nextSequence = sequence;
            _records.Add(record with { Sequence = sequence });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 返回当前 session 的 lane 指针快照。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>lane 名称与叶节点 id 列表。</returns>
    public Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LanePointer>>(
                _lanes.Select(static pair => new LanePointer(pair.Key, pair.Value)).ToArray());
        }
    }

    /// <summary>
    /// 创建一个新的 lane，并将其初始叶节点设置为指定 entry。
    /// </summary>
    /// <param name="lane">新 lane 名称。</param>
    /// <param name="at">初始叶节点；为空表示从根开始。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task CreateLaneAsync(string lane, string? at = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_lanes.ContainsKey(lane))
                throw new SessionException("already_exists", $"Lane already exists: {lane}");
            ValidateTargetCore(at);
            var sequence = NextSequenceCore();
            _lanes[lane] = at;
            _log.Add(new SessionLaneLogItem(sequence, lane, at));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 移动已有 lane 的叶节点并记录 lane mutation。
    /// </summary>
    /// <param name="lane">要移动的 lane 名称。</param>
    /// <param name="to">新的叶节点；为空表示移动到根。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_lanes.ContainsKey(lane))
                throw new SessionException("invalid_lane", $"Lane not found: {lane}");
            ValidateTargetCore(to);
            var sequence = NextSequenceCore();
            _lanes[lane] = to;
            if (lane.Equals("main", StringComparison.Ordinal))
                _leafId = to;
            _log.Add(new SessionLaneLogItem(sequence, lane, to));
            return Task.CompletedTask;
        }
    }

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _byId.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(
                _entries.Where(entry => entry.Type == type).ToArray());
        }
    }

    /// <summary>
    /// 按 entry 查询条件返回 session 记录。
    /// </summary>
    /// <param name="query">类型、顺序、游标和数量过滤；为空表示返回全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>满足条件的 entry 列表。</returns>
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        EntryQuery? query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new EntryQuery();
        ValidateLimit(query.Limit);
        lock (_gate)
        {
            var indexed = _entries.Select(entry => (entry, sequence: _entrySequences[entry.Id]));
            if (query.Order == EntryOrder.NewestFirst)
                indexed = indexed.Reverse();

            var result = indexed
                .Where(item => (query.Type is null || item.entry.Type.Equals(query.Type, StringComparison.Ordinal)) &&
                               (query.CustomType is null || item.entry is CustomSessionEntry custom && custom.CustomType.Equals(query.CustomType, StringComparison.Ordinal)) &&
                               (!query.AfterSequence.HasValue || (query.Order == EntryOrder.OldestFirst
                                   ? item.sequence > query.AfterSequence.Value
                                   : item.sequence < query.AfterSequence.Value)))
                .Select(item => item.entry)
                .Take(query.Limit ?? int.MaxValue)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(result);
        }
    }

    /// <summary>
    /// 沿当前或指定 entry 的父链执行有界分支查询。
    /// </summary>
    /// <param name="query">entry 过滤和结果顺序。</param>
    /// <param name="bounds">起点以及 stopAtId/stopAtType 边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分支上的匹配 entry。</returns>
    public async Task<IReadOnlyList<SessionTreeEntry>> FindEntriesOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new EntryQuery();
        bounds ??= new BranchBounds();
        ValidateLimit(query.Limit);
        var startId = bounds.StartId ?? await GetLeafIdAsync(cancellationToken).ConfigureAwait(false);
        if (startId is null)
            return [];

        List<SessionTreeEntry> path;
        lock (_gate)
        {
            path = [];
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var currentId = startId;
            while (currentId is not null)
            {
                if (!visited.Add(currentId))
                    throw new SessionException("invalid_session", $"Session branch contains a cycle at {currentId}");
                if (!_byId.TryGetValue(currentId, out var current))
                    throw new SessionException("not_found", $"Entry {currentId} not found");
                path.Add(current);
                if (current.Id.Equals(bounds.StopAtId, StringComparison.Ordinal) ||
                    current.Type.Equals(bounds.StopAtType, StringComparison.Ordinal))
                    break;
                currentId = current.ParentId;
            }
        }

        if (query.Order == EntryOrder.OldestFirst)
            path.Reverse();

        var indexed = path.Select(entry => (entry, sequence: _entrySequences[entry.Id]));
        var filtered = indexed
            .Where(item => (query.Type is null || item.entry.Type.Equals(query.Type, StringComparison.Ordinal)) &&
                           (query.CustomType is null || item.entry is CustomSessionEntry custom && custom.CustomType.Equals(query.CustomType, StringComparison.Ordinal)) &&
                           (!query.AfterSequence.HasValue || (query.Order == EntryOrder.OldestFirst
                               ? item.sequence > query.AfterSequence.Value
                               : item.sequence < query.AfterSequence.Value)))
            .Select(item => item.entry)
            .Take(query.Limit ?? int.MaxValue)
            .ToArray();
        return filtered;
    }

    /// <summary>
    /// 按 lane、record 类型、操作 id 和操作意图筛选 record-log。
    /// </summary>
    /// <param name="query">record 查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>满足条件的 lane records。</returns>
    public Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(
        RecordQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new RecordQuery();
        ValidateLimit(query.Limit);
        lock (_gate)
        {
            IEnumerable<LaneRecord> records = query.Order == EntryOrder.OldestFirst
                ? _records.OrderBy(item => item.Sequence)
                : _records.OrderByDescending(item => item.Sequence);
            records = records.Where(record =>
                (query.Lane is null || record.Lane.Equals(query.Lane, StringComparison.Ordinal)) &&
                (query.Type is null || record.Type.Equals(query.Type, StringComparison.Ordinal)) &&
                (!query.AfterSequence.HasValue || record.Sequence > query.AfterSequence.Value) &&
                MatchesRunId(record, query.RunId) &&
                (query.OperationKind is null || record is OperationStartedRecord started && started.OperationKind.Equals(query.OperationKind, StringComparison.Ordinal)));
            return Task.FromResult<IReadOnlyList<LaneRecord>>(records.Take(query.Limit ?? int.MaxValue).ToArray());
        }
    }

    /// <summary>
    /// 返回指定 lane 中尚未对应 operation_finished 的操作开始记录。
    /// </summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="limit">最多返回数量；必须为正数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未完成操作，按开始序号从新到旧排列。</returns>
    public Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLimit(limit);
        lock (_gate)
        {
            var finished = _records.OfType<OperationFinishedRecord>()
                .Where(item => item.Lane.Equals(lane, StringComparison.Ordinal))
                .Select(item => item.RunId)
                .ToHashSet(StringComparer.Ordinal);
            var operations = _records.OfType<OperationStartedRecord>()
                .Where(item => item.Lane.Equals(lane, StringComparison.Ordinal) && !finished.Contains(item.Id))
                .OrderByDescending(item => item.Sequence)
                .Take(limit ?? int.MaxValue)
                .ToArray();
            return Task.FromResult<IReadOnlyList<OperationStartedRecord>>(operations);
        }
    }

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _labelsById.TryGetValue(id, out var label);
            return Task.FromResult(label);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(
        string? leafId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (leafId is null)
                return Task.FromResult<IReadOnlyList<SessionTreeEntry>>([]);

            var path = new List<SessionTreeEntry>();
            if (!_byId.TryGetValue(leafId, out var current))
                throw new SessionException("not_found", $"Entry {leafId} not found");

            while (current is not null)
            {
                path.Insert(0, current);
                if (current.ParentId is null)
                    break;

                if (!_byId.TryGetValue(current.ParentId, out current))
                    throw new SessionException("invalid_session", $"Entry {path[0].ParentId} not found");
            }

            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(path);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(_entries.ToArray());
        }
    }

    /// <summary>返回全局 session 名称。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前名称；未设置时为空。</returns>
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_name);
        }
    }

    /// <summary>设置或清除全局 session 名称。</summary>
    /// <param name="name">新名称；空值清除名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var sequence = NextSequenceCore();
            _name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            _log.Add(new SessionFactLogItem(sequence, "name", null, _name));
            return Task.CompletedTask;
        }
    }

    /// <summary>计算 session 的消息、token 和成本统计。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前统计快照。</returns>
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var usages = _records.OfType<UsageRecord>().Select(static item => item.Usage).Where(static usage => usage is not null).Select(static usage => usage!.Value).ToArray();
            var cached = usages.Sum(static usage => usage.CacheReadTokens ?? 0);
            var uncached = usages.Sum(static usage => usage.InputTokens + (usage.CacheWriteTokens ?? 0));
            var total = usages.Sum(static usage => usage.TotalTokens ?? usage.InputTokens + usage.OutputTokens);
            var cost = usages.Sum(static usage => usage.Cost?.Total ?? 0m);
            return Task.FromResult(new SessionStats(
                _entries.OfType<MessageSessionEntry>().Count(),
                cached,
                uncached,
                total,
                cost));
        }
    }

    /// <summary>设置或清除 entry 的全局 label。</summary>
    /// <param name="id">目标 entry id。</param>
    /// <param name="label">label 文本；空值清除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_byId.ContainsKey(id))
                throw new SessionException("not_found", $"Entry {id} not found");
            var normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            if (normalized is null)
                _labelsById.Remove(id);
            else
                _labelsById[id] = normalized;
            var sequence = NextSequenceCore();
            _log.Add(new SessionFactLogItem(sequence, "label", id, normalized));
            return Task.CompletedTask;
        }
    }

    /// <summary>按共享序号读取合并日志。</summary>
    /// <param name="afterSequence">独占序号游标。</param>
    /// <param name="limit">最多返回数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按旧到新排列的日志项。</returns>
    public Task<IReadOnlyList<SessionLogItem>> GetLogAsync(long? afterSequence = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLimit(limit);
        if (afterSequence is < 0)
            throw new SessionException("invalid_query", "afterSequence must be a non-negative integer");
        lock (_gate)
        {
            var result = _log
                .Where(item => !afterSequence.HasValue || item.Sequence > afterSequence.Value)
                .Take(limit ?? int.MaxValue)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SessionLogItem>>(result);
        }
    }

    private void AppendEntryCore(SessionTreeEntry entry)
    {
        if (_byId.ContainsKey(entry.Id) || _records.Any(record => record.Id.Equals(entry.Id, StringComparison.Ordinal)))
            throw new SessionException("already_exists", $"Session id already exists: {entry.Id}");
        if (entry.ParentId is not null && !_byId.ContainsKey(entry.ParentId))
            throw new SessionException("not_found", $"Entry {entry.ParentId} not found");
        if (entry.Lane is not null && !_lanes.ContainsKey(entry.Lane))
            throw new SessionException("invalid_lane", $"Lane not found: {entry.Lane}");
        var sequence = entry.Sequence > _nextSequence ? entry.Sequence : _nextSequence + 1;
        entry = entry with { Sequence = sequence };
        _nextSequence = sequence;
        _entries.Add(entry);
        _byId[entry.Id] = entry;
        _entrySequences[entry.Id] = sequence;
        UpdateLabelCache(_labelsById, entry);
        _leafId = LeafIdAfterEntry(entry);
        if (entry.Lane is not null)
        {
            _lanes[entry.Lane] = entry.Id;
        }
        else
        {
            _lanes["main"] = _leafId;
        }
        _log.Add(new SessionEntryLogItem(sequence, entry));
    }

    private long NextSequenceCore() => ++_nextSequence;

    private void ValidateTargetCore(string? targetId)
    {
        if (targetId is not null && !_byId.ContainsKey(targetId))
            throw new SessionException("not_found", $"Entry not found: {targetId}");
    }

    private static void UpdateLabelCache(IDictionary<string, string> labelsById, SessionTreeEntry entry)
    {
        if (entry is not LabelSessionEntry labelEntry)
            return;

        var label = labelEntry.Label?.Trim();
        if (!string.IsNullOrEmpty(label))
            labelsById[labelEntry.TargetId] = label;
        else
            labelsById.Remove(labelEntry.TargetId);
    }

    private static string? LeafIdAfterEntry(SessionTreeEntry entry) =>
        entry is LeafSessionEntry leaf ? leaf.TargetId : entry.Id;

    private static string GenerateEntryId(IReadOnlyDictionary<string, SessionTreeEntry> byId)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = UuidV7.Create()[..8];
            if (!byId.ContainsKey(id))
                return id;
        }

        return UuidV7.Create();
    }

    private static string CreateTimestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateLimit(int? limit)
    {
        if (limit is <= 0)
            throw new SessionException("invalid_query", "limit must be a positive integer");
    }

    private static bool MatchesRunId(LaneRecord record, string? runId)
    {
        if (runId is null)
            return true;
        return record switch
        {
            OperationStartedRecord started => started.Id.Equals(runId, StringComparison.Ordinal),
            OperationFinishedRecord finished => finished.RunId.Equals(runId, StringComparison.Ordinal),
            AbortRequestedRecord abort => abort.RunId.Equals(runId, StringComparison.Ordinal),
            StepAttemptRecord attempt => attempt.RunId.Equals(runId, StringComparison.Ordinal),
            ToolStartedRecord tool => tool.RunId.Equals(runId, StringComparison.Ordinal),
            QueueEnqueuedRecord queued => queued.RunId?.Equals(runId, StringComparison.Ordinal) == true,
            QueueCancelledRecord cancelled => cancelled.RunId?.Equals(runId, StringComparison.Ordinal) == true,
            WriteDeferredRecord deferred => deferred.RunId.Equals(runId, StringComparison.Ordinal),
            UsageRecord usage => usage.RunId?.Equals(runId, StringComparison.Ordinal) == true,
            _ => false
        };
    }

    /// <summary>应用从 JSONL 恢复的 lane mutation，并保留原始共享序号。</summary>
    /// <param name="sequence">mutation 共享序号。</param>
    /// <param name="lane">lane 名称。</param>
    /// <param name="leafId">lane 叶节点。</param>
    internal void ApplyLaneMutation(long sequence, string lane, string? leafId)
    {
        lock (_gate)
        {
            ValidateTargetCore(leafId);
            _lanes[lane] = leafId;
            if (lane.Equals("main", StringComparison.Ordinal))
                _leafId = leafId;
            _nextSequence = Math.Max(_nextSequence, sequence);
            _log.Add(new SessionLaneLogItem(sequence, lane, leafId));
            _log.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        }
    }

    /// <summary>应用从 JSONL 恢复的全局 fact mutation，并保留原始共享序号。</summary>
    /// <param name="sequence">mutation 共享序号。</param>
    /// <param name="fact">fact 类型。</param>
    /// <param name="targetId">label 目标 entry id。</param>
    /// <param name="value">名称或 label 值。</param>
    internal void ApplyFactMutation(long sequence, string fact, string? targetId, string? value)
    {
        lock (_gate)
        {
            if (fact.Equals("label", StringComparison.Ordinal))
            {
                if (targetId is null || !_byId.ContainsKey(targetId))
                    throw new SessionException("invalid_session", $"Entry {targetId} not found");
                if (string.IsNullOrWhiteSpace(value))
                    _labelsById.Remove(targetId);
                else
                    _labelsById[targetId] = value;
            }
            else if (fact.Equals("name", StringComparison.Ordinal))
            {
                _name = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            else
            {
                throw new SessionException("invalid_session", $"Unknown fact type '{fact}'");
            }

            _nextSequence = Math.Max(_nextSequence, sequence);
            _log.Add(new SessionFactLogItem(sequence, fact, targetId, value));
            _log.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        }
    }
}
