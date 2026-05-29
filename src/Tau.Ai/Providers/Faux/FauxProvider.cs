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
}

public sealed class FauxProviderState
{
    public int CallCount { get; internal set; }
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
            maxTokenSize);

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
    private readonly Dictionary<string, string> _promptCache = new(StringComparer.Ordinal);

    public FauxStreamProvider(
        string api,
        string provider,
        IReadOnlyList<Model> models,
        double? tokensPerSecond,
        int minTokenSize,
        int maxTokenSize)
    {
        Api = api;
        _provider = provider;
        _models = models;
        _tokensPerSecond = tokensPerSecond;
        _minTokenSize = minTokenSize;
        _maxTokenSize = maxTokenSize;
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
            if (step is null)
            {
                var exhausted = WithUsageEstimate(
                    CreateErrorMessage("No more faux responses queued", requestModel),
                    context,
                    options);
                stream.Push(new ErrorEvent(exhausted.ErrorMessage ?? "No more faux responses queued", Message: exhausted));
                return;
            }

            var resolved = await step.Value.ResolveAsync(context, options, State, requestModel).ConfigureAwait(false);
            var message = WithUsageEstimate(CloneMessage(resolved, requestModel), context, options);
            await StreamWithDeltasAsync(stream, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = CreateErrorMessage(ex.Message, requestModel);
            stream.Push(new ErrorEvent(message.ErrorMessage ?? ex.Message, Message: message));
        }
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
        AssistantMessage message)
    {
        var partialContent = new List<ContentBlock>();
        stream.Push(new StartEvent(BuildPartial(message, partialContent)));

        for (var index = 0; index < message.Content.Count; index++)
        {
            var block = message.Content[index];
            switch (block)
            {
                case ThinkingContent thinking:
                    partialContent.Add(new ThinkingContent(string.Empty));
                    stream.Push(new ThinkingStartEvent(index, BuildPartial(message, partialContent)));
                    foreach (var chunk in SplitStringByTokenSize(thinking.Thinking))
                    {
                        await ScheduleChunkAsync(chunk).ConfigureAwait(false);
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
