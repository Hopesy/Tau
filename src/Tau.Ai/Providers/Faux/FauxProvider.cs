using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Faux;

public sealed record FauxTokenSize(int? Min = null, int? Max = null);

public sealed record FauxModelDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<string>? InputModalities { get; init; }
    public ModelCost? Cost { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxOutputTokens { get; init; }
}

public sealed record FauxProviderOptions
{
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public IReadOnlyList<FauxModelDefinition>? Models { get; init; }
    public double? TokensPerSecond { get; init; }
    public FauxTokenSize? TokenSize { get; init; }
    /// <summary>deferred 响应测试配置。</summary>
    public FauxDeferredOptions? Deferred { get; init; }
}

/// <summary>
/// Faux provider 的 deferred 响应行为配置。
/// </summary>
/// <param name="PendingFetches">在最终结果就绪前返回句柄的轮询次数。</param>
/// <param name="PollAfterMs">返回句柄时建议等待的毫秒数。</param>
public sealed record FauxDeferredOptions(int PendingFetches = 0, int? PollAfterMs = null);

public sealed class FauxProviderState
{
    public int CallCount { get; internal set; }
    /// <summary>已执行 deferred 拉取次数。</summary>
    public int DeferredFetchCount { get; internal set; }
    /// <summary>已取消的 deferred 句柄。</summary>
    public IList<DeferredHandle> CancelledDeferred { get; } = new List<DeferredHandle>();
}

public delegate ValueTask<AssistantMessage> FauxResponseFactory(
    LlmContext context,
    StreamOptions options,
    FauxProviderState state,
    Model model);

public readonly struct FauxResponseStep
{
    private FauxResponseStep(AssistantMessage? message, FauxResponseFactory? factory)
    {
        Message = message;
        Factory = factory;
    }

    internal AssistantMessage? Message { get; }

    internal FauxResponseFactory? Factory { get; }

    public static FauxResponseStep FromMessage(AssistantMessage message) => new(message, null);

    public static FauxResponseStep FromFactory(FauxResponseFactory factory) => new(null, factory);

    public static implicit operator FauxResponseStep(AssistantMessage message) => FromMessage(message);

    public ValueTask<AssistantMessage> ResolveAsync(
        LlmContext context,
        StreamOptions options,
        FauxProviderState state,
        Model model)
    {
        if (Message is not null)
        {
            return ValueTask.FromResult(Message);
        }

        if (Factory is not null)
        {
            return Factory(context, options, state, model);
        }

        return ValueTask.FromException<AssistantMessage>(
            new InvalidOperationException("Faux response step is empty."));
    }
}

public sealed class FauxProviderRegistration
{
    private readonly ProviderRegistry _registry;
    private readonly string _sourceId;
    private readonly FauxStreamProvider _provider;

    internal FauxProviderRegistration(
        ProviderRegistry registry,
        string sourceId,
        FauxStreamProvider provider,
        IReadOnlyList<Model> models)
    {
        _registry = registry;
        _sourceId = sourceId;
        _provider = provider;
        Models = models;
    }

    public string Api => _provider.Api;

    public IReadOnlyList<Model> Models { get; }

    public FauxProviderState State => _provider.State;

    public Model GetModel() => Models[0];

    public Model? GetModel(string modelId) =>
        Models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    public void SetResponses(IEnumerable<FauxResponseStep> responses) =>
        _provider.SetResponses(responses);

    public void AppendResponses(IEnumerable<FauxResponseStep> responses) =>
        _provider.AppendResponses(responses);

    public int GetPendingResponseCount() => _provider.PendingResponseCount;

    public void Unregister() => _registry.UnregisterBySource(_sourceId);
}

public static class Faux
{
    private const string DefaultApi = "faux";
    private const string DefaultProvider = "faux";
    private const string DefaultModelId = "faux-1";
    private const string DefaultModelName = "Faux Model";
    private const string DefaultBaseUrl = "http://localhost:0";
    private const int DefaultMinTokenSize = 3;
    private const int DefaultMaxTokenSize = 5;

    public static TextContent Text(string text) => new(text);

    public static ThinkingContent Thinking(string thinking) => new(thinking);

    public static ToolCallContent ToolCall(string name, string argumentsJson, string? id = null) =>
        new(id ?? RandomId("tool"), name, argumentsJson);

    public static ToolCallContent ToolCall(string name, JsonElement arguments, string? id = null) =>
        ToolCall(name, arguments.GetRawText(), id);

    public static ToolCallContent ToolCall(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        string? id = null)
    {
        var json = JsonSerializer.Serialize(arguments, FauxJsonContext.Default.IReadOnlyDictionaryStringObject);
        return ToolCall(name, json, id);
    }

    public static AssistantMessage AssistantMessage(
        string text,
        StopReason stopReason = StopReason.EndTurn,
        string? errorMessage = null,
        string? responseId = null,
        DateTimeOffset? timestamp = null) =>
        AssistantMessage([Text(text)], stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        ContentBlock content,
        StopReason stopReason = StopReason.EndTurn,
        string? errorMessage = null,
        string? responseId = null,
        DateTimeOffset? timestamp = null) =>
        AssistantMessage([content], stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        IReadOnlyList<ContentBlock> content,
        StopReason stopReason = StopReason.EndTurn,
        string? errorMessage = null,
        string? responseId = null,
        DateTimeOffset? timestamp = null) =>
        new(content)
        {
            Api = DefaultApi,
            Provider = DefaultProvider,
            Model = DefaultModelId,
            Usage = new Usage(0, 0, 0, 0),
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            ResponseId = responseId,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    public static FauxProviderRegistration Register(
        ProviderRegistry registry,
        FauxProviderOptions? options = null)
    {
        options ??= new FauxProviderOptions();
        var api = options.Api ?? RandomId(DefaultApi);
        var provider = options.Provider ?? DefaultProvider;
        var sourceId = RandomId("faux-provider");
        var minTokenSize = Math.Max(
            1,
            Math.Min(options.TokenSize?.Min ?? DefaultMinTokenSize, options.TokenSize?.Max ?? DefaultMaxTokenSize));
        var maxTokenSize = Math.Max(minTokenSize, options.TokenSize?.Max ?? DefaultMaxTokenSize);

        var models = BuildModels(api, provider, options.Models);
        var streamProvider = new FauxStreamProvider(
            api,
            provider,
            models,
            options.TokensPerSecond,
            minTokenSize,
            maxTokenSize,
            options.Deferred);

        registry.Register(api, streamProvider, sourceId);
        return new FauxProviderRegistration(registry, sourceId, streamProvider, models);
    }

    internal static string RandomId(string prefix) =>
        $"{prefix}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";

    private static IReadOnlyList<Model> BuildModels(
        string api,
        string provider,
        IReadOnlyList<FauxModelDefinition>? definitions)
    {
        var modelDefinitions = definitions is { Count: > 0 }
            ? definitions
            : [
                new FauxModelDefinition
                {
                    Id = DefaultModelId,
                    Name = DefaultModelName,
                    Reasoning = false,
                    InputModalities = ["text", "image"],
                    Cost = new ModelCost(0, 0, 0, 0),
                    ContextWindow = 128_000,
                    MaxOutputTokens = 16_384
                }
            ];

        return modelDefinitions.Select(definition => new Model
        {
            Id = definition.Id,
            Name = definition.Name ?? definition.Id,
            Api = api,
            Provider = provider,
            BaseUrl = DefaultBaseUrl,
            Reasoning = definition.Reasoning,
            InputModalities = definition.InputModalities ?? ["text", "image"],
            Cost = definition.Cost ?? new ModelCost(0, 0, 0, 0),
            ContextWindow = definition.ContextWindow ?? 128_000,
            MaxOutputTokens = definition.MaxOutputTokens ?? 16_384
        }).ToArray();
    }
}

internal sealed class FauxStreamProvider : IStreamProvider
{
    private readonly object _gate = new();
    private readonly Queue<FauxResponseStep> _pendingResponses = new();
    private readonly string _provider;
    private readonly IReadOnlyList<Model> _models;
    private readonly double? _tokensPerSecond;
    private readonly int _minTokenSize;
    private readonly int _maxTokenSize;
    private readonly FauxDeferredOptions? _deferredOptions;
    private readonly Dictionary<string, string> _promptCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredResponse> _deferredResponses = new(StringComparer.Ordinal);

    public FauxStreamProvider(
        string api,
        string provider,
        IReadOnlyList<Model> models,
        double? tokensPerSecond,
        int minTokenSize,
        int maxTokenSize,
        FauxDeferredOptions? deferredOptions)
    {
        Api = api;
        _provider = provider;
        _models = models;
        _tokensPerSecond = tokensPerSecond;
        _minTokenSize = minTokenSize;
        _maxTokenSize = maxTokenSize;
        _deferredOptions = deferredOptions;
    }

    public string Api { get; }

    public FauxProviderState State { get; } = new();

    public int PendingResponseCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingResponses.Count;
            }
        }
    }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        var step = DequeueResponseStep();

        _ = ProduceAsync(stream, step, model, context, options);
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, options);

    /// <summary>
    /// 拉取 Faux provider 已提交的 deferred 响应。
    /// </summary>
    /// <param name="model">请求使用的模型。</param>
    /// <param name="handle">提交时返回的句柄。</param>
    /// <param name="options">拉取选项。</param>
    /// <returns>包含 pending 句柄或最终结果的事件流。</returns>
    public AssistantMessageStream FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions options)
    {
        var stream = new AssistantMessageStream();
        lock (_gate) State.DeferredFetchCount++;
        _ = FetchDeferredAsync(stream, model, handle, options);
        return stream;
    }

    /// <summary>
    /// 取消 Faux provider 已提交的 deferred 响应。
    /// </summary>
    /// <param name="model">请求使用的模型。</param>
    /// <param name="handle">提交时返回的句柄。</param>
    /// <param name="options">取消选项。</param>
    /// <returns>取消完成任务。</returns>
    public async Task CancelDeferred(Model model, DeferredHandle handle, DeferredCancelOptions options)
    {
        lock (_gate)
        {
            State.CancelledDeferred.Add(handle with { Data = CloneDeferredData(handle.Data) });
            if (_deferredResponses.TryGetValue(handle.Id, out var response)) response.Cancelled = true;
        }

        if (options.OnResponse is not null)
        {
            await options.OnResponse(new ProviderResponse(200, new Dictionary<string, string>()), model).ConfigureAwait(false);
        }
    }

    public void SetResponses(IEnumerable<FauxResponseStep> responses)
    {
        lock (_gate)
        {
            _pendingResponses.Clear();
            foreach (var response in responses)
            {
                _pendingResponses.Enqueue(response);
            }
        }
    }

    public void AppendResponses(IEnumerable<FauxResponseStep> responses)
    {
        lock (_gate)
        {
            foreach (var response in responses)
            {
                _pendingResponses.Enqueue(response);
            }
        }
    }

    private FauxResponseStep? DequeueResponseStep()
    {
        lock (_gate)
        {
            State.CallCount++;
            return _pendingResponses.Count == 0 ? (FauxResponseStep?)null : _pendingResponses.Dequeue();
        }
    }

    private async Task ProduceAsync(
        AssistantMessageStream stream,
        FauxResponseStep? step,
        Model requestModel,
        LlmContext context,
        StreamOptions options)
    {
        try
        {
            if (options.OnResponse is not null)
            {
                await options.OnResponse(
                    new ProviderResponse(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                    requestModel).ConfigureAwait(false);
            }

            if (step is null)
            {
                var exhausted = WithUsageEstimate(
                    CreateErrorMessage("No more faux responses queued", requestModel),
                    context,
                    options);
                stream.Push(new ErrorEvent(exhausted.ErrorMessage ?? "No more faux responses queued", Message: exhausted));
                return;
            }

            if (options.Deferred is not null && IsDeferredRequested(options.Deferred))
            {
                var handle = new DeferredHandle(Api, requestModel.Id, requestModel.Api, Faux.RandomId("deferred"))
                {
                    PollAfterMs = GetDeferredPollAfterMs(options.Deferred) ?? _deferredOptions?.PollAfterMs
                };
                lock (_gate)
                {
                    _deferredResponses[handle.Id] = new DeferredResponse(
                        handle,
                        step.Value,
                        context,
                        options,
                        requestModel,
                        Math.Max(0, GetDeferredPendingFetches(options.Deferred) ?? _deferredOptions?.PendingFetches ?? 0));
                }

                var deferred = new AssistantMessage
                {
                    Api = Api,
                    Provider = _provider,
                    Model = requestModel.Id,
                    Content = [],
                    Usage = new Usage(0, 0, 0, 0),
                    StopReason = StopReason.Deferred,
                    Deferred = handle,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await StreamWithDeltasAsync(stream, deferred, options.Signal).ConfigureAwait(false);
                return;
            }
            var resolved = await step.Value.ResolveAsync(context, options, State, requestModel).ConfigureAwait(false);
            var message = WithUsageEstimate(CloneMessage(resolved, requestModel), context, options);
            await StreamWithDeltasAsync(stream, message, options.Signal).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = CreateErrorMessage(ex.Message, requestModel);
            stream.Push(new ErrorEvent(message.ErrorMessage ?? ex.Message, Message: message));
        }
    }

    private async Task FetchDeferredAsync(
        AssistantMessageStream stream,
        Model requestModel,
        DeferredHandle handle,
        DeferredFetchOptions fetchOptions)
    {
        try
        {
            if (fetchOptions.OnResponse is not null)
            {
                await fetchOptions.OnResponse(new ProviderResponse(200, new Dictionary<string, string>()), requestModel).ConfigureAwait(false);
            }

            DeferredResponse response;
            lock (_gate)
            {
                if (!_deferredResponses.TryGetValue(handle.Id, out response!) ||
                    response.Handle.Provider != handle.Provider ||
                    response.Handle.ModelId != handle.ModelId ||
                    response.Handle.Api != handle.Api)
                {
                    throw new InvalidOperationException($"Unknown faux deferred response: {handle.Id}");
                }

                if (response.Cancelled)
                {
                    throw new InvalidOperationException($"Faux deferred response was cancelled: {handle.Id}");
                }

                if (response.PendingFetches > 0)
                {
                    response.PendingFetches--;
                }
            }

            if (response.PendingFetches > 0 && response.Final is null)
            {
                // Pending polls return the same durable handle and do not consume the submission step.
                var pending = new AssistantMessage
                {
                    Api = Api,
                    Provider = _provider,
                    Model = requestModel.Id,
                    Content = [],
                    Usage = new Usage(0, 0, 0, 0),
                    StopReason = StopReason.Deferred,
                    Deferred = response.Handle,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await StreamWithDeltasAsync(stream, pending, fetchOptions.Signal).ConfigureAwait(false);
                return;
            }

            AssistantMessage? final;
            lock (_gate) final = response.Final;
            if (final is null)
            {
                var submissionOptions = response.Options with
                {
                    Deferred = null,
                    Signal = default,
                    OnResponse = null
                };
                try
                {
                    final = await response.Step.ResolveAsync(response.Context, submissionOptions, State, response.Model).ConfigureAwait(false);
                    final = WithUsageEstimate(CloneMessage(final, response.Model), response.Context, submissionOptions);
                }
                catch (Exception ex)
                {
                    final = CreateErrorMessage(ex.Message, response.Model);
                }

                lock (_gate)
                {
                    response.Final = final;
                    response.FinalReady = true;
                }
            }

            await StreamWithDeltasAsync(stream, final, fetchOptions.Signal).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = CreateErrorMessage(ex.Message, requestModel);
            stream.Push(new ErrorEvent(message.ErrorMessage ?? ex.Message, Message: message));
        }
    }

    private static bool IsDeferredRequested(object value) => value switch
    {
        bool boolean => boolean,
        IDictionary<string, object?> dictionary => !dictionary.TryGetValue("enabled", out var enabled) || enabled is not false,
        IReadOnlyDictionary<string, object?> dictionary => !dictionary.TryGetValue("enabled", out var enabled) || enabled is not false,
        _ => true
    };

    private static int? GetDeferredPendingFetches(object value) => ReadDeferredInt(value, "pendingFetches");
    private static int? GetDeferredPollAfterMs(object value) => ReadDeferredInt(value, "pollAfterMs");

    private static int? ReadDeferredInt(object value, string key)
    {
        if (value is IDictionary<string, object?> dictionary && dictionary.TryGetValue(key, out var raw)) return ConvertToInt(raw);
        if (value is IReadOnlyDictionary<string, object?> readOnly && readOnly.TryGetValue(key, out var readOnlyRaw)) return ConvertToInt(readOnlyRaw);
        return null;
    }

    private static int? ConvertToInt(object? value) => value switch
    {
        byte number => number,
        short number => number,
        int number => number,
        long number => checked((int)number),
        double number => checked((int)number),
        decimal number => checked((int)number),
        _ => null
    };

    private static object? CloneDeferredData(object? data) => data switch
    {
        null => null,
        ICloneable cloneable => cloneable.Clone(),
        _ => data
    };

    private sealed class DeferredResponse(
        DeferredHandle handle,
        FauxResponseStep step,
        LlmContext context,
        StreamOptions options,
        Model model,
        int pendingFetches)
    {
        public DeferredHandle Handle { get; } = handle;
        public FauxResponseStep Step { get; } = step;
        public LlmContext Context { get; } = context;
        public StreamOptions Options { get; } = options;
        public Model Model { get; } = model;
        public int PendingFetches { get; set; } = pendingFetches;
        public bool Cancelled { get; set; }
        public bool FinalReady { get; set; }
        public AssistantMessage? Final { get; set; }
    }

    private AssistantMessage CloneMessage(AssistantMessage message, Model requestModel) =>
        message with
        {
            Content = message.Content.Select(CloneContent).ToArray(),
            Api = Api,
            Provider = _provider,
            Model = requestModel.Id,
            Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow,
            Usage = message.Usage ?? new Usage(0, 0, 0, 0)
        };

    private static ContentBlock CloneContent(ContentBlock block) => block switch
    {
        TextContent text => text with { },
        ThinkingContent thinking => thinking with { },
        ImageContent image => image with { },
        ToolCallContent toolCall => toolCall with { },
        _ => block
    };

    private AssistantMessage CreateErrorMessage(string error, Model requestModel) =>
        new()
        {
            Api = Api,
            Provider = _provider,
            Model = requestModel.Id,
            Usage = new Usage(0, 0, 0, 0),
            StopReason = StopReason.Error,
            ErrorMessage = error,
            Timestamp = DateTimeOffset.UtcNow
        };

    private AssistantMessage WithUsageEstimate(
        AssistantMessage message,
        LlmContext context,
        StreamOptions options)
    {
        var promptText = SerializeContext(context);
        var promptTokens = EstimateTokens(promptText);
        var outputTokens = EstimateTokens(AssistantContentToText(message.Content));
        var input = promptTokens;
        var cacheRead = 0;
        var cacheWrite = 0;

        if (!string.IsNullOrWhiteSpace(options.SessionId) && options.CacheRetention != CacheRetention.None)
        {
            lock (_gate)
            {
                if (_promptCache.TryGetValue(options.SessionId, out var previousPrompt))
                {
                    var cachedChars = CommonPrefixLength(previousPrompt, promptText);
                    cacheRead = EstimateTokens(previousPrompt[..cachedChars]);
                    cacheWrite = EstimateTokens(promptText[cachedChars..]);
                    input = Math.Max(0, promptTokens - cacheRead);
                }
                else
                {
                    cacheWrite = promptTokens;
                }

                _promptCache[options.SessionId] = promptText;
            }
        }

        return message with
        {
            Usage = new Usage(input, outputTokens, cacheRead, cacheWrite)
        };
    }

    private async Task StreamWithDeltasAsync(
        AssistantMessageStream stream,
        AssistantMessage message,
        CancellationToken cancellationToken)
    {
        var partialContent = new List<ContentBlock>();
        if (cancellationToken.IsCancellationRequested)
        {
            var aborted = CreateAbortedMessage(BuildPartial(message, partialContent));
            stream.Push(new ErrorEvent(aborted.ErrorMessage!, Message: aborted));
            return;
        }

        stream.Push(new StartEvent(BuildPartial(message, partialContent)));

        for (var index = 0; index < message.Content.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var aborted = CreateAbortedMessage(BuildPartial(message, partialContent));
                stream.Push(new ErrorEvent(aborted.ErrorMessage!, BuildPartial(message, partialContent), aborted));
                return;
            }

            var block = message.Content[index];
            switch (block)
            {
                case ThinkingContent thinking:
                    partialContent.Add(new ThinkingContent(string.Empty));
                    stream.Push(new ThinkingStartEvent(index, BuildPartial(message, partialContent)));
                    foreach (var chunk in SplitStringByTokenSize(thinking.Thinking))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(BuildPartial(message, partialContent));
                            stream.Push(new ErrorEvent(aborted.ErrorMessage!, BuildPartial(message, partialContent), aborted));
                            return;
                        }

                        var current = (ThinkingContent)partialContent[index];
                        partialContent[index] = current with { Thinking = current.Thinking + chunk };
                        stream.Push(new ThinkingDeltaEvent(index, chunk, BuildPartial(message, partialContent)));
                    }

                    stream.Push(new ThinkingEndEvent(index, BuildPartial(message, partialContent)));
                    break;

                case TextContent text:
                    partialContent.Add(new TextContent(string.Empty));
                    stream.Push(new TextStartEvent(index, BuildPartial(message, partialContent)));
                    foreach (var chunk in SplitStringByTokenSize(text.Text))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(BuildPartial(message, partialContent));
                            stream.Push(new ErrorEvent(aborted.ErrorMessage!, BuildPartial(message, partialContent), aborted));
                            return;
                        }

                        var current = (TextContent)partialContent[index];
                        partialContent[index] = current with { Text = current.Text + chunk };
                        stream.Push(new TextDeltaEvent(index, chunk, BuildPartial(message, partialContent)));
                    }

                    stream.Push(new TextEndEvent(index, BuildPartial(message, partialContent)));
                    break;

                case ToolCallContent toolCall:
                    partialContent.Add(toolCall with { Arguments = string.Empty });
                    stream.Push(new ToolCallStartEvent(index, BuildPartial(message, partialContent)));
                    foreach (var chunk in SplitStringByTokenSize(toolCall.Arguments))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            var aborted = CreateAbortedMessage(BuildPartial(message, partialContent));
                            stream.Push(new ErrorEvent(aborted.ErrorMessage!, BuildPartial(message, partialContent), aborted));
                            return;
                        }

                        stream.Push(new ToolCallDeltaEvent(index, chunk, BuildPartial(message, partialContent)));
                    }

                    partialContent[index] = toolCall;
                    stream.Push(new ToolCallEndEvent(index, BuildPartial(message, partialContent)));
                    break;
            }
        }

        if (message.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new ErrorEvent(
                message.ErrorMessage ?? message.StopReason.ToString()!,
                BuildPartial(message, partialContent),
                message));
            return;
        }

        stream.Push(new DoneEvent(message));
    }

    private static AssistantMessage CreateAbortedMessage(AssistantMessage partial) =>
        partial with
        {
            StopReason = StopReason.Aborted,
            ErrorMessage = "Request was aborted",
            Timestamp = DateTimeOffset.UtcNow
        };

    private static AssistantMessage BuildPartial(
        AssistantMessage message,
        IReadOnlyList<ContentBlock> content) =>
        message with
        {
            Content = content.ToArray()
        };

    private async Task ScheduleChunkAsync(string chunk)
    {
        if (_tokensPerSecond is not > 0)
        {
            await Task.Yield();
            return;
        }

        var delayMs = (int)Math.Ceiling((EstimateTokens(chunk) / _tokensPerSecond.Value) * 1000);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    private IEnumerable<string> SplitStringByTokenSize(string text)
    {
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var index = 0;
        while (index < text.Length)
        {
            var tokenSize = _minTokenSize == _maxTokenSize
                ? _minTokenSize
                : Random.Shared.Next(_minTokenSize, _maxTokenSize + 1);
            var charSize = Math.Max(1, tokenSize * 4);
            var size = Math.Min(charSize, text.Length - index);
            yield return text.Substring(index, size);
            index += size;
        }
    }

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4d);

    private static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var index = 0;
        while (index < length && a[index] == b[index])
        {
            index++;
        }

        return index;
    }

    private static string SerializeContext(LlmContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            parts.Add($"system:{context.SystemPrompt}");
        }

        foreach (var message in context.Messages)
        {
            parts.Add($"{message.Role}:{MessageToText(message)}");
        }

        if (context.Tools is { Count: > 0 })
        {
            parts.Add($"tools:{ToolsToText(context.Tools)}");
        }

        return string.Join("\n\n", parts);
    }

    private static string MessageToText(ChatMessage message) => message switch
    {
        UserMessage user => UserContentToText(user.Content),
        AssistantMessage assistant => AssistantContentToText(assistant.Content),
        ToolResultMessage toolResult => ToolResultToText(toolResult),
        _ => string.Empty
    };

    private static string UserContentToText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.Select(block => block switch
        {
            TextContent text => text.Text,
            ImageContent image => $"[image:{image.MimeType}:{image.Data.Length}]",
            ThinkingContent thinking => thinking.Thinking,
            ToolCallContent toolCall => $"{toolCall.Name}:{toolCall.Arguments}",
            _ => string.Empty
        }));

    private static string AssistantContentToText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.Select(block => block switch
        {
            TextContent text => text.Text,
            ThinkingContent thinking => thinking.Thinking,
            ToolCallContent toolCall => $"{toolCall.Name}:{toolCall.Arguments}",
            ImageContent image => $"[image:{image.MimeType}:{image.Data.Length}]",
            _ => string.Empty
        }));

    private static string ToolResultToText(ToolResultMessage message) =>
        string.Join("\n", new[] { message.ToolCallId }.Concat(message.Content.Select(ContentToText)));

    private static string ContentToText(ContentBlock block) => block switch
    {
        TextContent text => text.Text,
        ImageContent image => $"[image:{image.MimeType}:{image.Data.Length}]",
        ThinkingContent thinking => thinking.Thinking,
        ToolCallContent toolCall => $"{toolCall.Name}:{toolCall.Arguments}",
        _ => string.Empty
    };

    private static string ToolsToText(IEnumerable<Tool> tools) =>
        string.Join(
            ";",
            tools.Select(tool => $"{tool.Name}:{tool.Description}:{tool.ParameterSchema.GetRawText()}"));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyDictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
internal partial class FauxJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
