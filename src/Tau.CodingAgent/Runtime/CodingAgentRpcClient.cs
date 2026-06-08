using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentRpcClientOptions
{
    public string FileName { get; init; } = "Tau.CodingAgent";

    public IReadOnlyList<string> ProcessArguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string> AgentArguments { get; init; } = [];

    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record CodingAgentRpcResponse(
    string? Id,
    string Command,
    bool Success,
    JsonElement? Data,
    string? Error,
    JsonElement Raw);

public interface ICodingAgentRpcTransport : IAsyncDisposable
{
    event Action<string>? StdoutLineReceived;

    event Action<string>? StderrReceived;

    bool HasExited { get; }

    int? ExitCode { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task SendLineAsync(string line, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class CodingAgentRpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CodingAgentRpcClientOptions _options;
    private readonly Func<CodingAgentRpcClientOptions, ICodingAgentRpcTransport> _transportFactory;
    private readonly object _gate = new();
    private readonly List<Action<JsonElement>> _eventListeners = [];
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private ICodingAgentRpcTransport? _transport;
    private int _requestId;
    private string _stderr = string.Empty;

    public CodingAgentRpcClient(CodingAgentRpcClientOptions? options = null)
        : this(options ?? new CodingAgentRpcClientOptions(), static options => new CodingAgentRpcProcessTransport(options))
    {
    }

    internal CodingAgentRpcClient(
        ICodingAgentRpcTransport transport,
        CodingAgentRpcClientOptions? options = null)
        : this(options ?? new CodingAgentRpcClientOptions(), _ => transport)
    {
    }

    private CodingAgentRpcClient(
        CodingAgentRpcClientOptions options,
        Func<CodingAgentRpcClientOptions, ICodingAgentRpcTransport> transportFactory)
    {
        _options = options;
        _transportFactory = transportFactory;
    }

    public string Stderr
    {
        get
        {
            lock (_gate)
            {
                return _stderr;
            }
        }
    }

    internal int PendingRequestCount => _pending.Count;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is not null)
        {
            throw new InvalidOperationException("RPC client is already started.");
        }

        var transport = _transportFactory(_options);
        transport.StdoutLineReceived += HandleLine;
        transport.StderrReceived += HandleStderr;
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _transport = transport;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }

        transport.StdoutLineReceived -= HandleLine;
        transport.StderrReceived -= HandleStderr;
        RejectAllPending(new InvalidOperationException("RPC client stopped."));
        await transport.StopAsync(cancellationToken).ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
    }

    public IDisposable OnEvent(Action<JsonElement> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _eventListeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _eventListeners.Remove(listener);
            }
        });
    }

    public Task<CodingAgentRpcResponse> SendAsync(
        string type,
        IReadOnlyDictionary<string, object?>? properties = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return SendCommandAsync(type, properties, timeout, cancellationToken);
    }

    public Task PromptAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        string? streamingBehavior = null,
        CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "prompt",
            new Dictionary<string, object?>
            {
                ["message"] = message,
                ["images"] = images,
                ["streamingBehavior"] = streamingBehavior
            },
            cancellationToken: cancellationToken);

    public Task SteerAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "steer",
            new Dictionary<string, object?>
            {
                ["message"] = message,
                ["images"] = images
            },
            cancellationToken: cancellationToken);

    public Task FollowUpAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "follow_up",
            new Dictionary<string, object?>
            {
                ["message"] = message,
                ["images"] = images
            },
            cancellationToken: cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync("abort", cancellationToken: cancellationToken);

    public Task<JsonElement?> NewSessionAsync(
        string? parentSession = null,
        CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "new_session",
            new Dictionary<string, object?> { ["parentSession"] = parentSession },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> GetStateAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_state", cancellationToken: cancellationToken);

    public Task<JsonElement?> SetModelAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "set_model",
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["modelId"] = modelId
            },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> CycleModelAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("cycle_model", cancellationToken: cancellationToken);

    public Task<JsonElement?> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_available_models", cancellationToken: cancellationToken);

    public Task SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_thinking_level",
            new Dictionary<string, object?> { ["level"] = level },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> CycleThinkingLevelAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("cycle_thinking_level", cancellationToken: cancellationToken);

    public Task SetSteeringModeAsync(string mode, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_steering_mode",
            new Dictionary<string, object?> { ["mode"] = mode },
            cancellationToken: cancellationToken);

    public Task SetFollowUpModeAsync(string mode, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_follow_up_mode",
            new Dictionary<string, object?> { ["mode"] = mode },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> CompactAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "compact",
            new Dictionary<string, object?> { ["customInstructions"] = customInstructions },
            cancellationToken: cancellationToken);

    public Task SetAutoCompactionAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_auto_compaction",
            new Dictionary<string, object?> { ["enabled"] = enabled },
            cancellationToken: cancellationToken);

    public Task SetAutoRetryAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_auto_retry",
            new Dictionary<string, object?> { ["enabled"] = enabled },
            cancellationToken: cancellationToken);

    public Task AbortRetryAsync(CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync("abort_retry", cancellationToken: cancellationToken);

    public async Task<CodingAgentShellResult> BashAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var data = await SendForDataAsync(
                "bash",
                new Dictionary<string, object?> { ["command"] = command },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (data is null)
        {
            throw new InvalidOperationException("RPC bash response did not include data.");
        }

        return data.Value.Deserialize<CodingAgentShellResult>(JsonOptions)
            ?? throw new InvalidOperationException("RPC bash response data could not be deserialized.");
    }

    public Task AbortBashAsync(CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync("abort_bash", cancellationToken: cancellationToken);

    public Task<JsonElement?> GetSessionStatsAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_session_stats", cancellationToken: cancellationToken);

    public Task<JsonElement?> ExportHtmlAsync(
        string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "export_html",
            new Dictionary<string, object?> { ["outputPath"] = outputPath },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> SwitchSessionAsync(
        string sessionPath,
        CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "switch_session",
            new Dictionary<string, object?> { ["sessionPath"] = sessionPath },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> ForkAsync(string entryId, CancellationToken cancellationToken = default) =>
        SendForDataAsync(
            "fork",
            new Dictionary<string, object?> { ["entryId"] = entryId },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> CloneAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("clone", cancellationToken: cancellationToken);

    public Task<JsonElement?> GetForkMessagesAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_fork_messages", cancellationToken: cancellationToken);

    public async Task<string?> GetLastAssistantTextAsync(CancellationToken cancellationToken = default)
    {
        var data = await SendForDataAsync("get_last_assistant_text", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (data is null ||
            !data.Value.TryGetProperty("text", out var text) ||
            text.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return text.GetString();
    }

    public Task SetSessionNameAsync(string name, CancellationToken cancellationToken = default) =>
        SendAndRequireSuccessAsync(
            "set_session_name",
            new Dictionary<string, object?> { ["name"] = name },
            cancellationToken: cancellationToken);

    public Task<JsonElement?> GetMessagesAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_messages", cancellationToken: cancellationToken);

    public Task<JsonElement?> GetCommandsAsync(CancellationToken cancellationToken = default) =>
        SendForDataAsync("get_commands", cancellationToken: cancellationToken);

    public async Task WaitForIdleAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        subscription = OnEvent(evt =>
        {
            if (GetEventType(evt).Equals("agent_end", StringComparison.Ordinal))
            {
                completion.TrySetResult();
            }
        });

        await WaitForEventCompletionAsync(
            completion,
            subscription,
            linkedCts,
            timeoutCts,
            () => $"Timeout waiting for agent to become idle. Stderr: {Stderr}").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JsonElement>> CollectEventsAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var events = new List<JsonElement>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        subscription = OnEvent(evt =>
        {
            events.Add(evt.Clone());
            if (GetEventType(evt).Equals("agent_end", StringComparison.Ordinal))
            {
                completion.TrySetResult();
            }
        });

        return await CollectEventCompletionAsync(
            completion,
            subscription,
            linkedCts,
            timeoutCts,
            events,
            () => $"Timeout collecting events. Stderr: {Stderr}").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JsonElement>> PromptAndWaitAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var eventsTask = CollectEventsAsync(timeout, cancellationToken);
        await PromptAsync(message, images, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await eventsTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<CodingAgentRpcResponse> SendCommandAsync(
        string type,
        IReadOnlyDictionary<string, object?>? properties,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("RPC client is not started.");
        var id = $"req_{Interlocked.Increment(ref _requestId)}";
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["id"] = id
        };
        if (properties is not null)
        {
            foreach (var (name, value) in properties)
            {
                payload[name] = value;
            }
        }

        RemoveNullValues(payload);
        var line = CodingAgentRpcJsonl.SerializeLine(payload, JsonOptions);
        var pending = new PendingRequest(
            id,
            type,
            timeout ?? _options.RequestTimeout,
            () => Stderr,
            removedId => _pending.TryRemove(removedId, out _));
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException($"Duplicate RPC request id '{id}'.");
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var request = (PendingRequest)state!;
                request.Fail(new OperationCanceledException());
            },
            pending);

        try
        {
            await transport.SendLineAsync(line, cancellationToken).ConfigureAwait(false);
            return await pending.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            pending.Dispose();
            throw;
        }
    }

    private async Task SendAndRequireSuccessAsync(
        string type,
        IReadOnlyDictionary<string, object?>? properties = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(type, properties, timeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private async Task<JsonElement?> SendForDataAsync(
        string type,
        IReadOnlyDictionary<string, object?>? properties = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(type, properties, timeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Data;
    }

    private static void EnsureSuccess(CodingAgentRpcResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? $"RPC command '{response.Command}' failed.");
        }
    }

    private void HandleLine(string line)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (TryParsePendingResponse(root, out var id, out var response) &&
            id is not null &&
            _pending.TryRemove(id, out var pending))
        {
            pending.Complete(response);
            return;
        }

        DispatchEvent(root);
    }

    private void HandleStderr(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _stderr += text;
        }
    }

    private void DispatchEvent(JsonElement evt)
    {
        Action<JsonElement>[] listeners;
        lock (_gate)
        {
            listeners = _eventListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            listener(evt.Clone());
        }
    }

    private static bool TryParsePendingResponse(
        JsonElement root,
        out string? id,
        out CodingAgentRpcResponse response)
    {
        response = default!;
        id = null;
        if (!root.TryGetProperty("type", out var type) ||
            !type.GetString()!.Equals("response", StringComparison.Ordinal))
        {
            return false;
        }

        id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        var command = root.TryGetProperty("command", out var commandElement) &&
            commandElement.ValueKind == JsonValueKind.String
                ? commandElement.GetString()!
                : string.Empty;
        var success = root.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            successElement.GetBoolean();
        var error = root.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;
        JsonElement? data = root.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : null;
        response = new CodingAgentRpcResponse(id, command, success, data, error, root.Clone());
        return true;
    }

    private void RejectAllPending(Exception error)
    {
        foreach (var pending in _pending.Values.ToArray())
        {
            if (_pending.TryRemove(pending.Id, out _))
            {
                pending.Fail(error);
            }
        }
    }

    private static async Task WaitForEventCompletionAsync(
        TaskCompletionSource completion,
        IDisposable? subscription,
        CancellationTokenSource linkedCts,
        CancellationTokenSource timeoutCts,
        Func<string> timeoutMessage)
    {
        using var registration = linkedCts.Token.Register(static state =>
        {
            var (source, timeout, message) = ((TaskCompletionSource, CancellationTokenSource, Func<string>))state!;
            source.TrySetException(timeout.IsCancellationRequested
                ? new TimeoutException(message())
                : new OperationCanceledException());
        }, (completion, timeoutCts, timeoutMessage));
        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    private static async Task<IReadOnlyList<JsonElement>> CollectEventCompletionAsync(
        TaskCompletionSource completion,
        IDisposable? subscription,
        CancellationTokenSource linkedCts,
        CancellationTokenSource timeoutCts,
        List<JsonElement> events,
        Func<string> timeoutMessage)
    {
        await WaitForEventCompletionAsync(completion, subscription, linkedCts, timeoutCts, timeoutMessage)
            .ConfigureAwait(false);
        return events;
    }

    private static string GetEventType(JsonElement evt) =>
        evt.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;

    private static void RemoveNullValues(Dictionary<string, object?> request)
    {
        foreach (var name in request.Where(static pair => pair.Value is null).Select(static pair => pair.Key).ToArray())
        {
            request.Remove(name);
        }
    }

    private sealed class PendingRequest : IDisposable
    {
        private readonly TaskCompletionSource<CodingAgentRpcResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private readonly Func<string> _getStderr;
        private readonly Action<string> _remove;
        private int _completed;

        public PendingRequest(
            string id,
            string command,
            TimeSpan timeout,
            Func<string> getStderr,
            Action<string> remove)
        {
            Id = id;
            Command = command;
            _getStderr = getStderr;
            _remove = remove;
            _timeout = new CancellationTokenSource(timeout);
            _timeoutRegistration = _timeout.Token.Register(static state =>
            {
                var request = (PendingRequest)state!;
                request.Fail(new TimeoutException($"Timeout waiting for response to {request.Command}. Stderr: {request._getStderr()}"));
            }, this);
        }

        public string Id { get; }

        public string Command { get; }

        public Task<CodingAgentRpcResponse> Task => _completion.Task;

        public void Complete(CodingAgentRpcResponse response)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _completion.TrySetResult(response);
            Dispose();
        }

        public void Fail(Exception error)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _remove(Id);
            _completion.TrySetException(error);
            Dispose();
        }

        public void Dispose()
        {
            _timeoutRegistration.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                unsubscribe();
            }
        }
    }
}

public sealed class CodingAgentRpcProcessTransport : ICodingAgentRpcTransport
{
    private readonly CodingAgentRpcClientOptions _options;
    private readonly CancellationTokenSource _readCancellation = new();
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    public CodingAgentRpcProcessTransport(CodingAgentRpcClientOptions options)
    {
        _options = options;
    }

    public event Action<string>? StdoutLineReceived;

    public event Action<string>? StderrReceived;

    public bool HasExited => _process?.HasExited ?? false;

    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("RPC process transport is already started.");
        }

        var process = new Process { StartInfo = CreateStartInfo(_options) };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start RPC process.");
        }

        _process = process;
        _stdoutTask = ReadStdoutAsync(process.StandardOutput, _readCancellation.Token);
        _stderrTask = ReadStderrAsync(process.StandardError, _readCancellation.Token);
        if (_options.StartupDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.StartupDelay, cancellationToken).ConfigureAwait(false);
        }

        if (process.HasExited)
        {
            throw new InvalidOperationException($"Agent process exited immediately with code {process.ExitCode}.");
        }
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("RPC process transport is not started.");
        if (process.HasExited)
        {
            throw new InvalidOperationException($"RPC process has exited with code {process.ExitCode}.");
        }

        await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.StopTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _readCancellation.Cancel();
        await WaitForReaderAsync(_stdoutTask).ConfigureAwait(false);
        await WaitForReaderAsync(_stderrTask).ConfigureAwait(false);
        _process = null;
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _readCancellation.Dispose();
        _process?.Dispose();
    }

    internal static ProcessStartInfo CreateStartInfo(CodingAgentRpcClientOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        foreach (var argument in options.ProcessArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        if (!string.IsNullOrWhiteSpace(options.Provider))
        {
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(options.Provider);
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(options.Model);
        }

        foreach (var argument in options.AgentArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.Environment is not null)
        {
            foreach (var (name, value) in options.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lineBuffer = new CodingAgentRpcJsonlLineBuffer(line => StdoutLineReceived?.Invoke(line));
        var buffer = new char[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            lineBuffer.Append(buffer.AsSpan(0, read));
        }

        lineBuffer.Complete();
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            StderrReceived?.Invoke(new string(buffer, 0, read));
        }
    }

    private static async Task WaitForReaderAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}

internal static class CodingAgentRpcJsonl
{
    public static string SerializeLine(object value, JsonSerializerOptions? options = null) =>
        $"{JsonSerializer.Serialize(value, options)}\n";
}

internal sealed class CodingAgentRpcJsonlLineBuffer
{
    private readonly Action<string> _onLine;
    private readonly StringBuilder _buffer = new();

    public CodingAgentRpcJsonlLineBuffer(Action<string> onLine)
    {
        _onLine = onLine;
    }

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                Emit();
            }
            else
            {
                _buffer.Append(character);
            }
        }
    }

    public void Complete()
    {
        if (_buffer.Length > 0)
        {
            Emit();
        }
    }

    private void Emit()
    {
        var line = _buffer.ToString();
        _buffer.Clear();
        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }

        _onLine(line);
    }
}
