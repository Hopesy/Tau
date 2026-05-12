using Microsoft.Extensions.Logging.Abstractions;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomChannelQueueDispatcherTests
{
    [Fact]
    public async Task Dispatch_WithSameChannelMessages_ProcessesSequentially()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-queue-{Guid.NewGuid():N}");
        var runner = new BlockingDelegationAgentRunner();
        var responder = new RecordingResponder();
        var dispatcher = CreateDispatcher(root, runner);

        try
        {
            var first = dispatcher.Dispatch(new MomChannelMessage("C123", "first", "1.000001", "U1"), responder);
            var firstStarted = await runner.WaitForStartedCountAsync(1);
            var second = dispatcher.Dispatch(new MomChannelMessage("C123", "second", "2.000001", "U1"), responder);

            await Assert.ThrowsAsync<TimeoutException>(() => runner.WaitForStartedCountAsync(2, TimeSpan.FromMilliseconds(150)));
            Assert.Equal(["first"], runner.StartedPrompts);

            firstStarted.Complete("first done");
            Assert.True(await first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            var secondStarted = await runner.WaitForStartedCountAsync(2);
            Assert.Equal("second", secondStarted.Request.Prompt);
            secondStarted.Complete("second done");
            Assert.True(await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(["first", "second"], runner.StartedPrompts);
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task Dispatch_WithDifferentChannels_AllowsIndependentProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-queue-{Guid.NewGuid():N}");
        var runner = new BlockingDelegationAgentRunner();
        var responder = new RecordingResponder();
        var dispatcher = CreateDispatcher(root, runner);

        try
        {
            var first = dispatcher.Dispatch(new MomChannelMessage("C123", "first", "1.000001", "U1"), responder);
            var firstStarted = await runner.WaitForStartedCountAsync(1);
            var second = dispatcher.Dispatch(new MomChannelMessage("C456", "second", "2.000001", "U2"), responder);
            var secondStarted = await runner.WaitForStartedCountAsync(2);

            Assert.Equal(["first", "second"], runner.StartedPrompts);

            firstStarted.Complete("first done");
            secondStarted.Complete("second done");
            Assert.True(await first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task Dispatch_WhenPendingQueueIsFull_RejectsExtraWork()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-queue-{Guid.NewGuid():N}");
        var runner = new BlockingDelegationAgentRunner();
        var responder = new RecordingResponder();
        var dispatcher = CreateDispatcher(root, runner, queueLimit: 1);

        try
        {
            var first = dispatcher.Dispatch(new MomChannelMessage("C123", "first", "1.000001", "U1"), responder);
            var firstStarted = await runner.WaitForStartedCountAsync(1);
            var second = dispatcher.Dispatch(new MomChannelMessage("C123", "second", "2.000001", "U1"), responder);
            var third = dispatcher.Dispatch(new MomChannelMessage("C123", "third", "3.000001", "U1"), responder);

            Assert.Equal(MomChannelDispatchStatus.Enqueued, second.Status);
            Assert.Equal(MomChannelDispatchStatus.QueueFull, third.Status);
            Assert.False(third.Accepted);
            Assert.False(await third.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            var queueFullResponse = Assert.Single(responder.Responses);
            Assert.Equal("_Queue full. Try again after the current work finishes._", queueFullResponse.Text);
            Assert.Equal(["first"], runner.StartedPrompts);

            firstStarted.Complete("first done");
            Assert.True(await first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            var secondStarted = await runner.WaitForStartedCountAsync(2);
            secondStarted.Complete("second done");
            Assert.True(await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task Dispatch_WithStopCommand_BypassesPendingQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-queue-{Guid.NewGuid():N}");
        var runner = new BlockingDelegationAgentRunner();
        var responder = new RecordingResponder();
        var dispatcher = CreateDispatcher(root, runner);

        try
        {
            var first = dispatcher.Dispatch(new MomChannelMessage("C123", "first", "1.000001", "U1"), responder);
            var firstStarted = await runner.WaitForStartedCountAsync(1);
            var second = dispatcher.Dispatch(new MomChannelMessage("C123", "second", "2.000001", "U1"), responder);
            var stop = dispatcher.Dispatch(new MomChannelMessage("C123", "stop", "3.000001", "U1"), responder);

            Assert.Equal(MomChannelDispatchStatus.StopDispatched, stop.Status);
            Assert.False(await stop.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(
                responder.Responses,
                response => response.Text == "_Stopping..._");
            Assert.Equal(["first"], runner.StartedPrompts);
            Assert.False(await first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.False(firstStarted.Response.Task.IsCompletedSuccessfully);

            var secondStarted = await runner.WaitForStartedCountAsync(2);
            Assert.Equal("second", secondStarted.Request.Prompt);
            secondStarted.Complete("second done");
            Assert.True(await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    private static MomChannelQueueDispatcher CreateDispatcher(
        string root,
        IDelegationAgentRunner runner,
        int queueLimit = 5)
    {
        var options = new MomOptions
        {
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4",
            RunningStatusStaleAfterMinutes = 60,
            SlackChannelQueueLimit = queueLimit
        };
        var processor = new MomChannelMessageProcessor(
            options,
            runner,
            new ChannelStatusStore(NullLogger<ChannelStatusStore>.Instance),
            NullLogger<MomChannelMessageProcessor>.Instance);
        return new MomChannelQueueDispatcher(
            processor,
            options,
            NullLogger<MomChannelQueueDispatcher>.Instance);
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class BlockingDelegationAgentRunner : IDelegationAgentRunner
    {
        private readonly object _gate = new();
        private readonly List<StartedExecution> _started = [];
        private readonly List<StartedWaiter> _waiters = [];

        public string[] StartedPrompts
        {
            get
            {
                lock (_gate)
                {
                    return _started.Select(static started => started.Request.Prompt).ToArray();
                }
            }
        }

        public Task<StartedExecution> WaitForStartedCountAsync(int count)
        {
            return WaitForStartedCountAsync(count, TimeSpan.FromSeconds(5));
        }

        public Task<StartedExecution> WaitForStartedCountAsync(int count, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (_started.Count >= count)
                {
                    return Task.FromResult(_started[count - 1]);
                }

                var source = new TaskCompletionSource<StartedExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new StartedWaiter(count, source));
                return source.Task.WaitAsync(timeout);
            }
        }

        public async Task<DelegationExecution> ExecuteAsync(
            DelegationRequest request,
            CancellationToken cancellationToken = default)
        {
            var started = new StartedExecution(request);
            lock (_gate)
            {
                _started.Add(started);
                CompleteReadyWaiters();
            }

            var response = await started.Response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DelegationExecution(
                response,
                [],
                Error: null,
                request.Provider ?? "openai",
                request.Model ?? "gpt-5.4",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: "end_turn");
        }

        private void CompleteReadyWaiters()
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (_started.Count < waiter.Count)
                {
                    continue;
                }

                _waiters.RemoveAt(index);
                waiter.Source.TrySetResult(_started[waiter.Count - 1]);
            }
        }
    }

    private sealed class StartedExecution
    {
        public StartedExecution(DelegationRequest request)
        {
            Request = request;
        }

        public DelegationRequest Request { get; }
        public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(string response)
        {
            Response.TrySetResult(response);
        }
    }

    private sealed record StartedWaiter(int Count, TaskCompletionSource<StartedExecution> Source);

    private sealed class RecordingResponder : IMomChannelResponder
    {
        public List<(MomChannelMessage Message, string Text)> Responses { get; } = [];

        public Task<string?> RespondAsync(
            MomChannelMessage message,
            string text,
            CancellationToken cancellationToken = default)
        {
            Responses.Add((message, text));
            return Task.FromResult<string?>("response-ts");
        }

        public Task<string?> RespondInThreadAsync(
            MomChannelMessage message,
            string text,
            CancellationToken cancellationToken = default)
        {
            Responses.Add((message, text));
            return Task.FromResult<string?>("thread-response-ts");
        }

        public Task SetTypingAsync(
            MomChannelMessage message,
            bool isTyping,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(
            MomChannelMessage message,
            string filePath,
            string? title = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
