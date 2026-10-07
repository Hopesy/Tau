using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class JsonlSessionStorageTests
{
    /// <summary>【AgentCore】【工具命名空间】JSONL 重开保留可选及空字符串空间，旧消息缺省值仍为空。</summary>
    /// <param name="toolNamespace">待持久化空间。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mcp_files")]
    public async Task ToolNamespaceSurvivesJsonlReopen(string? toolNamespace)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "namespace.jsonl");
        var session = new AgentHarnessSession<JsonlSessionMetadata>(await JsonlSessionStorage.CreateAsync(path, temp.Path, "namespace"));
        await session.AppendMessageAsync(new AssistantMessage([new ToolCallContent("call", "read", "{}")
        { Namespace = toolNamespace, ThoughtSignature = "signature" }]));
        var reopened = new AgentHarnessSession<JsonlSessionMetadata>(await JsonlSessionStorage.OpenAsync(path));
        var assistant = Assert.Single((await reopened.BuildContextAsync()).Messages.OfType<AssistantMessage>());
        var call = Assert.IsType<ToolCallContent>(Assert.Single(assistant.Content));
        Assert.Equal(toolNamespace, call.Namespace);
        Assert.Equal("signature", call.ThoughtSignature);
        Assert.Equal("{}", call.Arguments);
        if (toolNamespace is not null) Assert.Contains("\"namespace\":", File.ReadAllText(path));
    }

    /// <summary>【AgentCore】【思考等级持久化】JSONL 重开后保留原生等级和签名，旧消息缺省仍为空。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task OpenAsync_PreservesProviderThinkingLevel()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "effort.jsonl");
        var session = new AgentHarnessSession<JsonlSessionMetadata>(await JsonlSessionStorage.CreateAsync(path, temp.Path, "effort"));
        await session.AppendMessageAsync(new AssistantMessage([new ThinkingContent("reasoning") { ThinkingSignature = "opaque" }, new TextContent("answer")])
        { Api = "anthropic-messages", Provider = "test", Model = "model", ProviderThinkingLevel = "xhigh" });
        await session.AppendMessageAsync(new AssistantMessage([new TextContent("legacy")]));
        var reopened = new AgentHarnessSession<JsonlSessionMetadata>(await JsonlSessionStorage.OpenAsync(path));
        var assistants = (await reopened.BuildContextAsync()).Messages.OfType<AssistantMessage>().ToArray();
        Assert.Equal("xhigh", assistants[0].ProviderThinkingLevel);
        Assert.Equal("opaque", Assert.IsType<ThinkingContent>(assistants[0].Content[0]).ThinkingSignature);
        Assert.Null(assistants[1].ProviderThinkingLevel);
        Assert.Contains("\"providerThinkingLevel\":\"xhigh\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task ForkAsync_WritesV4ParentSessionIdByDefault()
    {
        using var temp = TempDirectory.Create();
        var sourceRoot = Path.Combine(temp.Path, "sessions");
        var repo = new JsonlSessionRepo(sourceRoot);
        var source = await repo.CreateAsync(temp.Path, id: "source-1");
        await source.GetStorage().AppendEntryAsync(new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        var sourceMetadata = await source.GetMetadataAsync();

        var fork = await repo.ForkAsync(sourceMetadata, temp.Path, new SessionForkOptions(Id: "fork-1"));
        var header = JsonDocument.Parse(File.ReadAllLines((await fork.GetMetadataAsync()).Path)[0]).RootElement;

        Assert.Equal("source-1", header.GetProperty("parentSessionId").GetString());
        Assert.False(header.TryGetProperty("legacyParentSessionPath", out _));
    }

    [Fact]
    public async Task CreateAsync_WritesHeaderAndAppendsEntries()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");

        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");

        Assert.True(File.Exists(filePath));
        var lines = File.ReadAllText(filePath).Trim().Split('\n');
        Assert.Single(lines);
        var header = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("header", header.GetProperty("kind").GetString());
        Assert.Equal(4, header.GetProperty("version").GetInt32());
        Assert.Null(await storage.GetLeafIdAsync());

        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));

        lines = File.ReadAllText(filePath).Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("user-1", JsonDocument.Parse(lines[1]).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task OpenAsync_LoadsExistingEntriesAndReconstructsLeaf()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var root = new MessageSessionEntry(
            "root",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("root"));
        var child = root with
        {
            Id = "child",
            ParentId = "root",
            Message = new AssistantMessage([new TextContent("child")])
        };
        await storage.AppendEntryAsync(root);
        await storage.AppendEntryAsync(child);

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("child", await loaded.GetLeafIdAsync());
        Assert.Equal(["root", "child"], (await loaded.GetEntriesAsync()).Select(static entry => entry.Id));
        await loaded.SetLeafIdAsync("root");

        var reloaded = await JsonlSessionStorage.OpenAsync(filePath);
        Assert.Equal("root", await reloaded.GetLeafIdAsync());
        Assert.IsType<LeafSessionEntry>((await reloaded.GetEntriesAsync()).Last());
        Assert.Equal(["root", "child"], (await loaded.GetPathToRootAsync("child")).Select(static entry => entry.Id));
    }

    [Fact]
    public async Task LoadMetadataAsync_ReadsOnlyHeaderMetadata()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(
            filePath,
            temp.Path,
            "session-1",
            parentSessionPath: "/tmp/parent.jsonl");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));

        var metadata = await JsonlSessionStorage.LoadMetadataAsync(filePath);

        Assert.Equal("session-1", metadata.Id);
        Assert.Equal(temp.Path, metadata.Cwd);
        Assert.Equal(Path.GetFullPath(filePath), metadata.Path);
        Assert.Equal("/tmp/parent.jsonl", metadata.ParentSessionPath);
    }

    [Fact]
    public async Task CreateAsync_PersistsResolvedParentAndMetadataInV4Header()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "metadata-session.jsonl");
        using var metadataDocument = JsonDocument.Parse("{\"owner\":\"agent\",\"version\":1}");

        await JsonlSessionStorage.CreateAsync(
            filePath,
            temp.Path,
            "session-1",
            parentSessionId: "parent-1",
            metadata: metadataDocument.RootElement.Clone());

        var header = JsonDocument.Parse(File.ReadAllLines(filePath)[0]).RootElement;
        Assert.Equal("parent-1", header.GetProperty("parentSessionId").GetString());
        Assert.False(header.TryGetProperty("legacyParentSessionPath", out _));
        Assert.Equal("agent", header.GetProperty("metadata").GetProperty("owner").GetString());

        var loaded = await JsonlSessionStorage.LoadMetadataAsync(filePath);
        Assert.Equal("parent-1", loaded.ParentSessionId);
        Assert.Null(loaded.ParentSessionPath);
        Assert.Equal("agent", loaded.Metadata?.GetProperty("owner").GetString());
    }

    [Fact]
    public async Task OpenAsync_ThrowsForMalformedSessionHeader()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(filePath, "not json\n");

        var ex = await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(filePath));

        Assert.Equal("invalid_session", ex.Code);
        Assert.Contains("first line is not a valid session header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ThrowsForMalformedEntryLine()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(
            filePath,
            """
            {"type":"session","version":3,"id":"session-1","timestamp":"2026-01-01T00:00:00.000Z","cwd":"."}
            not json
            """);

        var ex = await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(filePath));

        Assert.Equal("invalid_entry", ex.Code);
        Assert.Contains("line 2 is not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabelLookupSurvivesReload()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new LabelSessionEntry(
            "label-1",
            "entry-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "entry-1",
            "checkpoint"));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("checkpoint", await loaded.GetLabelAsync("entry-1"));
    }

    [Fact]
    public async Task OpenAsync_RoundTripsCompactionAndCustomEntries()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new CustomSessionEntry(
            "custom-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "internal",
            JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone()));
        await storage.AppendEntryAsync(new CustomMessageSessionEntry(
            "custom-message-1",
            "custom-1",
            DateTimeOffset.Parse("2026-01-01T00:00:02.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "visible",
            [new TextContent("custom text"), new ImageContent("abc", "image/png")],
            Display: true,
            JsonDocument.Parse("""{"source":"test"}""").RootElement.Clone()));
        await storage.AppendEntryAsync(new CompactionSessionEntry(
            "compaction-1",
            "custom-message-1",
            DateTimeOffset.Parse("2026-01-01T00:00:03.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "summary",
            "user-1",
            42,
            JsonDocument.Parse("""{"files":["README.md"]}""").RootElement.Clone(),
            FromHook: true));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var entries = await loaded.GetEntriesAsync();

        var compactionJson = File.ReadAllLines(filePath)
            .Single(line => line.Contains("\"type\":\"compaction\"", StringComparison.Ordinal));
        using var compactionDocument = JsonDocument.Parse(compactionJson);
        Assert.Equal(JsonValueKind.Array, compactionDocument.RootElement.GetProperty("retainedTail").ValueKind);

        var custom = Assert.IsType<CustomSessionEntry>(entries[1]);
        var customData = Assert.IsType<JsonElement>(custom.Data);
        Assert.Equal("ok", customData.GetProperty("status").GetString());

        var customMessage = Assert.IsType<CustomMessageSessionEntry>(entries[2]);
        Assert.Equal("visible", customMessage.CustomType);
        Assert.True(customMessage.Display);
        Assert.Equal("custom text", Assert.IsType<TextContent>(customMessage.Content[0]).Text);
        Assert.Equal("image/png", Assert.IsType<ImageContent>(customMessage.Content[1]).MimeType);
        var details = Assert.IsType<JsonElement>(customMessage.Details);
        Assert.Equal("test", details.GetProperty("source").GetString());

        var compaction = Assert.IsType<CompactionSessionEntry>(entries[3]);
        Assert.Equal("summary", compaction.Summary);
        Assert.Equal("user-1", compaction.FirstKeptEntryId);
        Assert.Equal(42, compaction.TokensBefore);
        Assert.True(compaction.FromHook);
    }

    [Fact]
    public async Task OpenAsync_RoundTripsTypedCompactionDetailsAsJson()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new CompactionSessionEntry(
            "compaction-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "summary",
            "user-1",
            42,
            new AgentCompactionDetails(["README.md"], ["src/Program.cs"])));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var compaction = Assert.IsType<CompactionSessionEntry>((await loaded.GetEntriesAsync()).Last());
        var details = Assert.IsType<JsonElement>(compaction.Details);

        Assert.Equal("README.md", details.GetProperty("readFiles")[0].GetString());
        Assert.Equal("src/Program.cs", details.GetProperty("modifiedFiles")[0].GetString());
    }

    [Fact]
    public async Task OpenAsync_RoundTripsTypedBranchSummaryDetailsAsJson()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new BranchSummarySessionEntry(
            "branch-summary-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "from-1",
            "summary",
            new AgentBranchSummaryDetails(["README.md"], ["src/Program.cs"])));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var branchSummary = Assert.IsType<BranchSummarySessionEntry>((await loaded.GetEntriesAsync()).Last());
        var details = Assert.IsType<JsonElement>(branchSummary.Details);

        Assert.Equal("README.md", details.GetProperty("readFiles")[0].GetString());
        Assert.Equal("src/Program.cs", details.GetProperty("modifiedFiles")[0].GetString());
    }

    [Fact]
    public async Task OpenAsync_RoundTripsAllHarnessAgentMessageVariants()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "agent-messages.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

        await storage.AppendEntryAsync(new MessageSessionEntry("bash", null, timestamp,
            new AgentBashExecutionMessage("echo hi", "hi", 0, false, false, Timestamp: timestamp)));
        await storage.AppendEntryAsync(new MessageSessionEntry("custom", "bash", timestamp,
            new AgentCustomMessage("notice", [new TextContent("custom")], true, JsonDocument.Parse("{\"source\":\"test\"}").RootElement.Clone(), timestamp)));
        await storage.AppendEntryAsync(new MessageSessionEntry("branch", "custom", timestamp,
            new AgentBranchSummaryMessage("branch summary", "bash", timestamp)));
        await storage.AppendEntryAsync(new MessageSessionEntry("compact", "branch", timestamp,
            new AgentCompactionSummaryMessage("compaction summary", 12, timestamp)));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var messages = (await loaded.GetEntriesAsync()).OfType<MessageSessionEntry>().Select(static entry => entry.Message).ToArray();

        Assert.IsType<AgentBashExecutionMessage>(messages[0]);
        Assert.IsType<AgentCustomMessage>(messages[1]);
        Assert.IsType<AgentBranchSummaryMessage>(messages[2]);
        Assert.IsType<AgentCompactionSummaryMessage>(messages[3]);
        Assert.Contains("bashExecution", File.ReadAllText(filePath), StringComparison.Ordinal);
        Assert.Contains("branchSummary", File.ReadAllText(filePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_PreservesV4NestedMessageTimestampsAndUsageDiagnostics()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "message-fields.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);
        var usage = new Usage(10, 20, 3, 4, "standard", new UsageCost(1, 2, 3, 4))
        {
            CacheWrite1hTokens = 2,
            ReasoningTokens = 5,
            TotalTokens = 37
        };
        var assistant = new AssistantMessage([new TextContent("answer")])
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "test-model",
            ResponseModel = "resolved-model",
            ResponseId = "response-1",
            StopReason = StopReason.EndTurn,
            RawStopReason = "completed",
            EndTurn = true,
            Usage = usage,
            Timestamp = timestamp,
            Diagnostics = [new AssistantMessageDiagnostic { Type = "test", Timestamp = timestamp }]
        };
        await storage.AppendEntryAsync(new MessageSessionEntry("assistant", null, timestamp, assistant));

        var raw = File.ReadAllText(filePath);
        Assert.Contains("\"timestamp\":1767225600000", raw, StringComparison.Ordinal);
        Assert.Contains("\"cacheWrite1h\":2", raw, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":5", raw, StringComparison.Ordinal);

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var roundTripped = Assert.IsType<AssistantMessage>(Assert.IsType<MessageSessionEntry>((await loaded.GetEntriesAsync()).Single()).Message);
        Assert.Equal(timestamp, roundTripped.Timestamp);
        Assert.Equal(2, roundTripped.Usage?.CacheWrite1hTokens);
        Assert.Equal(5, roundTripped.Usage?.ReasoningTokens);
        Assert.Equal(37, roundTripped.Usage?.TotalTokens);
        Assert.Equal("resolved-model", roundTripped.ResponseModel);
        Assert.Equal("completed", roundTripped.RawStopReason);
        Assert.True(roundTripped.EndTurn);
        Assert.Single(roundTripped.Diagnostics!);
    }

    [Fact]
    public async Task OpenAsync_RoundTripsToolCallArgumentsAsJsonObject()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "tool-call-arguments.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);
        var assistant = new AssistantMessage([
            new ToolCallContent("call-1", "read", "{\"path\":\"README.md\"}")
        ])
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "test-model",
            Usage = new Usage(0, 0, 0, 0),
            StopReason = StopReason.ToolUse,
            Timestamp = timestamp
        };

        await storage.AppendEntryAsync(new MessageSessionEntry("assistant", null, timestamp, assistant));

        var raw = File.ReadAllText(filePath);
        Assert.Contains("\"arguments\":{\"path\":\"README.md\"}", raw, StringComparison.Ordinal);

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var message = Assert.IsType<AssistantMessage>(Assert.IsType<MessageSessionEntry>(
            (await loaded.GetEntriesAsync()).Single()).Message);
        var toolCall = Assert.IsType<ToolCallContent>(Assert.Single(message.Content));
        Assert.Equal("{\"path\":\"README.md\"}", toolCall.Arguments);
    }

    [Fact]
    public async Task OpenAsync_RoundTripsToolCallArgumentsAsJsonValue()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "tool-call-arguments-value.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);
        var assistant = new AssistantMessage([
            new ToolCallContent("call-1", "read", "[1,true,{\"path\":\"README.md\"}]")
        ])
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "test-model",
            Usage = new Usage(0, 0, 0, 0),
            StopReason = StopReason.ToolUse,
            Timestamp = timestamp
        };

        await storage.AppendEntryAsync(new MessageSessionEntry("assistant", null, timestamp, assistant));

        var raw = File.ReadAllText(filePath);
        Assert.Contains("\"arguments\":[1,true,{\"path\":\"README.md\"}]", raw, StringComparison.Ordinal);

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var message = Assert.IsType<AssistantMessage>(Assert.IsType<MessageSessionEntry>(
            (await loaded.GetEntriesAsync()).Single()).Message);
        var toolCall = Assert.IsType<ToolCallContent>(Assert.Single(message.Content));
        Assert.Equal("[1,true,{\"path\":\"README.md\"}]", toolCall.Arguments);
    }

    [Fact]
    public async Task AppendRecordAsync_PersistsRecordLogAcrossReload()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendRecordAsync(new OperationStartedRecord(
            "run-1",
            1,
            "main",
            DateTimeOffset.UtcNow,
            "run"));
        await storage.AppendRecordAsync(new QueueEnqueuedRecord(
            "queue-1",
            2,
            "main",
            DateTimeOffset.UtcNow,
            "nextRun",
            "entry-1"));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        var records = await loaded.FindRecordsAsync(new RecordQuery { Lane = "main", Order = EntryOrder.OldestFirst });
        Assert.Collection(
            records,
            record => Assert.IsType<OperationStartedRecord>(record),
            record => Assert.IsType<QueueEnqueuedRecord>(record));
        Assert.Single(await loaded.FindOpenOperationsAsync("main"));
        Assert.Contains("\"kind\":\"record\"", File.ReadAllText(filePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ReadsPiV4HeaderAndMutationLines()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session-v4.jsonl");
        await File.WriteAllTextAsync(
            filePath,
            ""
            + "{\"kind\":\"header\",\"version\":4,\"id\":\"session-v4\",\"createdAt\":1767225600000,\"cwd\":\"" + temp.Path.Replace("\\", "\\\\") + "\"}\n"
            + "{\"kind\":\"entry\",\"seq\":1,\"id\":\"entry-1\",\"parentId\":null,\"timestamp\":1767225600000,\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}\n"
            + "{\"kind\":\"record\",\"seq\":2,\"id\":\"run-1\",\"lane\":\"main\",\"timestamp\":1767225600000,\"type\":\"operation_started\",\"operationKind\":\"run\"}\n");

        var storage = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("session-v4", (await storage.GetMetadataAsync()).Id);
        Assert.Equal("entry-1", (await storage.GetEntriesAsync()).Single().Id);
        Assert.Equal("run-1", Assert.Single(await storage.FindOpenOperationsAsync("main")).Id);
    }

    [Fact]
    public async Task OpenAsync_ReplaysSecondaryLaneMutationsInFileOrder()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session-v4-secondary-lane.jsonl");
        var cwd = temp.Path.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            filePath,
            $"{{\"kind\":\"header\",\"version\":4,\"id\":\"session-1\",\"createdAt\":1,\"cwd\":\"{cwd}\"}}\n" +
            "{\"kind\":\"lane\",\"seq\":1,\"lane\":\"review\",\"leafId\":null}\n" +
            "{\"kind\":\"entry\",\"seq\":2,\"lane\":\"review\",\"id\":\"review-root\",\"parentId\":null,\"timestamp\":1,\"type\":\"custom\",\"customType\":\"note\"}\n" +
            "{\"kind\":\"entry\",\"seq\":3,\"lane\":\"main\",\"id\":\"main-root\",\"parentId\":null,\"timestamp\":1,\"type\":\"custom\",\"customType\":\"note\"}\n");

        var storage = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Contains(await storage.GetLanesAsync(), lane => lane == new LanePointer("review", "review-root"));
        Assert.Contains(await storage.GetLanesAsync(), lane => lane == new LanePointer("main", "main-root"));
        Assert.Equal(["review-root", "main-root"], (await storage.GetLogAsync()).OfType<SessionEntryLogItem>().Select(item => item.Entry.Id));
    }

    [Fact]
    public async Task V4LaneAndFactMutations_SurviveReloadAndPreserveLogOrder()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session-v4-lanes.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-v4-lanes");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "root",
            null,
            DateTimeOffset.UtcNow,
            new UserMessage("root")));
        await storage.CreateLaneAsync("review", "root");
        await storage.SetNameAsync("Review session");
        await storage.SetLabelAsync("root", "checkpoint");

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        Assert.Contains(await loaded.GetLanesAsync(), lane => lane == new LanePointer("review", "root"));
        Assert.Equal("Review session", await loaded.GetNameAsync());
        Assert.Equal("checkpoint", await loaded.GetLabelAsync("root"));

        var log = await loaded.GetLogAsync();
        Assert.Equal(["entry", "lane", "fact", "fact"], log.Select(item => item.Kind));
        Assert.True(log.Zip(log.Skip(1), (left, right) => left.Sequence < right.Sequence).All(static result => result));
    }

    [Fact]
    public async Task QueueCancellation_OmitsOptionalNullRunId()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "queue-cancelled.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");

        await storage.AppendRecordAsync(new QueueCancelledRecord(
            "cancel-1",
            1,
            "main",
            DateTimeOffset.UnixEpoch.AddMilliseconds(1),
            "entry-1"));

        var raw = File.ReadAllText(filePath);
        Assert.DoesNotContain("runId", raw, StringComparison.Ordinal);
        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        Assert.Single(await loaded.FindRecordsAsync());
    }

    [Fact]
    public async Task OpenAsync_RejectsNegativeOptionalUsageToken()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "invalid-usage.jsonl");
        var cwd = temp.Path.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            filePath,
            $"{{\"kind\":\"header\",\"version\":4,\"id\":\"session-1\",\"createdAt\":1,\"cwd\":\"{cwd}\"}}\n" +
            "{\"kind\":\"record\",\"seq\":1,\"id\":\"usage-1\",\"lane\":\"main\",\"timestamp\":1,\"type\":\"usage\",\"cause\":\"adjustment\",\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"cacheWrite1h\":-1,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}}\n");

        var exception = await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(filePath));

        Assert.Equal("invalid_entry", exception.Code);
        Assert.Contains("usage.cacheWrite1h", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-agentcore-jsonl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
