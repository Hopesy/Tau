using System.Collections.Concurrent;
using System.Threading.Channels;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebUiJavaScriptReplBridge
{
    public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromSeconds(25);

    private readonly ConcurrentDictionary<string, SessionQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingExecution> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _executionTimeout;

    public WebUiJavaScriptReplBridge()
        : this(DefaultExecutionTimeout)
    {
    }

    public WebUiJavaScriptReplBridge(TimeSpan executionTimeout)
    {
        if (executionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout), "Execution timeout must be positive.");
        }

        _executionTimeout = executionTimeout;
    }

    public async Task<WebJavaScriptReplResultDto> ExecuteAsync(
        string sessionId,
        string toolCallId,
        string title,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var normalizedSessionId = NormalizeSessionId(sessionId);
        var request = new WebJavaScriptReplRequestDto(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(toolCallId) ? Guid.NewGuid().ToString("N") : toolCallId.Trim(),
            string.IsNullOrWhiteSpace(title) ? "Executing JavaScript" : title.Trim(),
            code,
            DateTimeOffset.UtcNow);
        var pending = new PendingExecution(normalizedSessionId, request);

        if (!_pending.TryAdd(request.Id, pending))
        {
            throw new InvalidOperationException($"Duplicate JavaScript REPL request id: {request.Id}");
        }

        var queue = GetQueue(normalizedSessionId);
        if (!queue.Requests.Writer.TryWrite(request))
        {
            _pending.TryRemove(request.Id, out _);
            throw new InvalidOperationException("JavaScript REPL request queue is closed.");
        }

        using var timeout = new CancellationTokenSource(_executionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (_pending.TryRemove(request.Id, out _))
            {
                return new WebJavaScriptReplResultDto(
                    "JavaScript REPL browser execution timed out waiting for an active WebUi page to execute the request.",
                    IsError: true,
                    Files: []);
            }

            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _pending.TryRemove(request.Id, out _);
            }
        }
    }

    public async Task<WebJavaScriptReplRequestDto?> WaitForNextAsync(
        string sessionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var queue = GetQueue(NormalizeSessionId(sessionId));
        var waitTimeout = timeout is { } value && value > TimeSpan.Zero
            ? value
            : DefaultPollTimeout;

        using var timeoutCts = new CancellationTokenSource(waitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        while (true)
        {
            try
            {
                var request = await queue.Requests.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (_pending.ContainsKey(request.Id))
                {
                    return request;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    public bool Complete(string sessionId, string requestId, WebJavaScriptReplResultRequest result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(result);

        if (!_pending.TryGetValue(requestId, out var pending) ||
            !string.Equals(pending.SessionId, NormalizeSessionId(sessionId), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_pending.TryRemove(requestId, out _))
        {
            return false;
        }

        var output = NormalizeOutput(result);
        pending.Completion.TrySetResult(new WebJavaScriptReplResultDto(
            output,
            result.IsError,
            result.Files ?? []));
        return true;
    }

    public void CancelSession(string sessionId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = NormalizeSessionId(sessionId);
        foreach (var pending in _pending.Values.Where(pending => string.Equals(pending.SessionId, normalizedSessionId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (_pending.TryRemove(pending.Request.Id, out _))
            {
                pending.Completion.TrySetResult(new WebJavaScriptReplResultDto(
                    string.IsNullOrWhiteSpace(reason) ? "JavaScript REPL request was cancelled." : reason,
                    IsError: true,
                    Files: []));
            }
        }
    }

    private SessionQueue GetQueue(string sessionId) =>
        _queues.GetOrAdd(sessionId, static _ => new SessionQueue());

    private static string NormalizeSessionId(string sessionId) => sessionId.Trim();

    private static string NormalizeOutput(WebJavaScriptReplResultRequest result)
    {
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            return result.Output.TrimEnd();
        }

        if (result.IsError)
        {
            return string.IsNullOrWhiteSpace(result.Error)
                ? "JavaScript REPL execution failed."
                : result.Error.TrimEnd();
        }

        return "Code executed successfully (no output)";
    }

    private sealed class SessionQueue
    {
        public Channel<WebJavaScriptReplRequestDto> Requests { get; } = Channel.CreateUnbounded<WebJavaScriptReplRequestDto>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
    }

    private sealed record PendingExecution(
        string SessionId,
        WebJavaScriptReplRequestDto Request)
    {
        public TaskCompletionSource<WebJavaScriptReplResultDto> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
