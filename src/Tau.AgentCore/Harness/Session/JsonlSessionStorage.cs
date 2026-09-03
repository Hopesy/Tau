using System.Text.Json;

namespace Tau.AgentCore.Harness.Session;

public sealed class JsonlSessionStorage : ISessionStorage<JsonlSessionMetadata>
{
    private readonly string _filePath;
    private readonly InMemorySessionStorage<JsonlSessionMetadata> _storage;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private JsonlSessionStorage(
        string filePath,
        JsonlSessionMetadata metadata,
        IEnumerable<SessionTreeEntry> entries,
        IEnumerable<LaneRecord>? records = null,
        IEnumerable<SessionLogItem>? mutations = null,
        bool laneLessEntriesMoveMain = true)
    {
        _filePath = filePath;
        if (metadata.SourceFormat == 4)
        {
            // v4 必须按文件共享序号回放，lane mutation 可能出现在 secondary lane entry 之前
            _storage = new InMemorySessionStorage<JsonlSessionMetadata>(metadata, laneLessEntriesMoveMain: false);
            var ordered = entries
                .Select(static entry => new SessionEntryLogItem(entry.Sequence, entry) as SessionLogItem)
                .Concat((records ?? []).Select(static record => new SessionRecordLogItem(record.Sequence, record)))
                .Concat(mutations ?? [])
                .OrderBy(static item => item.Sequence)
                .ToArray();
            foreach (var mutation in ordered)
            {
                switch (mutation)
                {
                    case SessionEntryLogItem entry:
                        _storage.ApplyEntryMutation(entry.Sequence, entry.Entry);
                        break;
                    case SessionRecordLogItem record:
                        _storage.ApplyRecordMutation(record.Sequence, record.Record);
                        break;
                    case SessionLaneLogItem lane:
                        _storage.ApplyLaneMutation(lane.Sequence, lane.Lane, lane.LeafId, requireConsecutiveSequence: true);
                        break;
                    case SessionFactLogItem fact:
                        _storage.ApplyFactMutation(fact.Sequence, fact.Fact, fact.TargetId, fact.Value, requireConsecutiveSequence: true);
                        break;
                }
            }
        }
        else
        {
            _storage = new InMemorySessionStorage<JsonlSessionMetadata>(metadata, entries, records, laneLessEntriesMoveMain);
            foreach (var mutation in mutations ?? [])
            {
                switch (mutation)
                {
                    case SessionLaneLogItem lane:
                        _storage.ApplyLaneMutation(lane.Sequence, lane.Lane, lane.LeafId);
                        break;
                    case SessionFactLogItem fact:
                        _storage.ApplyFactMutation(fact.Sequence, fact.Fact, fact.TargetId, fact.Value);
                        break;
                }
            }
        }
    }

    public static async Task<JsonlSessionStorage> CreateAsync(
        string filePath,
        string cwd,
        string sessionId,
        string? parentSessionPath = null,
        string? parentSessionId = null,
        JsonElement? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createdAt)
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var header = new JsonlSessionHeader(
            "session",
            4,
            sessionId,
            timestamp,
            cwd,
            parentSessionPath)
        {
            ParentSessionId = parentSessionId,
            LegacyParentSessionPath = parentSessionPath
        };
        if (parentSessionId is not null && parentSessionPath is not null)
            throw new ArgumentException("v4 header cannot contain both parentSessionId and parentSessionPath.", nameof(parentSessionPath));
        if (metadata is { } metadataValue && metadataValue.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("v4 header metadata must be a JSON object.", nameof(metadata));
        header = header with { Metadata = metadata };
        await File.WriteAllTextAsync(
            fullPath,
            JsonlSessionSerialization.SerializeV4Header(
                sessionId,
                createdAt,
                cwd,
                parentSessionId: parentSessionId,
                legacyParentSessionPath: parentSessionPath,
                metadata: metadata),
            cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(fullPath, ToMetadata(header, fullPath), []);
    }

    /// <summary>
    /// 保留旧版参数顺序的 JSONL session 创建入口，避免把取消令牌误当作 parentSessionId。
    /// </summary>
    /// <param name="filePath">session 文件路径。</param>
    /// <param name="cwd">session 工作目录。</param>
    /// <param name="sessionId">session id。</param>
    /// <param name="parentSessionPath">旧版父 session 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已创建的 JSONL session storage。</returns>
    public static Task<JsonlSessionStorage> CreateAsync(
        string filePath,
        string cwd,
        string sessionId,
        string? parentSessionPath,
        CancellationToken cancellationToken) =>
        CreateAsync(filePath, cwd, sessionId, parentSessionPath, null, null, cancellationToken);

    public static async Task<JsonlSessionStorage> OpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(
            loaded.FilePath,
            ToMetadata(loaded.Header, loaded.FilePath),
            loaded.Entries,
            loaded.Records,
            loaded.Mutations,
            laneLessEntriesMoveMain: loaded.Header.Version != 4);
    }

    public static async Task<JsonlSessionMetadata> LoadMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        try
        {
            await using var stream = File.OpenRead(fullPath);
            using var reader = new StreamReader(stream);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(line))
                return ToMetadata(JsonlSessionSerialization.ParseHeader(line, fullPath), fullPath);
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }

        throw new SessionException("invalid_session", $"Invalid JSONL session file {fullPath}: missing session header");
    }

    public Task<JsonlSessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        _storage.GetMetadataAsync(cancellationToken);

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLeafIdAsync(cancellationToken);

    public async Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        if (leafId is not null && await _storage.GetEntryAsync(leafId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {leafId} not found");

        var entry = new LeafSessionEntry(
            await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
            await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
            DateTimeOffset.UtcNow,
            leafId);
        await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) =>
        _storage.CreateEntryIdAsync(cancellationToken);

    public async Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // v4 本地追加属于 main lane；只有导入 entry 才省略 lane 字段
            var persistedEntry = entry with
            {
                Sequence = _storage.PeekNextSequence(),
                Lane = entry.Lane ?? "main"
            };
            var line = JsonlSessionSerialization.SerializeV4Entry(persistedEntry);
            await using var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _storage.AppendEntryAsync(persistedEntry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>追加一条 lane record 到当前打开的 storage 视图。</summary>
    /// <param name="record">待追加 record。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>记录以 v4 mutation 行追加，重新打开 session 后仍可恢复。</remarks>
    public async Task AppendRecordAsync(LaneRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persistedRecord = record with { Sequence = _storage.PeekNextSequence() };
            var line = JsonlSessionSerialization.SerializeRecord(persistedRecord);
            await using var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _storage.AppendRecordAsync(persistedRecord, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetEntryAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        string type,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesAsync(type, cancellationToken);

    /// <summary>按查询条件读取 JSONL session entries。</summary>
    /// <param name="query">entry 查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 entry 列表。</returns>
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        EntryQuery? query,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesAsync(query, cancellationToken);

    /// <summary>沿 JSONL session 的父链读取分支 entries。</summary>
    /// <param name="query">entry 查询条件。</param>
    /// <param name="bounds">分支起止边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的分支 entries。</returns>
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesOnBranchAsync(query, bounds, cancellationToken);

    /// <summary>读取 JSONL 存储中的 lane records。</summary>
    /// <param name="query">record 查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 records；旧格式没有 record-log 时为空。</returns>
    public Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(
        RecordQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindRecordsAsync(query, cancellationToken);

    /// <summary>读取 JSONL session 的 lane 指针。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 lane 指针快照。</returns>
    public Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLanesAsync(cancellationToken);

    /// <summary>创建 lane 并持久化 lane mutation。</summary>
    /// <param name="lane">新 lane 名称。</param>
    /// <param name="at">初始叶节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CreateLaneAsync(string lane, string? at = null, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = _storage.PeekNextSequence();
            await AppendLineAsync(JsonlSessionSerialization.SerializeLaneMutation(sequence, lane, at), cancellationToken).ConfigureAwait(false);
            await _storage.CreateLaneAsync(lane, at, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>移动 lane 并持久化 lane mutation。</summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="to">新的叶节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = _storage.PeekNextSequence();
            await AppendLineAsync(JsonlSessionSerialization.SerializeLaneMutation(sequence, lane, to), cancellationToken).ConfigureAwait(false);
            await _storage.MoveLaneAsync(lane, to, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读取全局 session 名称。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前名称。</returns>
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) =>
        _storage.GetNameAsync(cancellationToken);

    /// <summary>设置全局 session 名称并持久化 fact mutation。</summary>
    /// <param name="name">名称；空值清除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetNameAsync(string? name, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = _storage.PeekNextSequence();
            await AppendLineAsync(JsonlSessionSerialization.SerializeFactMutation(sequence, "name", name: name), cancellationToken).ConfigureAwait(false);
            await _storage.SetNameAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读取 session 统计信息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统计快照。</returns>
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _storage.GetStatsAsync(cancellationToken);

    /// <summary>设置或清除 entry label，并持久化 fact mutation。</summary>
    /// <param name="id">目标 entry id。</param>
    /// <param name="label">label 文本；空值清除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = _storage.PeekNextSequence();
            await AppendLineAsync(JsonlSessionSerialization.SerializeFactMutation(sequence, "label", targetId: id, label: label), cancellationToken).ConfigureAwait(false);
            await _storage.SetLabelAsync(id, label, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读取 session 合并顺序日志。</summary>
    /// <param name="afterSequence">独占序号游标。</param>
    /// <param name="limit">最多返回数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按序号排列的日志项。</returns>
    public Task<IReadOnlyList<SessionLogItem>> GetLogAsync(long? afterSequence = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _storage.GetLogAsync(afterSequence, limit, cancellationToken);

    /// <summary>读取指定 lane 的未完成操作。</summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="limit">最多返回数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未完成操作列表。</returns>
    public Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindOpenOperationsAsync(lane, limit, cancellationToken);

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetLabelAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(
        string? leafId,
        CancellationToken cancellationToken = default) =>
        _storage.GetPathToRootAsync(leafId, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetEntriesAsync(cancellationToken);

    private static async Task<(string FilePath, JsonlSessionHeader Header, IReadOnlyList<SessionTreeEntry> Entries, IReadOnlyList<LaneRecord> Records, IReadOnlyList<SessionLogItem> Mutations)> LoadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }

        var lines = content.Split('\n');
        var hasTrailingNewline = content.EndsWith("\n", StringComparison.Ordinal);
        if (hasTrailingNewline)
            lines = lines[..^1];
        var nonEmptyLines = lines.Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (nonEmptyLines.Length == 0)
            throw new SessionException("invalid_session", $"Invalid JSONL session file {fullPath}: missing session header");

        var header = JsonlSessionSerialization.ParseHeader(nonEmptyLines[0], fullPath);
        var entries = new List<SessionTreeEntry>();
        var records = new List<LaneRecord>();
        var mutations = new List<SessionLogItem>();
        var replay = header.Version == 4 ? new ReplayValidationState() : null;
        for (var i = 1; i < nonEmptyLines.Length; i++)
        {
            try
            {
                JsonDocument? lineDocument = null;
                try
                {
                    lineDocument = JsonDocument.Parse(nonEmptyLines[i]);
                }
                catch (JsonException)
                {
                    // 让 ParseEntry 生成统一的 invalid_entry 错误信息
                }

                using (lineDocument)
                {
                    var root = lineDocument?.RootElement;
                    if (header.Version == 4 &&
                        (root is not { ValueKind: JsonValueKind.Object } ||
                         !root.Value.TryGetProperty("kind", out var mutationKind) ||
                         mutationKind.ValueKind != JsonValueKind.String ||
                         mutationKind.GetString() is not ("entry" or "record" or "lane" or "fact")))
                        throw InvalidMutation(fullPath, i + 1, "has unknown mutation kind");
                    if (root is { ValueKind: JsonValueKind.Object } &&
                        root.Value.TryGetProperty("kind", out var kind) &&
                        kind.ValueKind == JsonValueKind.String &&
                        kind.GetString() == "record")
                    {
                        var record = JsonlSessionSerialization.ParseRecord(nonEmptyLines[i], fullPath, i + 1);
                        records.Add(record);
                        replay?.ApplyRecord(record, fullPath, i + 1);
                    }
                    else if (root is { ValueKind: JsonValueKind.Object } &&
                             root.Value.TryGetProperty("kind", out kind) &&
                             kind.ValueKind == JsonValueKind.String &&
                             kind.GetString() == "lane")
                    {
                        var sequence = RequiredMutationSequence(root.Value, fullPath, i + 1);
                        var lane = RequiredMutationString(root.Value, "lane", fullPath, i + 1);
                        var leafId = RequiredNullableMutationString(root.Value, "leafId", fullPath, i + 1);
                        mutations.Add(new SessionLaneLogItem(sequence, lane, leafId));
                        replay?.ApplyLane(sequence, lane, leafId, fullPath, i + 1);
                    }
                    else if (root is { ValueKind: JsonValueKind.Object } &&
                             root.Value.TryGetProperty("kind", out kind) &&
                             kind.ValueKind == JsonValueKind.String &&
                             kind.GetString() == "fact")
                    {
                        var sequence = RequiredMutationSequence(root.Value, fullPath, i + 1);
                        var fact = RequiredMutationString(root.Value, "fact", fullPath, i + 1);
                        if (fact is not ("name" or "label"))
                            throw InvalidMutation(fullPath, i + 1, "has unsupported fact type");
                        var targetId = fact == "label"
                            ? RequiredMutationString(root.Value, "targetId", fullPath, i + 1)
                            : null;
                        var value = OptionalMutationString(root.Value, fact, fullPath, i + 1);
                        mutations.Add(new SessionFactLogItem(sequence, fact, targetId, value));
                        replay?.ApplyFact(sequence, fact, targetId, value, fullPath, i + 1);
                    }
                    else
                    {
                        if (header.Version == 4 &&
                            root!.Value.TryGetProperty("lane", out var laneValue) &&
                            laneValue.ValueKind != JsonValueKind.String)
                        {
                            // v4 imported entries may omit lane, but an explicit null or non-string is invalid
                            throw InvalidMutation(fullPath, i + 1, "has invalid lane");
                        }
                        var entry = JsonlSessionSerialization.ParseEntry(nonEmptyLines[i], fullPath, i + 1);
                        entries.Add(entry);
                        replay?.ApplyEntry(entry, root!.Value.TryGetProperty("lane", out var explicitLane), fullPath, i + 1);
                    }
                }
            }
            catch (SessionException ex) when (!hasTrailingNewline &&
                                               i == nonEmptyLines.Length - 1 &&
                                               ex.Code == "invalid_entry" &&
                                               ex.InnerException is JsonException &&
                                               LooksLikeTruncatedJson(nonEmptyLines[i]))
            {
                // 丢弃未完成的最后一行，并原子修复为有效前缀
                var validPrefix = string.Join("\n", nonEmptyLines.Take(i)) + "\n";
                var tempPath = fullPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, validPrefix, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, fullPath, overwrite: true);
                break;
            }
        }

        return (fullPath, header, entries, records, mutations);
    }

    /// <summary>按文件顺序校验 v4 mutation 的共享序号、引用关系和 lane 约束。</summary>
    private sealed class ReplayValidationState
    {
        private long _nextSequence;
        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _entryIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _lanes = new(StringComparer.Ordinal) { ["main"] = null };

        /// <summary>校验并登记一条 entry mutation。</summary>
        /// <param name="entry">entry 数据。</param>
        /// <param name="hasLane">原始 JSON 是否包含 lane 字段。</param>
        /// <param name="filePath">文件路径。</param>
        /// <param name="lineNumber">行号。</param>
        public void ApplyEntry(SessionTreeEntry entry, bool hasLane, string filePath, int lineNumber)
        {
            EnsureSequence(entry.Sequence, filePath, lineNumber);
            if (!_usedIds.Add(entry.Id)) throw InvalidMutation(filePath, lineNumber, $"contains duplicate id {entry.Id}");
            if (entry.ParentId is not null && !_entryIds.Contains(entry.ParentId))
                throw InvalidMutation(filePath, lineNumber, $"references missing parent {entry.ParentId}");
            if (hasLane)
            {
                if (entry.Lane is null || !_lanes.TryGetValue(entry.Lane, out var leafId))
                    throw InvalidMutation(filePath, lineNumber, $"references missing lane {entry.Lane}");
                if (!string.Equals(entry.ParentId, leafId, StringComparison.Ordinal))
                    throw InvalidMutation(filePath, lineNumber, "does not chain to the lane leaf");
                _lanes[entry.Lane] = entry.Id;
            }
            _entryIds.Add(entry.Id);
        }

        /// <summary>校验并登记一条 record mutation。</summary>
        /// <param name="record">record 数据。</param>
        /// <param name="filePath">文件路径。</param>
        /// <param name="lineNumber">行号。</param>
        public void ApplyRecord(LaneRecord record, string filePath, int lineNumber)
        {
            EnsureSequence(record.Sequence, filePath, lineNumber);
            if (!_lanes.ContainsKey(record.Lane))
                throw InvalidMutation(filePath, lineNumber, $"references missing lane {record.Lane}");
            if (!_usedIds.Add(record.Id)) throw InvalidMutation(filePath, lineNumber, $"contains duplicate id {record.Id}");
        }

        /// <summary>校验并登记一条 lane mutation。</summary>
        /// <param name="sequence">共享序号。</param>
        /// <param name="lane">lane 名称。</param>
        /// <param name="leafId">目标叶节点。</param>
        /// <param name="filePath">文件路径。</param>
        /// <param name="lineNumber">行号。</param>
        public void ApplyLane(long sequence, string lane, string? leafId, string filePath, int lineNumber)
        {
            EnsureSequence(sequence, filePath, lineNumber);
            if (leafId is not null && !_entryIds.Contains(leafId))
                throw InvalidMutation(filePath, lineNumber, $"references missing lane target {leafId}");
            _lanes[lane] = leafId;
        }

        /// <summary>校验并登记一条 fact mutation。</summary>
        /// <param name="sequence">共享序号。</param>
        /// <param name="fact">fact 类型。</param>
        /// <param name="targetId">label 目标。</param>
        /// <param name="value">fact 值。</param>
        /// <param name="filePath">文件路径。</param>
        /// <param name="lineNumber">行号。</param>
        public void ApplyFact(long sequence, string fact, string? targetId, string? value, string filePath, int lineNumber)
        {
            EnsureSequence(sequence, filePath, lineNumber);
            if (fact == "label" && (targetId is null || !_entryIds.Contains(targetId)))
                throw InvalidMutation(filePath, lineNumber, $"references missing label target {targetId}");
        }

        /// <summary>校验 v4 mutation 是否使用连续正整数序号。</summary>
        /// <param name="sequence">待校验序号。</param>
        /// <param name="filePath">文件路径。</param>
        /// <param name="lineNumber">行号。</param>
        private void EnsureSequence(long sequence, string filePath, int lineNumber)
        {
            if (sequence <= 0 || sequence != _nextSequence + 1)
                throw InvalidMutation(filePath, lineNumber, $"has non-consecutive seq {sequence}");
            _nextSequence = sequence;
        }
    }

    /// <summary>校验 lane/fact mutation 的正整数序列号。</summary>
    /// <param name="root">mutation JSON 对象。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    /// <returns>合法的共享序列号。</returns>
    private static long RequiredMutationSequence(JsonElement root, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty("seq", out var value) || !value.TryGetInt64(out var sequence) || sequence <= 0)
            throw InvalidMutation(filePath, lineNumber, "has invalid seq");
        return sequence;
    }

    /// <summary>读取 mutation 中的非空字符串字段。</summary>
    /// <param name="root">mutation JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    /// <returns>字段字符串。</returns>
    private static string RequiredMutationString(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidMutation(filePath, lineNumber, $"is missing or invalid {name}");
        return value.GetString()!;
    }

    /// <summary>读取允许为 null 的 mutation 字符串字段。</summary>
    /// <param name="root">mutation JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    /// <returns>字段字符串或 null。</returns>
    private static string? OptionalMutationString(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidMutation(filePath, lineNumber, $"has invalid {name}");
        return value.GetString();
    }

    /// <summary>读取必须出现但允许为 null 的 mutation 字符串字段。</summary>
    /// <param name="root">mutation JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    /// <returns>字段字符串或 null。</returns>
    private static string? RequiredNullableMutationString(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value))
            throw InvalidMutation(filePath, lineNumber, $"is missing {name}");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidMutation(filePath, lineNumber, $"has invalid {name}");
        return value.GetString();
    }

    /// <summary>生成统一的 mutation 格式错误。</summary>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    /// <param name="message">错误描述。</param>
    /// <returns>invalid_entry 异常。</returns>
    private static SessionException InvalidMutation(string filePath, int lineNumber, string message) =>
        new("invalid_entry", $"Invalid JSONL session file {filePath}: line {lineNumber} {message}");

    /// <summary>向 JSONL 文件追加一行并确保写入落盘。</summary>
    /// <param name="line">包含换行符的 JSON 行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough | FileOptions.Asynchronous);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断最后一行是否呈现明显的 JSON 结构截断形态。
    /// </summary>
    /// <param name="line">待判断的 JSONL 行文本。</param>
    /// <returns>行以对象或数组开始但没有对应闭合符号时返回 true。</returns>
    private static bool LooksLikeTruncatedJson(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        var closing = trimmed[0] == '{' ? '}' : ']';
        return trimmed[^1] != closing;
    }

    private static JsonlSessionMetadata ToMetadata(JsonlSessionHeader header, string filePath) =>
        new(
            header.Id,
            header.Timestamp,
            header.Cwd,
            filePath,
            // v4 的 ParentSession 是兼容性投影，不能把 parentSessionId 当成旧路径
            header.Version == 4 ? header.LegacyParentSessionPath : header.ParentSession)
        {
            SourceFormat = header.Version,
            ParentSessionId = header.ParentSessionId,
            LegacyParentSessionPath = header.LegacyParentSessionPath,
            Metadata = header.Metadata,
            ModifiedAt = File.Exists(filePath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero).ToUnixTimeMilliseconds()
                : 0
        };
}
