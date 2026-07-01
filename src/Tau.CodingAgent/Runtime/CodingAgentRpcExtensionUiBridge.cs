using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentRpcExtensionUiBridge
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IPendingExtensionUiRequest> _pending = new(StringComparer.Ordinal);
    private Func<object, CancellationToken, Task>? _writeRequestAsync;
    private CodingAgentFooterDataProvider? _footerDataProvider;

    internal void Attach(Func<object, CancellationToken, Task> writeRequestAsync)
    {
        ArgumentNullException.ThrowIfNull(writeRequestAsync);

        lock (_gate)
        {
            _writeRequestAsync = writeRequestAsync;
        }
    }

    public void SetFooterDataProvider(CodingAgentFooterDataProvider? footerDataProvider)
    {
        lock (_gate)
        {
            _footerDataProvider = footerDataProvider;
        }
    }

    public Task<string?> SelectAsync(
        string title,
        IReadOnlyList<string> options,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(options);

        return RequestAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "select",
                ["title"] = title,
                ["options"] = options.ToArray()
            },
            defaultValue: null,
            parseResponse: ReadValueOrDefault,
            timeout,
            cancellationToken);
    }

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return RequestAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "confirm",
                ["title"] = title,
                ["message"] = message
            },
            defaultValue: false,
            parseResponse: static response =>
                IsCancelled(response)
                    ? false
                    : response.TryGetProperty("confirmed", out var confirmed) &&
                      confirmed.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                      confirmed.GetBoolean(),
            timeout,
            cancellationToken);
    }

    public Task<string?> InputAsync(
        string title,
        string? placeholder = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return RequestAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "input",
                ["title"] = title,
                ["placeholder"] = placeholder
            },
            defaultValue: null,
            parseResponse: ReadValueOrDefault,
            timeout,
            cancellationToken);
    }

    public Task<string?> EditorAsync(
        string title,
        string? prefill = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return RequestAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "editor",
                ["title"] = title,
                ["prefill"] = prefill
            },
            defaultValue: null,
            parseResponse: ReadValueOrDefault,
            timeout,
            cancellationToken);
    }

    public Task NotifyAsync(
        string message,
        string? notifyType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "notify",
                ["message"] = message,
                ["notifyType"] = notifyType
            },
            cancellationToken);
    }

    public Task SetStatusAsync(
        string statusKey,
        string? statusText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusKey);
        GetFooterDataProvider()?.SetExtensionStatus(statusKey, statusText);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setStatus",
                ["statusKey"] = statusKey,
                ["statusText"] = statusText
            },
            cancellationToken);
    }

    private CodingAgentFooterDataProvider? GetFooterDataProvider()
    {
        lock (_gate)
        {
            return _footerDataProvider;
        }
    }

    public Task SetWidgetAsync(
        string widgetKey,
        IReadOnlyList<string>? widgetLines,
        string? widgetPlacement = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetKey);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setWidget",
                ["widgetKey"] = widgetKey,
                ["widgetLines"] = widgetLines?.ToArray(),
                ["widgetPlacement"] = widgetPlacement
            },
            cancellationToken);
    }

    public Task SetFooterAsync(
        IReadOnlyList<string>? footerLines,
        CancellationToken cancellationToken = default)
    {
        GetFooterDataProvider()?.SetCustomFooterLines(footerLines);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setFooter",
                ["footerLines"] = footerLines?.ToArray()
            },
            cancellationToken);
    }

    public Task SetHeaderAsync(
        IReadOnlyList<string>? headerLines,
        CancellationToken cancellationToken = default)
    {
        GetFooterDataProvider()?.SetCustomHeaderLines(headerLines);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setHeader",
                ["headerLines"] = headerLines?.ToArray()
            },
            cancellationToken);
    }

    public Task SetWorkingMessageAsync(
        string? message,
        CancellationToken cancellationToken = default)
    {
        GetFooterDataProvider()?.SetWorkingMessage(message);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setWorkingMessage",
                ["workingMessage"] = message
            },
            cancellationToken);
    }

    public Task SetWorkingIndicatorAsync(
        IReadOnlyList<string>? frames,
        int? intervalMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        GetFooterDataProvider()?.SetWorkingIndicator(frames, intervalMilliseconds);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setWorkingIndicator",
                ["workingIndicatorFrames"] = frames?.ToArray(),
                ["workingIndicatorIntervalMs"] = intervalMilliseconds
            },
            cancellationToken);
    }

    public Task SetHiddenThinkingLabelAsync(
        string? label,
        CancellationToken cancellationToken = default)
    {
        GetFooterDataProvider()?.SetHiddenThinkingLabel(label);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setHiddenThinkingLabel",
                ["hiddenThinkingLabel"] = label
            },
            cancellationToken);
    }

    public Task SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "setTitle",
                ["title"] = title
            },
            cancellationToken);
    }

    public Task SetEditorTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        return SendFireAndForgetAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "set_editor_text",
                ["text"] = text
            },
            cancellationToken);
    }

    internal bool TryHandleResponse(JsonElement response)
    {
        var id = ReadString(response, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        IPendingExtensionUiRequest? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out pending))
            {
                return false;
            }
        }

        pending.TryComplete(response);
        return true;
    }

    private async Task<T> RequestAsync<T>(
        Dictionary<string, object?> request,
        T defaultValue,
        Func<JsonElement, T> parseResponse,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return defaultValue;
        }

        var writeRequestAsync = GetWriter();
        var id = CreateRequestId();
        request["type"] = "extension_ui_request";
        request["id"] = id;
        if (timeout is not null)
        {
            request["timeout"] = checked((int)Math.Ceiling(timeout.Value.TotalMilliseconds));
        }
        RemoveNullValues(request);

        var pending = new PendingExtensionUiRequest<T>(
            id,
            defaultValue,
            parseResponse,
            RemovePending);
        lock (_gate)
        {
            _pending[id] = pending;
        }

        CancellationTokenRegistration cancellationRegistration = default;
        if (cancellationToken.CanBeCanceled)
        {
            cancellationRegistration = cancellationToken.Register(static state =>
                ((IPendingExtensionUiRequest)state!).TryCompleteDefault(), pending);
        }

        if (timeout is not null)
        {
            _ = CompleteAfterTimeoutAsync(pending, timeout.Value);
        }

        try
        {
            await writeRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
            return await pending.Task.ConfigureAwait(false);
        }
        catch
        {
            pending.TryCompleteDefault();
            throw;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
            RemovePending(id);
        }
    }

    private async Task SendFireAndForgetAsync(
        Dictionary<string, object?> request,
        CancellationToken cancellationToken)
    {
        var writeRequestAsync = GetWriter();
        request["type"] = "extension_ui_request";
        request["id"] = CreateRequestId();
        RemoveNullValues(request);
        await writeRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Func<object, CancellationToken, Task> GetWriter()
    {
        lock (_gate)
        {
            return _writeRequestAsync ?? throw new InvalidOperationException("RPC extension UI bridge is not attached to a host.");
        }
    }

    private void RemovePending(string id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    private static async Task CompleteAfterTimeoutAsync(IPendingExtensionUiRequest pending, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout).ConfigureAwait(false);
            pending.TryCompleteDefault();
        }
        catch (ArgumentOutOfRangeException)
        {
            pending.TryCompleteDefault();
        }
    }

    private static string? ReadValueOrDefault(JsonElement response) =>
        IsCancelled(response) ? null : ReadString(response, "value");

    private static void RemoveNullValues(Dictionary<string, object?> request)
    {
        foreach (var name in request.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
        {
            request.Remove(name);
        }
    }

    private static bool IsCancelled(JsonElement response) =>
        response.TryGetProperty("cancelled", out var cancelled) &&
        cancelled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        cancelled.GetBoolean();

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string CreateRequestId() => Guid.NewGuid().ToString("D");

    private interface IPendingExtensionUiRequest
    {
        string Id { get; }

        void TryComplete(JsonElement response);

        void TryCompleteDefault();
    }

    private sealed class PendingExtensionUiRequest<T>(
        string id,
        T defaultValue,
        Func<JsonElement, T> parseResponse,
        Action<string> remove) : IPendingExtensionUiRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public string Id => id;

        public Task<T> Task => _completion.Task;

        public void TryComplete(JsonElement response)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                _completion.TrySetResult(parseResponse(response));
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                remove(id);
            }
        }

        public void TryCompleteDefault()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _completion.TrySetResult(defaultValue);
            remove(id);
        }
    }
}
