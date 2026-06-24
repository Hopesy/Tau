using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public sealed class OpenAiCodexResponsesProvider : IStreamProvider, IDisposable
{
    private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
    private const string WebSocketBetaHeader = "responses_websockets=2026-02-06";
    private static readonly TimeSpan SessionWebSocketCacheTtl = TimeSpan.FromMinutes(5);
    private const int MaxRetries = 3;
    private readonly HttpClient _httpClient;
    private readonly ICodexWebSocketTransport _webSocketTransport;
    private readonly ConcurrentDictionary<string, CachedCodexWebSocketConnection> _webSocketSessionCache = new(StringComparer.Ordinal);
    private readonly IDisposable _sessionCleanupRegistration;

    public OpenAiCodexResponsesProvider(
        HttpClient? httpClient = null,
        ICodexWebSocketTransport? webSocketTransport = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _webSocketTransport = webSocketTransport ?? new ClientCodexWebSocketTransport();
        _sessionCleanupRegistration = SessionResources.RegisterSessionResourceCleanup(CloseOpenAiCodexWebSocketSessions);
    }

    public string Api => "openai-codex-responses";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                StreamOptionHelpers.PushAborted(stream, model, Api);
            }
            catch (Exception ex)
            {
                stream.Push(new ErrorEvent(ex.Message));
            }
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        var reasoningEffort = OpenAiResponsesShared.MapReasoningEffort(options.Reasoning, model);
        var responseOptions = new OpenAiCodexResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens ?? model.MaxOutputTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            MaxRetryDelay = options.MaxRetryDelay,
            Metadata = options.Metadata,
            Transport = options.Transport,
            ReasoningEffort = reasoningEffort
        };
        return Stream(model, context, responseOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            stream.Push(new ErrorEvent($"No API key for provider: {model.Provider}"));
            return;
        }

        var accountId = OpenAiResponsesShared.ExtractAccountIdFromJwt(options.ApiKey!);
        var url = ResolveCodexUrl(model.BaseUrl);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options)).ConfigureAwait(false);
        var bodyJson = JsonSerializer.Serialize(body, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
        List<AssistantMessageDiagnostic>? diagnostics = null;

        if (options.Transport is StreamTransport.WebSocket or StreamTransport.Auto)
        {
            var webSocketStarted = false;
            try
            {
                await StreamWebSocketAsync(
                    model,
                    options,
                    stream,
                    accountId,
                    url,
                    body,
                    () => webSocketStarted = true).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                var diagnostic = CreateTransportFailureDiagnostic(options.Transport, webSocketStarted, ex, bodyJson);
                if (options.Transport is StreamTransport.WebSocket || webSocketStarted)
                {
                    stream.Push(new ErrorEvent(
                        ex.Message,
                        Message: new AssistantMessage
                        {
                            Api = Api,
                            Provider = model.Provider,
                            Model = model.Id,
                            StopReason = StopReason.Error,
                            ErrorMessage = ex.Message,
                            Diagnostics = [diagnostic],
                            Timestamp = DateTimeOffset.UtcNow
                        }));
                    return;
                }

                diagnostics ??= [];
                diagnostics.Add(diagnostic);
            }
        }

        HttpResponseMessage? response = null;
        string? lastError = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            ApplyCodexHeaders(request, model, options, accountId);
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, options.Signal).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                break;
            }

            lastError = await response.Content.ReadAsStringAsync(options.Signal).ConfigureAwait(false);
            if (attempt == MaxRetries || !OpenAiResponsesShared.IsRetryableError((int)response.StatusCode, lastError))
            {
                stream.Push(new ErrorEvent($"{Api} error {(int)response.StatusCode}: {lastError}"));
                return;
            }

            response.Dispose();
            await Task.Delay(GetRetryDelay(options, attempt), options.Signal).ConfigureAwait(false);
        }

        if (response is null)
        {
            stream.Push(new ErrorEvent(lastError ?? "Codex Responses request failed."));
            return;
        }

        using (response)
        {
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
            var partial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Diagnostics = diagnostics,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            await using var responseStream = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
            await OpenAiResponsesShared.ProcessResponsesStreamAsync(
                responseStream,
                partial,
                stream,
                OpenAiResponsesShared.MapCodexEvent,
                requestedServiceTier: (options as OpenAiCodexResponsesOptions)?.ServiceTier,
                resolveServiceTier: ResolveCodexServiceTier,
                cancellationToken: options.Signal).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["input"] = OpenAiResponsesShared.ConvertResponsesMessages(model, context, includeSystemPrompt: false),
            ["stream"] = true,
            ["store"] = false,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["text"] = new Dictionary<string, object> { ["verbosity"] = "medium" },
            ["include"] = new List<object> { "reasoning.encrypted_content" }
        };

        if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
        {
            body["instructions"] = context.SystemPrompt!;
        }

        var tools = OpenAiResponsesShared.ConvertResponsesTools(context.Tools);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (options.MaxTokens.HasValue)
        {
            body["max_output_tokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = options.SessionId!;
        }

        if (options is OpenAiCodexResponsesOptions codexOptions)
        {
            AddCodexOptions(body, model, codexOptions);
        }

        return body;
    }

    private static void AddCodexOptions(Dictionary<string, object> body, Model model, OpenAiCodexResponsesOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort) ||
            !string.IsNullOrWhiteSpace(options.ReasoningSummary))
        {
            var reasoning = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            {
                reasoning["effort"] = ClampCodexReasoningEffort(model.Id, options.ReasoningEffort!);
            }

            reasoning["summary"] = string.IsNullOrWhiteSpace(options.ReasoningSummary) ? "auto" : options.ReasoningSummary!;

            body["reasoning"] = reasoning;
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceTier))
        {
            body["service_tier"] = options.ServiceTier!;
        }

        if (!string.IsNullOrWhiteSpace(options.TextVerbosity))
        {
            body["text"] = new Dictionary<string, object> { ["verbosity"] = options.TextVerbosity! };
        }
    }

    private async Task StreamWebSocketAsync(
        Model model,
        StreamOptions options,
        AssistantMessageStream stream,
        string accountId,
        string sseUrl,
        Dictionary<string, object> body,
        Action onStarted)
    {
        var requestId = string.IsNullOrWhiteSpace(options.SessionId)
            ? CreateCodexRequestId()
            : options.SessionId!;
        var headers = BuildWebSocketHeaders(model, options, accountId, requestId);
        var webSocketUrl = ResolveCodexWebSocketUrl(sseUrl);
        var lease = await AcquireWebSocketAsync(webSocketUrl, headers, options.SessionId, options.Signal).ConfigureAwait(false);
        var keepConnection = true;
        var released = false;
        void Release(bool keep)
        {
            if (released)
            {
                return;
            }

            lease.Release(keep);
            released = true;
        }

        try
        {
            var frame = new Dictionary<string, object>(body, StringComparer.Ordinal)
            {
                ["type"] = "response.create"
            };
            var frameJson = JsonSerializer.Serialize(frame, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
            await lease.Connection.SendTextAsync(frameJson, options.Signal).ConfigureAwait(false);
            onStarted();

            var partial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            var completed = await OpenAiResponsesShared.ProcessResponsesJsonEventsAsync(
                lease.Connection.ReadTextMessagesAsync(options.Signal),
                partial,
                stream,
                OpenAiResponsesShared.MapCodexEvent,
                beforeDone: () => Release(keep: true),
                requestedServiceTier: (options as OpenAiCodexResponsesOptions)?.ServiceTier,
                resolveServiceTier: ResolveCodexServiceTier,
                cancellationToken: options.Signal).ConfigureAwait(false);
            if (!completed)
            {
                keepConnection = false;
                throw new InvalidOperationException("WebSocket stream closed before response.completed");
            }
        }
        catch
        {
            keepConnection = false;
            throw;
        }
        finally
        {
            Release(keepConnection);
        }
    }

    private async Task<CodexWebSocketLease> AcquireWebSocketAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var connection = await _webSocketTransport.ConnectAsync(url, headers, cancellationToken).ConfigureAwait(false);
            return new CodexWebSocketLease(connection, _ => CloseWebSocketSilently(connection));
        }

        if (_webSocketSessionCache.TryGetValue(sessionId!, out var cached))
        {
            lock (cached.SyncRoot)
            {
                cached.IdleTimer?.Dispose();
                cached.IdleTimer = null;
                if (!cached.Busy && IsWebSocketReusable(cached.Connection))
                {
                    cached.Busy = true;
                    return new CodexWebSocketLease(cached.Connection, keep =>
                    {
                        lock (cached.SyncRoot)
                        {
                            if (!keep || !IsWebSocketReusable(cached.Connection))
                            {
                                CloseWebSocketSilently(cached.Connection);
                                cached.IdleTimer?.Dispose();
                                _webSocketSessionCache.TryRemove(sessionId!, out _);
                                return;
                            }

                            cached.Busy = false;
                            ScheduleSessionWebSocketExpiry(sessionId!, cached);
                        }
                    });
                }

                if (!cached.Busy && !IsWebSocketReusable(cached.Connection))
                {
                    CloseWebSocketSilently(cached.Connection);
                    cached.IdleTimer?.Dispose();
                    _webSocketSessionCache.TryRemove(sessionId!, out _);
                }
            }

            if (cached.Busy)
            {
                var overflowConnection = await _webSocketTransport.ConnectAsync(url, headers, cancellationToken).ConfigureAwait(false);
                return new CodexWebSocketLease(overflowConnection, _ => CloseWebSocketSilently(overflowConnection));
            }
        }

        var newConnection = await _webSocketTransport.ConnectAsync(url, headers, cancellationToken).ConfigureAwait(false);
        var entry = new CachedCodexWebSocketConnection(newConnection)
        {
            Busy = true
        };
        _webSocketSessionCache[sessionId!] = entry;
        return new CodexWebSocketLease(newConnection, keep =>
        {
            lock (entry.SyncRoot)
            {
                if (!keep || !IsWebSocketReusable(entry.Connection))
                {
                    CloseWebSocketSilently(entry.Connection);
                    entry.IdleTimer?.Dispose();
                    if (_webSocketSessionCache.TryGetValue(sessionId!, out var current) &&
                        ReferenceEquals(current, entry))
                    {
                        _webSocketSessionCache.TryRemove(sessionId!, out _);
                    }
                    return;
                }

                entry.Busy = false;
                ScheduleSessionWebSocketExpiry(sessionId!, entry);
            }
        });
    }

    public void CloseOpenAiCodexWebSocketSessions(string? sessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (_webSocketSessionCache.TryRemove(sessionId!, out var entry))
            {
                CloseCachedWebSocket(entry, "debug_close");
            }

            return;
        }

        foreach (var key in _webSocketSessionCache.Keys)
        {
            if (_webSocketSessionCache.TryRemove(key, out var entry))
            {
                CloseCachedWebSocket(entry, "debug_close");
            }
        }
    }

    private void ScheduleSessionWebSocketExpiry(string sessionId, CachedCodexWebSocketConnection entry)
    {
        entry.IdleTimer?.Dispose();
        entry.IdleTimer = new Timer(_state =>
        {
            lock (entry.SyncRoot)
            {
                if (entry.Busy)
                {
                    return;
                }

                CloseWebSocketSilently(entry.Connection);
                if (_webSocketSessionCache.TryGetValue(sessionId, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _webSocketSessionCache.TryRemove(sessionId, out _);
                }
            }
        }, null, SessionWebSocketCacheTtl, Timeout.InfiniteTimeSpan);
    }

    private static void ApplyCodexHeaders(
        HttpRequestMessage request,
        Model model,
        StreamOptions options,
        string accountId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("originator", "tau");
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation("session_id", options.SessionId);
            request.Headers.TryAddWithoutValidation("x-client-request-id", options.SessionId);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", $"tau ({RuntimeInformation.OSDescription})");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildWebSocketHeaders(
        Model model,
        StreamOptions options,
        string accountId,
        string requestId)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyHeaders(headers, model.Headers);
        CopyHeaders(headers, options.Headers);
        headers["Authorization"] = $"Bearer {options.ApiKey}";
        headers["chatgpt-account-id"] = accountId;
        headers["originator"] = "tau";
        headers["User-Agent"] = $"tau ({RuntimeInformation.OSDescription})";
        headers["OpenAI-Beta"] = WebSocketBetaHeader;
        headers["x-client-request-id"] = requestId;
        headers["session_id"] = requestId;
        return headers;
    }

    private static AssistantMessageDiagnostic CreateTransportFailureDiagnostic(
        StreamTransport configuredTransport,
        bool webSocketStarted,
        Exception error,
        string requestBody)
    {
        return AssistantMessageDiagnostics.CreateAssistantMessageDiagnostic(
            "provider_transport_failure",
            error,
            new Dictionary<string, object?>
            {
                ["configuredTransport"] = FormatTransport(configuredTransport),
                ["fallbackTransport"] = webSocketStarted ? null : "sse",
                ["eventsEmitted"] = webSocketStarted,
                ["phase"] = webSocketStarted ? "after_message_stream_start" : "before_message_stream_start",
                ["requestBytes"] = Encoding.UTF8.GetByteCount(requestBody)
            });
    }

    private static string FormatTransport(StreamTransport transport) =>
        transport switch
        {
            StreamTransport.Auto => "auto",
            StreamTransport.Sse => "sse",
            StreamTransport.WebSocket => "websocket",
            _ => transport.ToString()
        };

    private static void CopyHeaders(IDictionary<string, string> target, IDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static string ResolveCodexUrl(string? baseUrl)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        var normalized = raw.TrimEnd('/');
        if (normalized.EndsWith("/codex/responses", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/codex", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/responses";
        }

        return $"{normalized}/codex/responses";
    }

    private static string? ResolveCodexServiceTier(string? responseServiceTier, string? requestServiceTier)
    {
        if (string.Equals(responseServiceTier, "default", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(requestServiceTier, "flex", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(requestServiceTier, "priority", StringComparison.OrdinalIgnoreCase)))
        {
            return requestServiceTier;
        }

        return responseServiceTier ?? requestServiceTier;
    }

    private static Uri ResolveCodexWebSocketUrl(string sseUrl)
    {
        var builder = new UriBuilder(sseUrl);
        builder.Scheme = builder.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => builder.Scheme
        };
        return builder.Uri;
    }

    private static bool IsWebSocketReusable(ICodexWebSocketConnection connection) =>
        connection.State == System.Net.WebSockets.WebSocketState.Open;

    private static void CloseCachedWebSocket(CachedCodexWebSocketConnection entry, string reason)
    {
        lock (entry.SyncRoot)
        {
            entry.IdleTimer?.Dispose();
            entry.IdleTimer = null;
            entry.Busy = false;
            CloseWebSocketSilently(entry.Connection, reason);
        }
    }

    private static void CloseWebSocketSilently(ICodexWebSocketConnection connection, string reason = "done")
    {
        try
        {
            connection.CloseAsync(1000, reason).AsTask().GetAwaiter().GetResult();
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string CreateCodexRequestId() =>
        $"codex_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..36];

    public void Dispose()
    {
        _sessionCleanupRegistration.Dispose();
        CloseOpenAiCodexWebSocketSessions();
        GC.SuppressFinalize(this);
    }

    private static string ClampCodexReasoningEffort(string modelId, string effort)
    {
        var id = modelId.Contains('/', StringComparison.Ordinal) ? modelId.Split('/').Last() : modelId;
        if ((id.StartsWith("gpt-5.2", StringComparison.OrdinalIgnoreCase) ||
             id.StartsWith("gpt-5.3", StringComparison.OrdinalIgnoreCase) ||
             id.StartsWith("gpt-5.4", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(effort, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(id, "gpt-5.1", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(effort, "xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        if (string.Equals(id, "gpt-5.1-codex-mini", StringComparison.OrdinalIgnoreCase))
        {
            return effort is "high" or "xhigh" ? "high" : "medium";
        }

        return effort;
    }

    private static TimeSpan GetRetryDelay(StreamOptions options, int attempt)
    {
        var computed = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return options.MaxRetryDelay.HasValue && options.MaxRetryDelay.Value < computed
            ? options.MaxRetryDelay.Value
            : computed;
    }

    private sealed class CachedCodexWebSocketConnection
    {
        public CachedCodexWebSocketConnection(ICodexWebSocketConnection connection)
        {
            Connection = connection;
        }

        public ICodexWebSocketConnection Connection { get; }
        public bool Busy { get; set; }
        public Timer? IdleTimer { get; set; }
        public object SyncRoot { get; } = new();
    }

    private sealed record CodexWebSocketLease(
        ICodexWebSocketConnection Connection,
        Action<bool> Release);
}
