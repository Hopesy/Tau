using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class SessionQueryParityTests
{
    [Fact]
    public async Task FindEntriesOnBranchAsync_RespectsBoundsOrderAndType()
    {
        var root = new MessageSessionEntry("root", null, DateTimeOffset.UtcNow, new UserMessage("root"));
        var custom = new CustomSessionEntry("custom", "root", DateTimeOffset.UtcNow, "note");
        var leaf = new MessageSessionEntry("leaf", "custom", DateTimeOffset.UtcNow, new UserMessage("leaf"));
        var storage = new InMemorySessionStorage<SessionMetadata>(entries: [root, custom, leaf]);

        var newest = await storage.FindEntriesOnBranchAsync(new EntryQuery(Type: "message"));
        var oldest = await storage.FindEntriesOnBranchAsync(
            new EntryQuery(Order: EntryOrder.OldestFirst),
            new BranchBounds(StopAtType: "custom"));

        Assert.Equal(["leaf", "root"], newest.Select(item => item.Id));
        Assert.Equal(["custom", "leaf"], oldest.Select(item => item.Id));
    }

    [Fact]
    public async Task FindRecordsAsync_FiltersOperationKindAndOpenOperations()
    {
        var started = new OperationStartedRecord("run-1", 1, "main", DateTimeOffset.UtcNow, "run");
        var compaction = new OperationStartedRecord("compact-1", 2, "main", DateTimeOffset.UtcNow, "compaction");
        var finished = new OperationFinishedRecord("finish-1", 3, "main", DateTimeOffset.UtcNow, "compact-1", "completed");
        var storage = new InMemorySessionStorage<SessionMetadata>(records: [started, compaction, finished]);

        var runs = await storage.FindRecordsAsync(new RecordQuery(OperationKind: "run"));
        var open = await storage.FindOpenOperationsAsync("main");

        Assert.Single(runs);
        Assert.Equal("run-1", runs[0].Id);
        Assert.Equal(["run-1"], open.Select(item => item.Id));
    }

    [Fact]
    public async Task ReduceLaneState_RestoresPendingQueuesAndTerminalFailure()
    {
        var started = new OperationStartedRecord("run-1", 1, "main", DateTimeOffset.UtcNow, "run");
        var queued = new QueueEnqueuedRecord("queue-1", 2, "main", DateTimeOffset.UtcNow, "steer", "pending", "run-1");
        var attempt = new StepAttemptRecord("attempt-1", 3, "main", DateTimeOffset.UtcNow, "run-1", "assistant", 1, "error");
        var error = new MessageSessionEntry(
            "error",
            null,
            DateTimeOffset.UtcNow,
            new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = "failed" });
        var slice = new RecordLogSlice("main", [started, queued, attempt], [started], [error]);
        var input = new LaneReductionInput(
            slice,
            "error",
            [error],
            [],
            new EffectiveLaneConfiguration("openai", "gpt-5", "off", []));

        var result = HarnessRecordReducer.ReduceLaneState(input);

        Assert.Equal("run-1", result.LaneState.Operation?.Id);
        Assert.Equal(["pending"], result.LaneState.Operation?.PendingSteer);
        Assert.Equal("error", result.TerminalFailure?.EntryId);
    }
}
