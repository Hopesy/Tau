using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

// Direct unit coverage for the Tau-native port of upstream `packages/ai/src/utils/event-stream.ts`.
// EventStream is a push/pull bridge: producers Push events, a single completing event terminates the
// stream and extracts the final result, and consumers iterate the buffered events via
// IAsyncEnumerable. These tests pin the primitive's own contract directly; provider-family terminal
// event contracts are covered separately by the per-provider parser tests.
public sealed class EventStreamTests
{
    private static EventStream<string, string> CreateStream() =>
        new(isComplete: static e => e == "END", extractResult: static e => e == "END" ? "final-result" : null);

    [Fact]
    public async Task Push_DeliversEventsInOrderIncludingTerminator()
    {
        var stream = CreateStream();
        stream.Push("a");
        stream.Push("b");
        stream.Push("END");

        var collected = new List<string>();
        await foreach (var e in stream)
        {
            collected.Add(e);
        }

        Assert.Equal(["a", "b", "END"], collected);
    }

    [Fact]
    public async Task Push_CompletingEvent_SetsResult()
    {
        var stream = CreateStream();
        stream.Push("a");
        stream.Push("END");

        Assert.Equal("final-result", await stream.ResultAsync);
    }

    [Fact]
    public void Push_AfterCompletion_Throws()
    {
        var stream = CreateStream();
        stream.Push("END");

        Assert.Throws<InvalidOperationException>(() => stream.Push("late"));
    }

    [Fact]
    public async Task Push_CompletingEventWithoutResult_FaultsResult()
    {
        // isComplete returns true but extractResult returns null: the stream must surface a fault
        // rather than silently completing with a missing result.
        var stream = new EventStream<string, string>(
            isComplete: static e => e == "STOP",
            extractResult: static _ => null);
        stream.Push("STOP");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ResultAsync);
    }

    [Fact]
    public async Task End_CompletesStreamAndSetsResultWithoutTerminatorEvent()
    {
        var stream = CreateStream();
        stream.Push("a");
        stream.End("manual-result");

        var collected = new List<string>();
        await foreach (var e in stream)
        {
            collected.Add(e);
        }

        Assert.Equal(["a"], collected);
        Assert.Equal("manual-result", await stream.ResultAsync);
    }

    [Fact]
    public async Task Fault_FaultsBothEnumerationAndResult()
    {
        var stream = CreateStream();
        stream.Push("a");
        stream.Fault(new InvalidOperationException("boom"));

        // Buffered events are still drained, then the fault surfaces on the enumerator.
        var enumerationEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });
        Assert.Equal("boom", enumerationEx.Message);

        var resultEx = await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ResultAsync);
        Assert.Equal("boom", resultEx.Message);
    }

    [Fact]
    public async Task Consumer_CanStartBeforeProducerPushes()
    {
        var stream = CreateStream();

        var consumer = Task.Run(async () =>
        {
            var collected = new List<string>();
            await foreach (var e in stream)
            {
                collected.Add(e);
            }

            return collected;
        });

        stream.Push("x");
        stream.Push("y");
        stream.Push("END");

        Assert.Equal(["x", "y", "END"], await consumer);
    }
}

// Direct unit coverage for the typed assistant stream (`AssistantMessageStream`), which specializes
// EventStream to terminate on DoneEvent / ErrorEvent and reconstruct the final AssistantMessage.
public sealed class AssistantMessageStreamTests
{
    [Fact]
    public async Task DoneEvent_TerminatesWithThatMessage()
    {
        var stream = new AssistantMessageStream();
        var message = new AssistantMessage([new TextContent("hi")]) { StopReason = StopReason.EndTurn };
        stream.Push(new StartEvent(new AssistantMessage()));
        stream.Push(new DoneEvent(message));

        Assert.Same(message, await stream.ResultAsync);
    }

    [Fact]
    public async Task ErrorEvent_WithExplicitMessage_TerminatesWithThatMessage()
    {
        var stream = new AssistantMessageStream();
        var message = new AssistantMessage { ErrorMessage = "explicit" };
        stream.Push(new ErrorEvent("some error", Message: message));

        Assert.Same(message, await stream.ResultAsync);
    }

    [Fact]
    public async Task ErrorEvent_WithoutMessage_BuildsResultFromPartial()
    {
        var stream = new AssistantMessageStream();
        var partial = new AssistantMessage([new TextContent("partial text")])
        {
            Usage = new Usage(10, 5),
            StopReason = StopReason.MaxTokens,
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude",
            ResponseId = "resp-1"
        };
        stream.Push(new ErrorEvent("network fail", Partial: partial));

        var result = await stream.ResultAsync;
        Assert.Equal("network fail", result.ErrorMessage);
        Assert.Equal(partial.Content, result.Content);
        Assert.Equal(StopReason.MaxTokens, result.StopReason);
        Assert.Equal("anthropic-messages", result.Api);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("claude", result.Model);
        Assert.Equal("resp-1", result.ResponseId);
        Assert.Equal(partial.Usage, result.Usage);
    }

    [Fact]
    public async Task ErrorEvent_WithoutMessageOrPartial_BuildsMinimalErrorResult()
    {
        var stream = new AssistantMessageStream();
        stream.Push(new ErrorEvent("bare error"));

        var result = await stream.ResultAsync;
        Assert.Equal("bare error", result.ErrorMessage);
        Assert.Empty(result.Content);
        Assert.Equal(StopReason.Error, result.StopReason);
    }

    [Fact]
    public async Task DeliversFullLifecycleBeforeTermination()
    {
        var stream = new AssistantMessageStream();
        stream.Push(new StartEvent(new AssistantMessage()));
        stream.Push(new TextStartEvent(0, new AssistantMessage()));
        stream.Push(new TextDeltaEvent(0, "hello", new AssistantMessage()));
        stream.Push(new TextEndEvent(0, new AssistantMessage()));
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("hello")])));

        var types = new List<string>();
        await foreach (var e in stream)
        {
            types.Add(e.Type);
        }

        Assert.Equal(["start", "text_start", "text_delta", "text_end", "done"], types);
    }
}
