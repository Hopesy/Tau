using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class CurrentReferenceParityTests
{
    [Fact]
    public void HarnessWatch_BuffersEventsUntilStarted()
    {
        var bus = new HarnessEventBus();
        var watch = bus.Watch(() => 7);
        bus.Emit(new HarnessRunStartEvent("main", "run-1"));
        var received = new List<object>();
        watch.Start(received.Add);
        Assert.Equal(7, watch.Snapshot);
        Assert.Single(received);
    }

    [Fact]
    public void Reducer_RejectsMultipleOpenOperations()
    {
        Assert.Throws<RecordLogCorruptionException>(() => HarnessRecordReducer.ValidateRecordLog(new HarnessRecordLogSlice("main", [], ["a", "b"])));
    }
}
