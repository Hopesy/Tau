using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentExtensionEventBusTests
{
    [Fact]
    public async Task PublishAsync_InvokesMatchingHandlersInSubscriptionOrder()
    {
        var bus = new CodingAgentExtensionEventBus();
        var calls = new List<string>();

        bus.Subscribe("extension.ready", (extensionEvent, _) =>
        {
            calls.Add($"first:{extensionEvent.Payload}");
            return ValueTask.CompletedTask;
        });
        bus.Subscribe("other.event", (_, _) =>
        {
            calls.Add("other");
            return ValueTask.CompletedTask;
        });
        bus.Subscribe("extension.ready", (extensionEvent, _) =>
        {
            calls.Add($"second:{extensionEvent.Type}");
            return ValueTask.CompletedTask;
        });

        var result = await bus.PublishAsync("extension.ready", "payload");

        Assert.True(result.Succeeded);
        Assert.Equal("extension.ready", result.EventType);
        Assert.Equal(2, result.HandlerCount);
        Assert.Equal(["first:payload", "second:extension.ready"], calls);
        Assert.All(result.HandlerResults, static handlerResult => Assert.True(handlerResult.Succeeded));
    }

    [Fact]
    public async Task Subscribe_DisposeUnsubscribesHandler()
    {
        var bus = new CodingAgentExtensionEventBus();
        var calls = 0;
        var subscription = bus.Subscribe("extension.ready", (_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync("extension.ready", null);
        subscription.Dispose();
        await bus.PublishAsync("extension.ready", null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PublishAsync_TypedEventUsesEventRecordType()
    {
        var bus = new CodingAgentExtensionEventBus();
        var received = new List<ExtensionRuntimeStartedEvent>();

        bus.Subscribe<ExtensionRuntimeStartedEvent>((eventRecord, _) =>
        {
            received.Add(eventRecord);
            return ValueTask.CompletedTask;
        });

        var result = await bus.PublishAsync(new ExtensionRuntimeStartedEvent("sample-extension"));

        Assert.True(result.Succeeded);
        Assert.Equal(typeof(ExtensionRuntimeStartedEvent).FullName, result.EventType);
        Assert.Equal([new ExtensionRuntimeStartedEvent("sample-extension")], received);
    }

    [Fact]
    public async Task PublishAsync_CollectsHandlerExceptionsAndContinues()
    {
        var bus = new CodingAgentExtensionEventBus();
        var calls = new List<string>();

        bus.Subscribe("extension.ready", (_, _) =>
        {
            calls.Add("first");
            throw new InvalidOperationException("handler failed");
        });
        bus.Subscribe("extension.ready", (_, _) =>
        {
            calls.Add("second");
            return ValueTask.CompletedTask;
        });

        var result = await bus.PublishAsync("extension.ready", null);

        Assert.False(result.Succeeded);
        Assert.Equal(["first", "second"], calls);
        Assert.Equal(2, result.HandlerCount);
        var exception = Assert.Single(result.Exceptions);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("handler failed", exception.Message);
    }

    [Fact]
    public async Task PublishAsync_ThrowsWhenCancelledBeforeHandler()
    {
        var bus = new CodingAgentExtensionEventBus();
        var cts = new CancellationTokenSource();
        var calls = 0;
        bus.Subscribe("extension.ready", (_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.PublishAsync("extension.ready", null, cts.Token));
        Assert.Equal(0, calls);
    }

    private sealed record ExtensionRuntimeStartedEvent(string ExtensionName);
}
