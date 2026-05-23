using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

public sealed record CodingAgentTreeViewItem(
    string EntryId,
    string DisplayLine,
    bool IsCurrentLeaf,
    bool IsOnBranch,
    string? ParentEntryId = null,
    int Depth = 0,
    string EntryType = "");

public sealed record CodingAgentForkMessage(string EntryId, string Text);

public sealed record CodingAgentTreeFoldState(IReadOnlyList<string> CollapsedEntryIds);

public sealed record CodingAgentCompactionRetentionOptions(
    int KeepRecentTokens = 20_000,
    int KeepRecentMessages = 4)
{
    private const int DefaultCompactionRetainedMessageCount = 4;
    private const int DefaultCompactionRetainedTokenCount = 20_000;
    private const string CompactionRetainedMessageCountEnvironmentVariable = "TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES";
    private const string CompactionRetainedTokenCountEnvironmentVariable = "TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS";

    public static CodingAgentCompactionRetentionOptions FromEnvironment() =>
        new(
            ReadNonNegativeEnvironmentInt(
                CompactionRetainedTokenCountEnvironmentVariable,
                DefaultCompactionRetainedTokenCount),
            ReadNonNegativeEnvironmentInt(
                CompactionRetainedMessageCountEnvironmentVariable,
                DefaultCompactionRetainedMessageCount));

    private static int ReadNonNegativeEnvironmentInt(string name, int defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultValue;
        }

        return int.TryParse(configured, out var parsed)
            ? Math.Max(0, parsed)
            : defaultValue;
    }
}

public sealed class CodingAgentTreeSessionController
{
    private readonly CodingAgentCompactionRetentionOptions _compactionRetention;
    private int _persistedMessageCount;
    private string? _persistedProvider;
    private string? _persistedModel;
    private string? _persistedName;

    public CodingAgentTreeSessionController(
        CodingAgentTreeSessionStore store,
        CodingAgentCompactionRetentionOptions? compactionRetention = null)
    {
        Store = store;
        _compactionRetention = compactionRetention ?? CodingAgentCompactionRetentionOptions.FromEnvironment();
        MarkSnapshot(Store.LoadCurrentBranchSnapshot());
    }

    public CodingAgentTreeSessionStore Store { get; private set; }

    public string Path => Store.Path;

    public static CodingAgentTreeSessionController OpenOrCreate(
        string? path = null,
        CodingAgentCompactionRetentionOptions? compactionRetention = null) =>
        new(new CodingAgentTreeSessionStore(path), compactionRetention);

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

    public void StartNewFromRunner(ICodingAgentRunner runner, string? parentSession = null)
    {
        if (!string.IsNullOrWhiteSpace(parentSession))
        {
            Store.RewriteHeader(parentSession.Trim());
        }

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
        var firstKeptEntryId = string.IsNullOrWhiteSpace(result.FirstKeptEntryId)
            ? Store.FindCompactionFirstKeptEntryId(
                _compactionRetention.KeepRecentTokens,
                _compactionRetention.KeepRecentMessages)
            : result.FirstKeptEntryId;
        var turnPrefixSummary = Store.CreateSplitTurnPrefixSummary(firstKeptEntryId);
        var id = Store.AppendCompaction(
            result.Summary,
            firstKeptEntryId,
            result.TokensBefore,
            result.FromHook,
            turnPrefixSummary);

        var snapshot = Store.LoadCurrentBranchSnapshot();
        runner.RestoreSession(snapshot.ToFlatSnapshot());
        MarkSnapshot(snapshot);
        return id;
    }

    public IReadOnlyList<ChatMessage> CollectBranchSummaryMessages(string entryId) =>
        Store.CollectBranchSummaryMessages(entryId);

    public CodingAgentTreeSessionSnapshot BranchWithSummary(
        string entryId,
        CodingAgentBranchSummaryResult result)
    {
        Store.BranchWithSummary(
            entryId,
            result.Summary,
            result.ReadFiles,
            result.ModifiedFiles,
            result.FromHook);
        var snapshot = Store.LoadCurrentBranchSnapshot();
        MarkSnapshot(snapshot);
        return snapshot;
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

    public string AppendAutoRetryStart(int attempt, int maxAttempts, int delayMs, string errorMessage) =>
        Store.AppendAutoRetryStart(attempt, maxAttempts, delayMs, errorMessage);

    public string AppendAutoRetryEnd(bool success, int attempt, string? finalError = null) =>
        Store.AppendAutoRetryEnd(success, attempt, finalError);

    public string AppendTreeFoldState(IReadOnlySet<string> collapsedEntryIds) =>
        Store.AppendTreeFoldState(collapsedEntryIds);

    public string? GetLabel(string entryId) => Store.GetLabel(entryId);

    public CodingAgentTreeSessionSummary GetSummary() => Store.GetSummary();

    public CodingAgentTreeFoldState? LoadTreeFoldState() => Store.LoadTreeFoldState();

    public string FormatTree(int maxEntries = 24) => Store.FormatTree(maxEntries);

    public string FormatTree(CodingAgentTreeFormatOptions options) => Store.FormatTree(options);

    public string FormatMetadata(string? entryId = null) => Store.FormatMetadata(entryId);

    public IReadOnlyList<CodingAgentTreeViewItem> EnumerateView(CodingAgentTreeFormatOptions options) =>
        Store.EnumerateView(options);

    public IReadOnlyList<CodingAgentForkMessage> GetUserMessagesForForking() =>
        Store.GetUserMessagesForForking();

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
    private const string BranchSummaryType = "branch_summary";
    private const string AutoRetryStartType = "auto_retry_start";
    private const string AutoRetryEndType = "auto_retry_end";
    private const string TreeStateType = "tree_state";

    private readonly string _path;
    private readonly string _cwd;
    private readonly TauSecretRedactor _secretRedactor;

    public CodingAgentTreeSessionStore(string? path = null, string? cwd = null, TauSecretRedactor? secretRedactor = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : System.IO.Path.GetFullPath(path);
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        _secretRedactor = secretRedactor ??
            TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.CodingAgentEnvironmentVariable);
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

    public string AppendCompaction(
        string summary,
        string? firstKeptEntryId,
        int tokensBefore,
        bool fromHook = false,
        string? turnPrefixSummary = null)
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
            FirstKeptEntryId = NormalizeName(firstKeptEntryId) ?? string.Empty,
            TokensBefore = Math.Max(0, tokensBefore),
            FromHook = fromHook ? true : null,
            IsSplitTurn = string.IsNullOrWhiteSpace(turnPrefixSummary) ? null : true,
            TurnPrefixSummary = NormalizeName(turnPrefixSummary)
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendBranchSummary(
        string? branchFromId,
        string summary,
        IReadOnlyList<string>? readFiles = null,
        IReadOnlyList<string>? modifiedFiles = null,
        bool fromHook = false)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Branch summary cannot be empty.", nameof(summary));
        }

        var state = ReadState();
        var targetId = string.IsNullOrWhiteSpace(branchFromId)
            ? null
            : state.ResolveEntryId(branchFromId).Id;
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = BranchSummaryType,
            Id = id,
            ParentId = targetId,
            Timestamp = DateTimeOffset.UtcNow,
            FromId = targetId ?? "root",
            Summary = summary.Trim(),
            ReadFiles = NormalizeStringList(readFiles),
            ModifiedFiles = NormalizeStringList(modifiedFiles),
            FromHook = fromHook ? true : null
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendAutoRetryStart(int attempt, int maxAttempts, int delayMs, string errorMessage)
    {
        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = AutoRetryStartType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            Attempt = Math.Max(1, attempt),
            MaxAttempts = Math.Max(1, maxAttempts),
            DelayMs = Math.Max(0, delayMs),
            ErrorMessage = NormalizeName(errorMessage) ?? "Unknown error"
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendAutoRetryEnd(bool success, int attempt, string? finalError = null)
    {
        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = AutoRetryEndType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            Success = success,
            Attempt = Math.Max(0, attempt),
            FinalError = NormalizeName(finalError)
        };
        AppendEntry(entry);
        return id;
    }

    public string AppendTreeFoldState(IReadOnlySet<string> collapsedEntryIds)
    {
        ArgumentNullException.ThrowIfNull(collapsedEntryIds);

        var state = ReadState();
        var id = CreateEntryId(state.EntryIds);
        var entry = new CodingAgentTreeSessionEntry
        {
            Type = TreeStateType,
            Id = id,
            ParentId = state.LeafId,
            Timestamp = DateTimeOffset.UtcNow,
            CollapsedEntryIds = NormalizeStringList([.. collapsedEntryIds])
        };
        AppendEntry(entry);
        return id;
    }

    public CodingAgentTreeFoldState? LoadTreeFoldState()
    {
        var state = ReadState();
        return state.TreeFoldState;
    }

    public string? FindCompactionFirstKeptEntryId(int retainRecentTokenCount, int retainRecentMessageCount)
    {
        if (retainRecentTokenCount <= 0 && retainRecentMessageCount <= 0)
        {
            return null;
        }

        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        var boundaryIndex = -1;
        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i].Type == CompactionType)
            {
                boundaryIndex = i;
                break;
            }
        }

        var messageEntries = branch
            .Skip(boundaryIndex + 1)
            .Where(static entry => entry.Type == MessageType && entry.Message is not null)
            .ToArray();

        var tokenCutPoint = FindTokenRetentionCutPoint(messageEntries, retainRecentTokenCount);
        if (!string.IsNullOrWhiteSpace(tokenCutPoint))
        {
            return tokenCutPoint;
        }

        if (retainRecentMessageCount <= 0)
        {
            return null;
        }

        if (messageEntries.Length <= retainRecentMessageCount)
        {
            return null;
        }

        return messageEntries[^retainRecentMessageCount].Id;
    }

    public string? CreateSplitTurnPrefixSummary(string? firstKeptEntryId)
    {
        if (string.IsNullOrWhiteSpace(firstKeptEntryId))
        {
            return null;
        }

        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        var firstKeptIndex = -1;
        for (var i = 0; i < branch.Count; i++)
        {
            if (branch[i].Id.Equals(firstKeptEntryId, StringComparison.OrdinalIgnoreCase))
            {
                firstKeptIndex = i;
                break;
            }
        }

        if (firstKeptIndex <= 0 || branch[firstKeptIndex].Type != MessageType)
        {
            return null;
        }

        var boundaryStart = 0;
        for (var i = firstKeptIndex - 1; i >= 0; i--)
        {
            if (branch[i].Type == CompactionType)
            {
                boundaryStart = i + 1;
                break;
            }
        }

        var turnStartIndex = -1;
        for (var i = firstKeptIndex; i >= boundaryStart; i--)
        {
            var entryMessage = branch[i].Message;
            if (branch[i].Type != MessageType || entryMessage is null)
            {
                continue;
            }

            if (string.Equals(entryMessage.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                turnStartIndex = i;
                break;
            }
        }

        if (turnStartIndex < boundaryStart || turnStartIndex >= firstKeptIndex)
        {
            return null;
        }

        var prefixEntries = branch
            .Skip(turnStartIndex)
            .Take(firstKeptIndex - turnStartIndex)
            .Where(static entry => entry.Type == MessageType && entry.Message is not null)
            .ToArray();
        if (prefixEntries.Length == 0)
        {
            return null;
        }

        return BuildSplitTurnPrefixSummary(prefixEntries, branch[firstKeptIndex]);
    }

    private static string? FindTokenRetentionCutPoint(
        IReadOnlyList<CodingAgentTreeSessionEntry> messageEntries,
        int retainRecentTokenCount)
    {
        if (retainRecentTokenCount <= 0)
        {
            return null;
        }

        var accumulatedTokens = 0;
        for (var i = messageEntries.Count - 1; i >= 0; i--)
        {
            var message = CodingAgentSessionStore.ToMessage(messageEntries[i].Message!);
            if (message is null)
            {
                continue;
            }

            accumulatedTokens += CodingAgentTokenEstimator.Estimate([message]);
            if (accumulatedTokens >= retainRecentTokenCount)
            {
                return messageEntries[i].Id;
            }
        }

        return null;
    }

    private static string BuildSplitTurnPrefixSummary(
        IReadOnlyList<CodingAgentTreeSessionEntry> prefixEntries,
        CodingAgentTreeSessionEntry firstKeptEntry)
    {
        var builder = new StringBuilder();
        var originalRequest = prefixEntries
            .Select(static entry => CodingAgentSessionStore.ToMessage(entry.Message!))
            .OfType<UserMessage>()
            .Select(PreviewMessage)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));

        builder.AppendLine("## Original Request");
        builder.AppendLine(string.IsNullOrWhiteSpace(originalRequest) ? "- unavailable" : originalRequest);
        builder.AppendLine();
        builder.AppendLine("## Early Progress");

        foreach (var entry in prefixEntries.Take(8))
        {
            var message = CodingAgentSessionStore.ToMessage(entry.Message!);
            if (message is null)
            {
                continue;
            }

            builder.Append("- ")
                .Append(DescribeRole(message))
                .Append(": ")
                .AppendLine(PreviewMessage(message));
        }

        if (prefixEntries.Count > 8)
        {
            builder.Append("- ... ")
                .Append(prefixEntries.Count - 8)
                .AppendLine(" earlier prefix messages omitted");
        }

        builder.AppendLine();
        builder.AppendLine("## Context for Suffix");
        builder.Append("- Retained suffix starts at ")
            .Append(DescribeEntry(firstKeptEntry))
            .Append(" (")
            .Append(ShortId(firstKeptEntry.Id))
            .AppendLine(").");

        return builder.ToString().Trim();
    }

    private static string DescribeRole(ChatMessage message) =>
        message switch
        {
            UserMessage => "user",
            AssistantMessage => "assistant",
            ToolResultMessage => "tool result",
            _ => message.Role
        };

    private static string PreviewMessage(ChatMessage message)
    {
        var content = message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage toolResult => toolResult.Content,
            _ => []
        };
        var text = string.Join(
                " ",
                content.Select(static block => block switch
                {
                    TextContent text => text.Text,
                    ThinkingContent thinking => thinking.Thinking,
                    ToolCallContent toolCall => $"tool call {toolCall.Name} {toolCall.Arguments}",
                    ImageContent image => $"image {image.MimeType}",
                    _ => block.Type
                }))
            .ReplaceLineEndings(" ")
            .Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "(no text content)";
        }

        return text.Length <= 220 ? text : string.Concat(text.AsSpan(0, 217), "...");
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

    public void RewriteHeader(string? parentSession = null)
    {
        var state = ReadState();
        var header = new CodingAgentTreeSessionHeader
        {
            Type = SessionType,
            Version = state.Header.Version <= 0 ? CurrentVersion : state.Header.Version,
            Id = state.Header.Id,
            Timestamp = state.Header.Timestamp,
            Cwd = string.IsNullOrWhiteSpace(state.Header.Cwd) ? _cwd : state.Header.Cwd,
            ParentSession = NormalizeName(parentSession) ?? state.Header.ParentSession
        };

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        using (var writer = new StreamWriter(tempPath, false))
        {
            writer.WriteLine(SerializeJsonlLine(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader));
            foreach (var entry in state.Entries)
            {
                writer.WriteLine(SerializeJsonlLine(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry));
            }
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        File.Move(tempPath, _path);
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

    public string BranchWithSummary(
        string entryId,
        string summary,
        IReadOnlyList<string>? readFiles = null,
        IReadOnlyList<string>? modifiedFiles = null,
        bool fromHook = false)
    {
        var state = ReadState();
        var target = state.ResolveEntryId(entryId);
        return AppendBranchSummary(target.Id, summary, readFiles, modifiedFiles, fromHook);
    }

    public IReadOnlyList<ChatMessage> CollectBranchSummaryMessages(string targetEntryId)
    {
        var state = ReadState();
        var target = state.ResolveEntryId(targetEntryId);
        var entries = CollectEntriesForBranchSummary(state, state.LeafId, target.Id);
        return BuildBranchSummaryMessages(entries);
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
        writer.WriteLine(SerializeJsonlLine(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader));
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
            writer.WriteLine(SerializeJsonlLine(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry));
            ids.Add(id);
            idMap[original.Id] = id;
            parentId = id;
        }
    }

    public string FormatTree(int maxEntries = 24) =>
        FormatTree(new CodingAgentTreeFormatOptions(maxEntries));

    public string FormatMetadata(string? entryId = null)
    {
        var state = ReadState();
        var branch = state.GetBranch(state.LeafId);
        var branchIds = branch
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            $"metadata: file {_path}, session {state.Header.Id}, leaf {FormatEntryId(state.LeafId)}",
            $"cwd: {state.Header.Cwd}",
            $"parent session: {FormatOptionalValue(state.Header.ParentSession)}",
            $"counts: entries {state.Entries.Count}, branch entries {branch.Count}, messages {state.Entries.Count(static entry => entry.Type == MessageType)}, branch messages {branch.Count(static entry => entry.Type == MessageType)}, branches {state.BranchPointCount}, labels {state.LabelsById.Count}"
        };

        if (!string.IsNullOrWhiteSpace(entryId))
        {
            var entry = state.ResolveEntryId(entryId);
            AppendEntryMetadata(lines, state, branchIds, entry);
            return string.Join('\n', lines);
        }

        var metadataEntries = state.Entries
            .Where(static entry => entry.Type != MessageType)
            .TakeLast(8)
            .ToArray();
        if (metadataEntries.Length == 0)
        {
            lines.Add("latest metadata: none");
        }
        else
        {
            lines.Add($"latest metadata ({metadataEntries.Length}):");
            foreach (var entry in metadataEntries)
            {
                lines.Add($"  {entry.Id} <- {FormatEntryId(entry.ParentId)} {DescribeEntry(entry)}");
            }
        }

        return string.Join('\n', lines);
    }

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

        var items = BuildViewItems(state, branchIds, options);
        if (items.Count == 0)
        {
            lines.Add("tree has no entries matching filter");
        }
        else
        {
            foreach (var item in items)
            {
                lines.Add(item.DisplayLine);
            }
        }

        return string.Join('\n', lines);
    }

    public IReadOnlyList<CodingAgentTreeViewItem> EnumerateView(CodingAgentTreeFormatOptions options)
    {
        var state = ReadState();
        if (state.Entries.Count == 0)
        {
            return Array.Empty<CodingAgentTreeViewItem>();
        }

        var branchIds = state.GetBranch(state.LeafId)
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuildViewItems(state, branchIds, options);
    }

    public IReadOnlyList<CodingAgentForkMessage> GetUserMessagesForForking()
    {
        var state = ReadState();
        var messages = new List<CodingAgentForkMessage>();
        foreach (var entry in state.Entries)
        {
            if (entry.Type != MessageType ||
                entry.Message is null ||
                !string.Equals(entry.Message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ExtractUserMessageText(entry.Message);
            if (!string.IsNullOrWhiteSpace(text))
            {
                messages.Add(new CodingAgentForkMessage(entry.Id, text));
            }
        }

        return messages;
    }

    private List<CodingAgentTreeViewItem> BuildViewItems(
        TreeState state,
        HashSet<string> branchIds,
        CodingAgentTreeFormatOptions options)
    {
        var filteredEntries = state.Entries
            .Where(entry => ShouldShowEntry(entry, state, options.FilterMode))
            .Where(entry => MatchesSearch(entry, state, options.SearchQuery))
            .ToArray();
        var visibleEntries = filteredEntries
            .Skip(Math.Max(0, filteredEntries.Length - Math.Max(1, options.MaxEntries)));

        var items = new List<CodingAgentTreeViewItem>();
        foreach (var entry in visibleEntries)
        {
            var isLeaf = entry.Id.Equals(state.LeafId, StringComparison.OrdinalIgnoreCase);
            var onBranch = branchIds.Contains(entry.Id);
            var marker = isLeaf ? ">" : onBranch ? "*" : " ";
            var depth = Math.Min(GetDepth(entry, state.ById), 16);
            var indent = new string(' ', depth * 2);
            var label = state.LabelsById.TryGetValue(entry.Id, out var resolvedLabel)
                ? $" [{resolvedLabel}]"
                : string.Empty;
            var labelTime = options.ShowLabelTimestamps &&
                            state.LabelTimestampsById.TryGetValue(entry.Id, out var timestamp)
                ? $" @{timestamp}"
                : string.Empty;
            var line = $"{marker} {indent}{ShortId(entry.Id)} <- {ShortId(entry.ParentId)} {DescribeEntry(entry)}{label}{labelTime}";
            items.Add(new CodingAgentTreeViewItem(entry.Id, line, isLeaf, onBranch, entry.ParentId, depth, entry.Type));
        }

        return items;
    }

    private static void AppendEntryMetadata(
        ICollection<string> lines,
        TreeState state,
        HashSet<string> branchIds,
        CodingAgentTreeSessionEntry entry)
    {
        var childCount = state.Entries.Count(candidate =>
            string.Equals(candidate.ParentId, entry.Id, StringComparison.OrdinalIgnoreCase));
        lines.Add($"entry: {entry.Id}");
        lines.Add($"type: {entry.Type}");
        lines.Add($"parent: {FormatEntryId(entry.ParentId)}");
        lines.Add($"timestamp: {entry.Timestamp:O}");
        lines.Add($"path: {FormatPathState(state, branchIds, entry)}");
        lines.Add($"depth: {GetDepth(entry, state.ById)}, children {childCount}");
        if (state.LabelsById.TryGetValue(entry.Id, out var label))
        {
            lines.Add($"label: {label}");
        }

        switch (entry.Type)
        {
            case MessageType when entry.Message is not null:
                lines.Add($"message role: {entry.Message.Role}");
                AppendIfPresent(lines, "tool call id", entry.Message.ToolCallId);
                lines.Add($"content types: {FormatContentTypes(entry.Message)}");
                AppendIfPresent(lines, "preview", PreviewMessage(entry.Message));
                break;

            case ModelChangeType:
                lines.Add($"model: {FormatProviderModel(entry.Provider, entry.Model)}");
                break;

            case SessionInfoType:
                lines.Add($"action: {FormatOptionalValue(entry.Action)}");
                lines.Add($"name: {FormatOptionalValue(entry.Name)}");
                lines.Add($"model: {FormatProviderModel(entry.Provider, entry.Model)}");
                break;

            case LabelType:
                lines.Add($"target: {FormatEntryId(entry.TargetId)}");
                lines.Add($"value: {FormatOptionalValue(entry.Label)}");
                break;

            case CompactionType:
                lines.Add($"tokens before: {entry.TokensBefore.GetValueOrDefault()}");
                lines.Add($"first kept entry: {FormatEntryId(entry.FirstKeptEntryId)}");
                lines.Add($"from hook: {FormatNullableBoolean(entry.FromHook)}");
                lines.Add($"split turn: {FormatNullableBoolean(entry.IsSplitTurn)}");
                AppendIfPresent(lines, "summary", PreviewText(entry.Summary));
                AppendIfPresent(lines, "turn prefix summary", PreviewText(entry.TurnPrefixSummary));
                break;

            case BranchSummaryType:
                lines.Add($"from entry: {FormatEntryId(entry.FromId)}");
                lines.Add($"from hook: {FormatNullableBoolean(entry.FromHook)}");
                AppendIfPresent(lines, "summary", PreviewText(entry.Summary));
                AppendList(lines, "read files", entry.ReadFiles);
                AppendList(lines, "modified files", entry.ModifiedFiles);
                break;

            case AutoRetryStartType:
                lines.Add($"attempt: {entry.Attempt.GetValueOrDefault()}/{entry.MaxAttempts.GetValueOrDefault()}");
                lines.Add($"delay ms: {entry.DelayMs.GetValueOrDefault()}");
                AppendIfPresent(lines, "error", PreviewText(entry.ErrorMessage));
                break;

            case AutoRetryEndType:
                lines.Add($"success: {FormatNullableBoolean(entry.Success)}");
                lines.Add($"attempt: {entry.Attempt.GetValueOrDefault()}");
                AppendIfPresent(lines, "final error", PreviewText(entry.FinalError));
                break;

            case TreeStateType:
                AppendList(lines, "collapsed entries", entry.CollapsedEntryIds);
                break;
        }
    }

    private static string FormatEntryId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "none" : id;

    private static string FormatOptionalValue(string? value) =>
        NormalizeName(value) ?? "none";

    private static string FormatPathState(
        TreeState state,
        HashSet<string> branchIds,
        CodingAgentTreeSessionEntry entry)
    {
        if (entry.Id.Equals(state.LeafId, StringComparison.OrdinalIgnoreCase))
        {
            return "leaf";
        }

        return branchIds.Contains(entry.Id) ? "branch" : "off-branch";
    }

    private static string FormatProviderModel(string? provider, string? model) =>
        string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model)
            ? "none"
            : $"{FormatOptionalValue(provider)}/{FormatOptionalValue(model)}";

    private static string FormatNullableBoolean(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "none"
    };

    private static string FormatContentTypes(CodingAgentSessionMessage message)
    {
        var contentTypes = message.Content
            .Select(static content => content.Type)
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .ToArray();
        return contentTypes.Length == 0 ? "none" : string.Join(", ", contentTypes);
    }

    private static void AppendIfPresent(ICollection<string> lines, string label, string? value)
    {
        var normalized = NormalizeName(value);
        if (normalized is not null)
        {
            lines.Add($"{label}: {normalized}");
        }
    }

    private static void AppendList(ICollection<string> lines, string label, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            lines.Add($"{label}: {string.Join(", ", values)}");
        }
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
        File.WriteAllText(_path, SerializeJsonlLine(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader) + "\n");
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

        foreach (var entry in branch)
        {
            switch (entry.Type)
            {
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
            }
        }

        var messages = BuildSnapshotMessages(branch);
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

    private static IReadOnlyList<ChatMessage> BuildSnapshotMessages(IReadOnlyList<CodingAgentTreeSessionEntry> branch)
    {
        var messages = new List<ChatMessage>();
        var compactionIndex = -1;
        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i].Type == CompactionType)
            {
                compactionIndex = i;
                break;
            }
        }

        if (compactionIndex < 0)
        {
            foreach (var entry in branch)
            {
                AppendSnapshotMessage(messages, entry);
            }

            return messages;
        }

        var compaction = branch[compactionIndex];
        messages.Add(CodingAgentCompactionMessages.CreateSummaryMessage(
            compaction.Summary ?? string.Empty,
            compaction.TurnPrefixSummary));

        if (!string.IsNullOrWhiteSpace(compaction.FirstKeptEntryId))
        {
            var foundFirstKept = false;
            for (var i = 0; i < compactionIndex; i++)
            {
                var entry = branch[i];
                if (entry.Id.Equals(compaction.FirstKeptEntryId, StringComparison.OrdinalIgnoreCase))
                {
                    foundFirstKept = true;
                }

                if (foundFirstKept)
                {
                    AppendSnapshotMessage(messages, entry);
                }
            }
        }

        for (var i = compactionIndex + 1; i < branch.Count; i++)
        {
            AppendSnapshotMessage(messages, branch[i]);
        }

        return messages;
    }

    private static void AppendSnapshotMessage(ICollection<ChatMessage> messages, CodingAgentTreeSessionEntry entry)
    {
        if (entry.Type == BranchSummaryType && !string.IsNullOrWhiteSpace(entry.Summary))
        {
            messages.Add(CodingAgentCompactionMessages.CreateBranchSummaryMessage(entry.Summary, entry.FromId));
            return;
        }

        if (entry.Type != MessageType || entry.Message is null)
        {
            return;
        }

        var message = CodingAgentSessionStore.ToMessage(entry.Message);
        if (message is not null)
        {
            messages.Add(message);
        }
    }

    private static IReadOnlyList<CodingAgentTreeSessionEntry> CollectEntriesForBranchSummary(
        TreeState state,
        string? oldLeafId,
        string targetId)
    {
        if (string.IsNullOrWhiteSpace(oldLeafId) ||
            oldLeafId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var oldPathIds = state.GetBranch(oldLeafId)
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPath = state.GetBranch(targetId);
        string? commonAncestorId = null;
        for (var i = targetPath.Count - 1; i >= 0; i--)
        {
            if (oldPathIds.Contains(targetPath[i].Id))
            {
                commonAncestorId = targetPath[i].Id;
                break;
            }
        }

        var entries = new List<CodingAgentTreeSessionEntry>();
        var currentId = oldLeafId;
        while (!string.IsNullOrWhiteSpace(currentId) &&
               !currentId.Equals(commonAncestorId, StringComparison.OrdinalIgnoreCase) &&
               state.ById.TryGetValue(currentId, out var entry))
        {
            entries.Add(entry);
            currentId = entry.ParentId;
        }

        entries.Reverse();
        return entries;
    }

    private static IReadOnlyList<ChatMessage> BuildBranchSummaryMessages(
        IReadOnlyList<CodingAgentTreeSessionEntry> entries)
    {
        var messages = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case MessageType when entry.Message is not null &&
                                      !string.Equals(entry.Message.Role, "toolResult", StringComparison.OrdinalIgnoreCase):
                    var message = CodingAgentSessionStore.ToMessage(entry.Message);
                    if (message is not null)
                    {
                        messages.Add(message);
                    }

                    break;

                case CompactionType when !string.IsNullOrWhiteSpace(entry.Summary):
                    messages.Add(CodingAgentCompactionMessages.CreateSummaryMessage(entry.Summary));
                    break;

                case BranchSummaryType when !string.IsNullOrWhiteSpace(entry.Summary):
                    messages.Add(CodingAgentCompactionMessages.CreateBranchSummaryMessage(entry.Summary, entry.FromId));
                    break;
            }
        }

        return messages;
    }

    private void AppendEntry(CodingAgentTreeSessionEntry entry)
    {
        File.AppendAllText(_path, SerializeJsonlLine(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry) + "\n");
    }

    private string SerializeJsonlLine<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        var json = JsonSerializer.Serialize(value, jsonTypeInfo);
        return JsonlSecretRedactor.RedactLine(json, _secretRedactor);
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

    private static List<string>? NormalizeStringList(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return normalized.Count == 0 ? null : normalized;
    }

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

        var isSettingsEntry = entry.Type is LabelType or ModelChangeType or SessionInfoType or TreeStateType;
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

        if (!string.IsNullOrWhiteSpace(entry.FromId))
        {
            parts.Add(entry.FromId);
        }

        if (entry.ReadFiles is not null)
        {
            parts.AddRange(entry.ReadFiles);
        }

        if (entry.ModifiedFiles is not null)
        {
            parts.AddRange(entry.ModifiedFiles);
        }

        if (!string.IsNullOrWhiteSpace(entry.ErrorMessage))
        {
            parts.Add(entry.ErrorMessage);
        }

        if (!string.IsNullOrWhiteSpace(entry.FinalError))
        {
            parts.Add(entry.FinalError);
        }

        if (entry.Attempt is not null)
        {
            parts.Add(entry.Attempt.Value.ToString());
        }

        if (entry.MaxAttempts is not null)
        {
            parts.Add(entry.MaxAttempts.Value.ToString());
        }

        if (entry.DelayMs is not null)
        {
            parts.Add(entry.DelayMs.Value.ToString());
        }

        if (entry.Success is not null)
        {
            parts.Add(entry.Success.Value ? "success" : "failed");
        }

        if (entry.CollapsedEntryIds is not null)
        {
            parts.AddRange(entry.CollapsedEntryIds);
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
            BranchSummaryType => $"branch summary from {ShortId(entry.FromId)} {PreviewText(entry.Summary)}",
            AutoRetryStartType => $"auto-retry start {entry.Attempt.GetValueOrDefault()}/{entry.MaxAttempts.GetValueOrDefault()} {entry.DelayMs.GetValueOrDefault()}ms {PreviewText(entry.ErrorMessage)}",
            AutoRetryEndType => $"auto-retry end {(entry.Success == true ? "success" : "failed")} attempt {entry.Attempt.GetValueOrDefault()} {PreviewText(entry.FinalError)}",
            TreeStateType => $"tree state collapsed {entry.CollapsedEntryIds?.Count ?? 0}",
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

    private static string ExtractUserMessageText(CodingAgentSessionMessage message)
    {
        var text = string.Concat(
                message.Content
                    .Where(static content => content.Type == "text" && content.Text is not null)
                    .Select(static content => content.Text))
            .Trim();
        return text;
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
            TreeFoldState = BuildTreeFoldState(entries);
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
        public CodingAgentTreeFoldState? TreeFoldState { get; }
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

        private static CodingAgentTreeFoldState? BuildTreeFoldState(IReadOnlyList<CodingAgentTreeSessionEntry> entries)
        {
            CodingAgentTreeFoldState? latest = null;
            foreach (var entry in entries)
            {
                if (entry.Type != TreeStateType)
                {
                    continue;
                }

                latest = new CodingAgentTreeFoldState(NormalizeStringList(entry.CollapsedEntryIds) ?? []);
            }

            return latest;
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
    public string? FromId { get; init; }
    public List<string>? ReadFiles { get; init; }
    public List<string>? ModifiedFiles { get; init; }
    public string? FirstKeptEntryId { get; init; }
    public int? TokensBefore { get; init; }
    public bool? FromHook { get; init; }
    public bool? IsSplitTurn { get; init; }
    public string? TurnPrefixSummary { get; init; }
    public int? Attempt { get; init; }
    public int? MaxAttempts { get; init; }
    public int? DelayMs { get; init; }
    public string? ErrorMessage { get; init; }
    public bool? Success { get; init; }
    public string? FinalError { get; init; }
    public List<string>? CollapsedEntryIds { get; init; }

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
            FromId = FromId,
            ReadFiles = ReadFiles,
            ModifiedFiles = ModifiedFiles,
            FirstKeptEntryId = firstKeptEntryId ?? FirstKeptEntryId,
            TokensBefore = TokensBefore,
            FromHook = FromHook,
            IsSplitTurn = IsSplitTurn,
            TurnPrefixSummary = TurnPrefixSummary,
            Attempt = Attempt,
            MaxAttempts = MaxAttempts,
            DelayMs = DelayMs,
            ErrorMessage = ErrorMessage,
            Success = Success,
            FinalError = FinalError,
            CollapsedEntryIds = CollapsedEntryIds
        };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentTreeSessionHeader))]
[JsonSerializable(typeof(CodingAgentTreeSessionEntry))]
internal sealed partial class CodingAgentTreeSessionJsonContext : JsonSerializerContext;
