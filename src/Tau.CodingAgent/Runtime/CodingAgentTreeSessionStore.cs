using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentTreeSessionSnapshot(
    IReadOnlyList<ChatMessage> Messages,
    string? Provider,
    string? Model,
    string? Name,
    string SessionId,
    string FilePath,
    string? LeafId,
    int EntryCount)
{
    public CodingAgentSessionSnapshot ToFlatSnapshot() => new(Messages, Provider, Model, Name);
}

public sealed record CodingAgentTreeSessionSummary(
    string FilePath,
    string SessionId,
    string? LeafId,
    int EntryCount,
    int TotalMessageCount,
    int BranchEntryCount,
    int BranchMessageCount,
    int BranchPointCount,
    int LabelCount,
    string Cwd,
    string? ParentSession);

public enum CodingAgentTreeFilterMode
{
    Default,
    NoTools,
    UserOnly,
    LabeledOnly,
    All
}

public sealed record CodingAgentTreeFormatOptions(
    int MaxEntries = 24,
    CodingAgentTreeFilterMode FilterMode = CodingAgentTreeFilterMode.Default,
    bool ShowLabelTimestamps = false,
    string? SearchQuery = null);

public sealed class CodingAgentTreeSessionController
{
    private int _persistedMessageCount;
    private string? _persistedProvider;
    private string? _persistedModel;
    private string? _persistedName;

    public CodingAgentTreeSessionController(CodingAgentTreeSessionStore store)
    {
        Store = store;
        MarkSnapshot(Store.LoadCurrentBranchSnapshot());
    }

    public CodingAgentTreeSessionStore Store { get; private set; }

    public string Path => Store.Path;

    public static CodingAgentTreeSessionController OpenOrCreate(string? path = null) =>
        new(new CodingAgentTreeSessionStore(path));

    public CodingAgentTreeSessionSnapshot LoadSnapshot()
    {
        var snapshot = Store.LoadCurrentBranchSnapshot();
        MarkSnapshot(snapshot);
        return snapshot;
    }

    public void SyncFromRunner(ICodingAgentRunner runner)
    {
        var messages = runner.Messages;
        if (messages.Count < _persistedMessageCount)
        {
            Store.StartNew(runner.Model.Provider, runner.Model.Id, runner.SessionName);
            _persistedMessageCount = 0;
            _persistedProvider = runner.Model.Provider;
            _persistedModel = runner.Model.Id;
            _persistedName = NormalizeName(runner.SessionName);
        }

        if (!string.Equals(_persistedProvider, runner.Model.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_persistedModel, runner.Model.Id, StringComparison.OrdinalIgnoreCase))
        {
            Store.AppendModelChange(runner.Model.Provider, runner.Model.Id);
            _persistedProvider = runner.Model.Provider;
            _persistedModel = runner.Model.Id;
        }

        var sessionName = NormalizeName(runner.SessionName);
        if (!string.Equals(_persistedName, sessionName, StringComparison.Ordinal))
        {
            Store.AppendSessionInfo(sessionName, runner.Model.Provider, runner.Model.Id);
            _persistedName = sessionName;
        }

        if (messages.Count > _persistedMessageCount)
        {
            Store.AppendMessages(messages, _persistedMessageCount);
            _persistedMessageCount = messages.Count;
        }
    }

    public void StartNewFromRunner(ICodingAgentRunner runner)
    {
        Store.StartNew(runner.Model.Provider, runner.Model.Id, runner.SessionName);
        _persistedMessageCount = 0;
        _persistedProvider = runner.Model.Provider;
        _persistedModel = runner.Model.Id;
        _persistedName = NormalizeName(runner.SessionName);
    }

    public void ReplaceWithRunnerSession(ICodingAgentRunner runner)
    {
        Store.StartNew(runner.Model.Provider, runner.Model.Id, runner.SessionName);
        Store.AppendMessages(runner.Messages, 0);
        _persistedMessageCount = runner.Messages.Count;
        _persistedProvider = runner.Model.Provider;
        _persistedModel = runner.Model.Id;
        _persistedName = NormalizeName(runner.SessionName);
    }

    public string RecordCompaction(ICodingAgentRunner runner, CodingAgentCompactionResult result)
    {
        var id = Store.AppendCompaction(
            result.Summary,
            result.FirstKeptEntryId,
            result.TokensBefore,
            result.FromHook);
        _persistedMessageCount = runner.Messages.Count;
        _persistedProvider = runner.Model.Provider;
        _persistedModel = runner.Model.Id;
        _persistedName = NormalizeName(runner.SessionName);
        return id;
    }

    public CodingAgentTreeSessionSnapshot Branch(string entryId)
    {
        Store.Branch(entryId);
        var snapshot = Store.LoadCurrentBranchSnapshot();
        MarkSnapshot(snapshot);
        return snapshot;
    }

    public CodingAgentTreeSessionSnapshot? CloneCurrentBranch()
    {
        var current = Store.LoadCurrentBranchSnapshot();
        if (current.Messages.Count == 0)
        {
            return null;
        }

        var clonePath = Store.ExportCurrentBranchToNewSession();
        Store = new CodingAgentTreeSessionStore(clonePath);
        var snapshot = Store.LoadCurrentBranchSnapshot();
        MarkSnapshot(snapshot);
        return snapshot;
    }

    public CodingAgentTreeSessionSnapshot Resume(string path)
    {
        Store = new CodingAgentTreeSessionStore(path);
        var snapshot = Store.LoadCurrentBranchSnapshot();
        MarkSnapshot(snapshot);
        return snapshot;
    }

    public string ExportCurrentBranch(string path) => Store.ExportCurrentBranch(path);

    public string ExportCurrentBranchText() => Store.ExportCurrentBranchText();

    public string AppendLabelChange(string entryId, string? label) => Store.AppendLabelChange(entryId, label);

    public string? GetLabel(string entryId) => Store.GetLabel(entryId);

    public CodingAgentTreeSessionSummary GetSummary() => Store.GetSummary();

    public string FormatTree(int maxEntries = 24) => Store.FormatTree(maxEntries);

    public string FormatTree(CodingAgentTreeFormatOptions options) => Store.FormatTree(options);

    private void MarkSnapshot(CodingAgentTreeSessionSnapshot snapshot)
    {
        _persistedMessageCount = snapshot.Messages.Count;
        _persistedProvider = snapshot.Provider;
        _persistedModel = snapshot.Model;
        _persistedName = NormalizeName(snapshot.Name);
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

public sealed class CodingAgentTreeSessionStore
{
    public const int CurrentVersion = 3;
    private const string SessionType = "session";
    private const string MessageType = "message";
    private const string ModelChangeType = "model_change";
    private const string SessionInfoType = "session_info";
    private const string LabelType = "label";
    private const string CompactionType = "compaction";

    private readonly string _path;
    private readonly string _cwd;

    public CodingAgentTreeSessionStore(string? path = null, string? cwd = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : System.IO.Path.GetFullPath(path);
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        EnsureSessionFile();
    }

    public string Path => _path;

    public static bool HasExplicitTreeSessionPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE")))
            {
                return true;
            }

            var sessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
            return IsJsonlPath(sessionFile);
        }
    }

    public static bool IsJsonlPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(System.IO.Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase);

    public static string GetDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        var sessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        if (IsJsonlPath(sessionFile))
        {
            return System.IO.Path.GetFullPath(sessionFile!);
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, ".tau", "coding-agent-session.jsonl");
    }

    public static string? FindMostRecentSession()
    {
        var candidates = new List<string>();
        var defaultPath = GetDefaultPath();
        if (File.Exists(defaultPath) && IsValidSessionFileWithEntries(defaultPath))
        {
            candidates.Add(defaultPath);
        }

        foreach (var sessionsDir in GetSessionSearchDirectories())
        {
            if (Directory.Exists(sessionsDir))
            {
                candidates.AddRange(Directory.EnumerateFiles(sessionsDir, "*.jsonl").Where(IsValidSessionFileWithEntries));
            }
        }

        return candidates
            .Select(path => new { Path = path, Modified = File.GetLastWriteTimeUtc(path) })
            .OrderByDescending(file => file.Modified)
            .FirstOrDefault()
            ?.Path;
    }

    public CodingAgentTreeSessionSnapshot LoadCurrentBranchSnapshot()
    {
        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        return BuildSnapshot(state, branch);
    }

    public CodingAgentTreeSessionSummary GetSummary()
    {
        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        return new CodingAgentTreeSessionSummary(
            _path,
            state.Header.Id,
            state.LeafId,
            state.Entries.Count,
            state.Entries.Count(static entry => entry.Type == MessageType),
            branch.Count,
            branch.Count(static entry => entry.Type == MessageType),
            state.BranchPointCount,
            state.LabelsById.Count,
            state.Header.Cwd,
            state.Header.ParentSession);
    }

    public IReadOnlyList<string> AppendMessages(IReadOnlyList<ChatMessage> messages, int startIndex)
    {
        if (startIndex < 0 || startIndex > messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        var state = ReadState();
        var ids = state.EntryIds;
        var parentId = state.LeafId;
        var appended = new List<string>();
        for (var i = startIndex; i < messages.Count; i++)
        {
            var id = CreateEntryId(ids);
            var entry = new CodingAgentTreeSessionEntry
            {
                Type = MessageType,
                Id = id,
                ParentId = parentId,
                Timestamp = DateTimeOffset.UtcNow,
                Message = CodingAgentSessionStore.FromMessage(messages[i])
            };
            AppendEntry(entry);
            appended.Add(id);
            ids.Add(id);
            parentId = id;
        }

        return appended;
    }

    public string AppendModelChange(string provider, string model)
    {
        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = ModelChangeType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            Provider = provider,
            Model = model
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendSessionInfo(string? name, string? provider = null, string? model = null)
    {
        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = SessionInfoType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            Name = NormalizeName(name),
            Provider = provider,
            Model = model
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendLabelChange(string entryId, string? label)
    {
        var state = ReadState();
        var target = state.ResolveEntryId(entryId);
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = LabelType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            TargetId = target.Id,
            Label = NormalizeName(label)
        };
        AppendEntry(entry);
        return id;
    }

    public string? GetLabel(string entryId)
    {
        var state = ReadState();
        var target = state.ResolveEntryId(entryId);
        return state.LabelsById.TryGetValue(target.Id, out var label) ? label : null;
    }

    public string AppendCompaction(string summary, string? firstKeptEntryId, int tokensBefore, bool fromHook = false)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Compaction summary cannot be empty.", nameof(summary));
        }

        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = CompactionType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = summary.Trim(),
            FirstKeptEntryId = firstKeptEntryId ?? string.Empty,
            TokensBefore = Math.Max(0, tokensBefore),
            FromHook = fromHook ? true : null
        };
        AppendEntry(entry);
        return id;
    }

    public string StartNew(string provider, string model, string? name)
    {
        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = SessionInfoType,
            Id = id,
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "new",
            Name = NormalizeName(name),
            Provider = provider,
            Model = model
        };
        AppendEntry(entry);
        return id;
    }

    public string Branch(string entryId)
    {
        var state = ReadState();
        var target = state.ResolveEntryId(entryId);
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = SessionInfoType,
            Id = id,
            ParentId = target.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "branch"
        };
        AppendEntry(entry);
        return id;
    }

    public string ExportCurrentBranch(string path)
    {
        var exportPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(exportPath, false);
        WriteCurrentBranch(writer);
        return exportPath;
    }

    public string ExportCurrentBranchText()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);
        WriteCurrentBranch(writer);
        return builder.ToString();
    }

    public string ExportCurrentBranchToNewSession()
    {
        var directory = GetCloneSessionDirectory();
        Directory.CreateDirectory(directory);

        string path;
        do
        {
            path = System.IO.Path.Combine(directory, $"tau-session-{CreateSessionId()}.jsonl");
        }
        while (File.Exists(path));

        return ExportCurrentBranch(path);
    }

    private void WriteCurrentBranch(TextWriter writer)
    {
        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        var header = new CodingAgentTreeSessionHeader
        {
            Type = SessionType,
            Version = CurrentVersion,
            Id = CreateSessionId(),
            Timestamp = DateTimeOffset.UtcNow,
            Cwd = _cwd,
            ParentSession = _path
        };

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? parentId = null;
        writer.WriteLine(JsonSerializer.Serialize(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader));
        foreach (var original in branch)
        {
            var id = CreateEntryId(ids);
            var targetId = original.TargetId is not null && idMap.TryGetValue(original.TargetId, out var remappedTarget)
                ? remappedTarget
                : original.TargetId;
            var firstKeptEntryId = original.FirstKeptEntryId is not null &&
                                   idMap.TryGetValue(original.FirstKeptEntryId, out var remappedFirstKept)
                ? remappedFirstKept
                : original.FirstKeptEntryId;
            var entry = original.Clone(id, parentId, targetId, firstKeptEntryId);
            writer.WriteLine(JsonSerializer.Serialize(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry));
            ids.Add(id);
            idMap[original.Id] = id;
            parentId = id;
        }
    }

    public string FormatTree(int maxEntries = 24) =>
        FormatTree(new CodingAgentTreeFormatOptions(maxEntries));

    public string FormatTree(CodingAgentTreeFormatOptions options)
    {
        var state = ReadState();
        var branchIds = state.GetBranch(state.LeafId)
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summary = GetSummary();
        var lines = new List<string>
        {
            $"tree: file {summary.FilePath}, session {ShortId(summary.SessionId)}, leaf {ShortId(summary.LeafId)}, entries {summary.EntryCount}, messages {summary.TotalMessageCount}, branch messages {summary.BranchMessageCount}, branches {summary.BranchPointCount}, labels {summary.LabelCount}, cwd {summary.Cwd}{FormatParentSession(summary.ParentSession)}, filter {FormatFilterMode(options.FilterMode)}{FormatSearchQuery(options.SearchQuery)}"
        };

        if (state.Entries.Count == 0)
        {
            lines.Add("tree is empty");
            return string.Join('\n', lines);
        }

        var filteredEntries = state.Entries
            .Where(entry => ShouldShowEntry(entry, state, options.FilterMode))
            .Where(entry => MatchesSearch(entry, state, options.SearchQuery))
            .ToArray();
        var visibleEntries = filteredEntries
            .Skip(Math.Max(0, filteredEntries.Length - Math.Max(1, options.MaxEntries)));
        var wroteEntry = false;
        foreach (var entry in visibleEntries)
        {
            wroteEntry = true;
            var marker = entry.Id.Equals(state.LeafId, StringComparison.OrdinalIgnoreCase)
                ? ">"
                : branchIds.Contains(entry.Id)
                    ? "*"
                    : " ";
            var depth = Math.Min(GetDepth(entry, state.ById), 16);
            var indent = new string(' ', depth * 2);
            var label = state.LabelsById.TryGetValue(entry.Id, out var resolvedLabel)
                ? $" [{resolvedLabel}]"
                : string.Empty;
            var labelTime = options.ShowLabelTimestamps &&
                            state.LabelTimestampsById.TryGetValue(entry.Id, out var timestamp)
                ? $" @{timestamp}"
                : string.Empty;
            lines.Add($"{marker} {indent}{ShortId(entry.Id)} <- {ShortId(entry.ParentId)} {DescribeEntry(entry)}{label}{labelTime}");
        }

        if (!wroteEntry)
        {
            lines.Add("tree has no entries matching filter");
        }

        return string.Join('\n', lines);
    }

    private void EnsureSessionFile()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_path) && new FileInfo(_path).Length > 0)
        {
            return;
        }

        var header = new CodingAgentTreeSessionHeader
        {
            Type = SessionType,
            Version = CurrentVersion,
            Id = CreateSessionId(),
            Timestamp = DateTimeOffset.UtcNow,
            Cwd = _cwd
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader) + "\n");
    }

    private TreeState ReadState()
    {
        EnsureSessionFile();

        CodingAgentTreeSessionHeader? header = null;
        var entries = new List<CodingAgentTreeSessionEntry>();
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (header is null)
            {
                header = JsonSerializer.Deserialize(line, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader);
                if (header is null || header.Type != SessionType || string.IsNullOrWhiteSpace(header.Id))
                {
                    throw new JsonException("invalid coding agent tree session header");
                }

                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize(line, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Type) && !string.IsNullOrWhiteSpace(entry.Id))
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Match the upstream JSONL reader: a malformed trailing line must not make the whole session unrecoverable.
            }
        }

        if (header is null)
        {
            throw new JsonException("missing coding agent tree session header");
        }

        return new TreeState(header, entries);
    }

    private CodingAgentTreeSessionSnapshot BuildSnapshot(TreeState state, IReadOnlyList<CodingAgentTreeSessionEntry> branch)
    {
        string? provider = null;
        string? model = null;
        string? name = null;
        var messages = new List<ChatMessage>();

        foreach (var entry in branch)
        {
            switch (entry.Type)
            {
                case MessageType when entry.Message is not null:
                    var message = CodingAgentSessionStore.ToMessage(entry.Message);
                    if (message is not null)
                    {
                        messages.Add(message);
                    }

                    break;

                case ModelChangeType:
                    provider = entry.Provider ?? provider;
                    model = entry.Model ?? model;
                    break;

                case SessionInfoType:
                    if (string.Equals(entry.Action, "branch", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    name = NormalizeName(entry.Name);
                    provider = entry.Provider ?? provider;
                    model = entry.Model ?? model;
                    break;
                case LabelType:
                    break;

                case CompactionType:
                    messages.Clear();
                    messages.Add(CodingAgentCompactionMessages.CreateSummaryMessage(entry.Summary ?? string.Empty));
                    break;
            }
        }

        return new CodingAgentTreeSessionSnapshot(
            messages,
            provider,
            model,
            name,
            state.Header.Id,
            _path,
            state.LeafId,
            state.Entries.Count);
    }

    private void AppendEntry(CodingAgentTreeSessionEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
        File.AppendAllText(_path, json + "\n");
    }

    private static bool IsValidSessionFile(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return false;
            }

            var header = JsonSerializer.Deserialize(firstLine, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader);
            return header?.Type == SessionType && !string.IsNullOrWhiteSpace(header.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsValidSessionFileWithEntries(string path)
    {
        if (!IsValidSessionFile(path))
        {
            return false;
        }

        try
        {
            return File.ReadLines(path).Skip(1).Any(static line => !string.IsNullOrWhiteSpace(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CreateEntryId(ISet<string> existingIds)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!existingIds.Contains(id))
            {
                return id;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string CreateSessionId() => $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    private string GetCloneSessionDirectory()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        return System.IO.Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory,
            "coding-agent-sessions");
    }

    private static IReadOnlyList<string> GetSessionSearchDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            System.IO.Path.Combine(Environment.CurrentDirectory, ".tau", "coding-agent-sessions")
        };

        var defaultDirectory = System.IO.Path.GetDirectoryName(GetDefaultPath());
        if (!string.IsNullOrWhiteSpace(defaultDirectory))
        {
            directories.Add(System.IO.Path.Combine(defaultDirectory, "coding-agent-sessions"));
        }

        return [.. directories];
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string ShortId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "root" : id.Length <= 8 ? id : id[..8];

    private static bool ShouldShowEntry(
        CodingAgentTreeSessionEntry entry,
        TreeState state,
        CodingAgentTreeFilterMode filterMode)
    {
        if (filterMode == CodingAgentTreeFilterMode.All)
        {
            return true;
        }

        if (filterMode == CodingAgentTreeFilterMode.LabeledOnly)
        {
            return state.LabelsById.ContainsKey(entry.Id);
        }

        if (filterMode == CodingAgentTreeFilterMode.UserOnly)
        {
            return entry.Type == MessageType &&
                   string.Equals(entry.Message?.Role, "user", StringComparison.OrdinalIgnoreCase);
        }

        var isSettingsEntry = entry.Type is LabelType or ModelChangeType or SessionInfoType;
        if (isSettingsEntry)
        {
            return false;
        }

        if (IsAssistantToolOnlyMessage(entry, state.LeafId))
        {
            return false;
        }

        if (filterMode == CodingAgentTreeFilterMode.NoTools)
        {
            return entry.Type != MessageType ||
                   !string.Equals(entry.Message?.Role, "toolResult", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool MatchesSearch(
        CodingAgentTreeSessionEntry entry,
        TreeState state,
        string? searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return true;
        }

        var haystack = BuildSearchText(entry, state);
        var tokens = searchQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSearchText(CodingAgentTreeSessionEntry entry, TreeState state)
    {
        var parts = new List<string>
        {
            entry.Type,
            entry.Id
        };

        if (!string.IsNullOrWhiteSpace(entry.ParentId))
        {
            parts.Add(entry.ParentId);
        }

        if (state.LabelsById.TryGetValue(entry.Id, out var label))
        {
            parts.Add(label);
        }

        if (!string.IsNullOrWhiteSpace(entry.Provider))
        {
            parts.Add(entry.Provider);
        }

        if (!string.IsNullOrWhiteSpace(entry.Model))
        {
            parts.Add(entry.Model);
        }

        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            parts.Add(entry.Name);
        }

        if (!string.IsNullOrWhiteSpace(entry.Action))
        {
            parts.Add(entry.Action);
        }

        if (!string.IsNullOrWhiteSpace(entry.TargetId))
        {
            parts.Add(entry.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(entry.Label))
        {
            parts.Add(entry.Label);
        }

        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            parts.Add(entry.Summary);
        }

        if (entry.Message is { } message)
        {
            parts.Add(message.Role);
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                parts.Add(message.ToolCallId);
            }

            foreach (var content in message.Content)
            {
                parts.Add(content.Type);
                if (!string.IsNullOrWhiteSpace(content.Text))
                {
                    parts.Add(content.Text);
                }

                if (!string.IsNullOrWhiteSpace(content.Thinking))
                {
                    parts.Add(content.Thinking);
                }

                if (!string.IsNullOrWhiteSpace(content.Id))
                {
                    parts.Add(content.Id);
                }

                if (!string.IsNullOrWhiteSpace(content.Name))
                {
                    parts.Add(content.Name);
                }

                if (!string.IsNullOrWhiteSpace(content.Arguments))
                {
                    parts.Add(content.Arguments);
                }
            }
        }

        return string.Join(' ', parts);
    }

    private static bool IsAssistantToolOnlyMessage(CodingAgentTreeSessionEntry entry, string? leafId)
    {
        var message = entry.Message;
        if (entry.Type != MessageType ||
            message is null ||
            !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Equals(leafId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !message.Content.Any(static content =>
            content.Type == "text" && !string.IsNullOrWhiteSpace(content.Text));
    }

    private static int GetDepth(
        CodingAgentTreeSessionEntry entry,
        IReadOnlyDictionary<string, CodingAgentTreeSessionEntry> byId)
    {
        var depth = 0;
        var parentId = entry.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId) &&
               byId.TryGetValue(parentId, out var parent) &&
               depth < 128)
        {
            depth++;
            parentId = parent.ParentId;
        }

        return depth;
    }

    private static string FormatFilterMode(CodingAgentTreeFilterMode filterMode) =>
        filterMode switch
        {
            CodingAgentTreeFilterMode.NoTools => "no-tools",
            CodingAgentTreeFilterMode.UserOnly => "user-only",
            CodingAgentTreeFilterMode.LabeledOnly => "labeled-only",
            CodingAgentTreeFilterMode.All => "all",
            _ => "default"
        };

    private static string FormatSearchQuery(string? searchQuery) =>
        string.IsNullOrWhiteSpace(searchQuery) ? string.Empty : $", search {searchQuery.Trim()}";

    private static string FormatParentSession(string? parentSession) =>
        string.IsNullOrWhiteSpace(parentSession) ? string.Empty : $", parent {parentSession}";

    private static string DescribeEntry(CodingAgentTreeSessionEntry entry)
    {
        return entry.Type switch
        {
            MessageType when entry.Message is not null => $"message {entry.Message.Role} {PreviewMessage(entry.Message)}",
            ModelChangeType => $"model {entry.Provider}/{entry.Model}",
            CompactionType => $"compaction {entry.TokensBefore.GetValueOrDefault()} tokens {PreviewText(entry.Summary)}",
            LabelType => $"label {ShortId(entry.TargetId)} {NormalizeName(entry.Label) ?? "clear"}",
            SessionInfoType when string.Equals(entry.Action, "branch", StringComparison.OrdinalIgnoreCase) => "branch",
            SessionInfoType when entry.Action is not null => $"{entry.Action} name {NormalizeName(entry.Name) ?? "none"}",
            SessionInfoType => $"session name {NormalizeName(entry.Name) ?? "none"}",
            _ => entry.Type
        };
    }

    private static string PreviewMessage(CodingAgentSessionMessage message)
    {
        var text = string.Join(
            " ",
            message.Content
                .Where(static content => content.Type == "text" && !string.IsNullOrWhiteSpace(content.Text))
                .Select(static content => content.Text!.Trim()));
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length <= 72 ? text : text[..72] + "...";
    }

    private static string PreviewText(string? text)
    {
        text = NormalizeName(text);
        if (text is null)
        {
            return string.Empty;
        }

        return text.Length <= 72 ? text : text[..72] + "...";
    }

    private sealed class TreeState
    {
        public TreeState(CodingAgentTreeSessionHeader header, IReadOnlyList<CodingAgentTreeSessionEntry> entries)
        {
            Header = header;
            Entries = entries;
            ById = entries.ToDictionary(static entry => entry.Id, StringComparer.OrdinalIgnoreCase);
            EntryIds = entries.Select(static entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            LeafId = entries.Count == 0 ? null : entries[^1].Id;
            var labels = BuildLabelsById(entries);
            LabelsById = labels.Labels;
            LabelTimestampsById = labels.LabelTimestamps;
            BranchPointCount = entries
                .Where(static entry => entry.ParentId is not null)
                .GroupBy(static entry => entry.ParentId, StringComparer.OrdinalIgnoreCase)
                .Count(static group => group.Count() > 1);
        }

        public CodingAgentTreeSessionHeader Header { get; }
        public IReadOnlyList<CodingAgentTreeSessionEntry> Entries { get; }
        public IReadOnlyDictionary<string, CodingAgentTreeSessionEntry> ById { get; }
        public ISet<string> EntryIds { get; }
        public string? LeafId { get; }
        public IReadOnlyDictionary<string, string> LabelsById { get; }
        public IReadOnlyDictionary<string, string> LabelTimestampsById { get; }
        public int BranchPointCount { get; }

        public CodingAgentTreeSessionEntry ResolveEntryId(string idOrPrefix)
        {
            if (ById.TryGetValue(idOrPrefix, out var exact))
            {
                return exact;
            }

            var matches = Entries
                .Where(entry => entry.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException($"session entry '{idOrPrefix}' not found"),
                _ => throw new InvalidOperationException($"session entry '{idOrPrefix}' is ambiguous")
            };
        }

        public IReadOnlyList<CodingAgentTreeSessionEntry> GetBranch(string? leafId)
        {
            var path = new List<CodingAgentTreeSessionEntry>();
            if (leafId is null || !ById.TryGetValue(leafId, out var current))
            {
                return path;
            }

            while (current is not null)
            {
                path.Add(current);
                current = current.ParentId is not null && ById.TryGetValue(current.ParentId, out var parent)
                    ? parent
                    : null;
            }

            path.Reverse();
            return path;
        }

        private static (IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> LabelTimestamps) BuildLabelsById(
            IReadOnlyList<CodingAgentTreeSessionEntry> entries)
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var timestamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Type != LabelType || string.IsNullOrWhiteSpace(entry.TargetId))
                {
                    continue;
                }

                var label = NormalizeName(entry.Label);
                if (label is null)
                {
                    labels.Remove(entry.TargetId);
                    timestamps.Remove(entry.TargetId);
                }
                else
                {
                    labels[entry.TargetId] = label;
                    timestamps[entry.TargetId] = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz");
                }
            }

            return (labels, timestamps);
        }
    }
}

internal sealed class CodingAgentTreeSessionHeader
{
    public string Type { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Cwd { get; init; } = string.Empty;
    public string? ParentSession { get; init; }
}

internal sealed class CodingAgentTreeSessionEntry
{
    public string Type { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public CodingAgentSessionMessage? Message { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Name { get; init; }
    public string? Action { get; init; }
    public string? TargetId { get; init; }
    public string? Label { get; init; }
    public string? Summary { get; init; }
    public string? FirstKeptEntryId { get; init; }
    public int? TokensBefore { get; init; }
    public bool? FromHook { get; init; }

    public CodingAgentTreeSessionEntry Clone(
        string id,
        string? parentId,
        string? targetId = null,
        string? firstKeptEntryId = null) =>
        new()
        {
            Type = Type,
            Id = id,
            ParentId = parentId,
            Timestamp = Timestamp,
            Message = Message,
            Provider = Provider,
            Model = Model,
            Name = Name,
            Action = Action,
            TargetId = targetId ?? TargetId,
            Label = Label,
            Summary = Summary,
            FirstKeptEntryId = firstKeptEntryId ?? FirstKeptEntryId,
            TokensBefore = TokensBefore,
            FromHook = FromHook
        };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentTreeSessionHeader))]
[JsonSerializable(typeof(CodingAgentTreeSessionEntry))]
internal sealed partial class CodingAgentTreeSessionJsonContext : JsonSerializerContext;
