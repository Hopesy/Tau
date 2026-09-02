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
        IEnumerable<SessionLogItem>? mutations = null)
    {
        _filePath = filePath;
        _storage = new InMemorySessionStorage<JsonlSessionMetadata>(metadata, entries, records);
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

    public static async Task<JsonlSessionStorage> CreateAsync(
        string filePath,
        string cwd,
        string sessionId,
        string? parentSessionPath = null,
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
            LegacyParentSessionPath = parentSessionPath
        };
        await File.WriteAllTextAsync(
            fullPath,
            JsonlSessionSerialization.SerializeV4Header(
                sessionId,
                createdAt,
                cwd,
                legacyParentSessionPath: parentSessionPath),
            cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(fullPath, ToMetadata(header, fullPath), []);
    }

    public static async Task<JsonlSessionStorage> OpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(loaded.FilePath, ToMetadata(loaded.Header, loaded.FilePath), loaded.Entries, loaded.Records, loaded.Mutations);
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
            var persistedEntry = entry with { Sequence = _storage.PeekNextSequence() };
            var line = JsonlSessionSerialization.SerializeEntry(persistedEntry) + "\n";
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
                    if (root is { ValueKind: JsonValueKind.Object } &&
                        root.Value.TryGetProperty("kind", out var kind) &&
                        kind.ValueKind == JsonValueKind.String &&
                        kind.GetString() == "record")
                    {
                        records.Add(JsonlSessionSerialization.ParseRecord(nonEmptyLines[i], fullPath, i + 1));
                    }
                    else if (root is { ValueKind: JsonValueKind.Object } &&
                             root.Value.TryGetProperty("kind", out kind) &&
                             kind.ValueKind == JsonValueKind.String &&
                             kind.GetString() == "lane")
                    {
                        var sequence = root.Value.GetProperty("seq").GetInt64();
                        var lane = root.Value.GetProperty("lane").GetString() ?? throw new SessionException("invalid_entry", $"line {i + 1} is missing lane");
                        var leafId = root.Value.TryGetProperty("leafId", out var leaf) && leaf.ValueKind != JsonValueKind.Null ? leaf.GetString() : null;
                        mutations.Add(new SessionLaneLogItem(sequence, lane, leafId));
                    }
                    else if (root is { ValueKind: JsonValueKind.Object } &&
                             root.Value.TryGetProperty("kind", out kind) &&
                             kind.ValueKind == JsonValueKind.String &&
                             kind.GetString() == "fact")
                    {
                        var sequence = root.Value.GetProperty("seq").GetInt64();
                        var fact = root.Value.GetProperty("fact").GetString() ?? throw new SessionException("invalid_entry", $"line {i + 1} is missing fact");
                        var targetId = root.Value.TryGetProperty("targetId", out var target) && target.ValueKind != JsonValueKind.Null ? target.GetString() : null;
                        var value = root.Value.TryGetProperty(fact.Equals("name", StringComparison.Ordinal) ? "name" : "label", out var factValue) && factValue.ValueKind != JsonValueKind.Null ? factValue.GetString() : null;
                        mutations.Add(new SessionFactLogItem(sequence, fact, targetId, value));
                    }
                    else
                    {
                        entries.Add(JsonlSessionSerialization.ParseEntry(nonEmptyLines[i], fullPath, i + 1));
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
            header.ParentSession)
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
