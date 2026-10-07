using System.Net.Http.Headers;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

internal static class StreamOptionHelpers
{
    public const string AbortedErrorMessage = "Request was aborted";

    /// <summary>【AI】【缓存默认值】显式策略优先，其次请求环境中的 long，否则采用短缓存。</summary>
    /// <param name="options">原始请求选项。</param><returns>实际缓存保留策略。</returns>
    internal static CacheRetention ResolveCacheRetention(StreamOptions options) => options.HasExplicitCacheRetention ? options.CacheRetention
        : ProviderEnvironment.GetValue("PI_CACHE_RETENTION", options.Env) == "long" ? CacheRetention.Long : CacheRetention.Short;

    /// <summary>【AI】【直接调用】为绕过统一入口的提供方请求补充默认缓存，保留派生类型和调用方对象。</summary>
    /// <param name="options">原始请求选项。</param><returns>已有显式值时复用原对象，否则返回派生类型不变的副本。</returns>
    internal static StreamOptions WithCacheDefaults(StreamOptions options) => options.HasExplicitCacheRetention ? options
        : options with { CacheRetention = ResolveCacheRetention(options) };

    public static StreamRequestTimeout CreateRequestTimeout(StreamOptions options) => new(options);

    /// <summary>【AI】【协议事件】在供应商解析器归一化前通知观察器，传出独立的 JSON 快照。</summary>
    /// <param name="options">包含可选观察器的选项。</param>
    /// <param name="model">实际请求模型。</param>
    /// <param name="json">已分帧的 JSON 事件。</param>
    /// <returns>观察器执行任务。</returns>
    public static async ValueTask InvokeProviderStreamEventAsync(StreamOptions options, Model model, string json)
    {
        if (options.OnProviderStreamEvent is null) return;
        using var document = JsonDocument.Parse(json);
        await options.OnProviderStreamEvent(document.RootElement.Clone(), model).ConfigureAwait(false);
    }

    /// <summary>【AI】【请求头转换】对发送前的请求及内容头执行转换，支持大小写不敏感覆盖和删除。</summary>
    /// <param name="options">含可选转换器的选项。</param>
    /// <param name="model">实际请求模型。</param>
    /// <param name="request">即将发送的 HTTP 请求。</param>
    /// <param name="protectBedrockAuth">是否保护 Bedrock 签名保留字段。</param>
    /// <returns>头部转换任务。</returns>
    public static async ValueTask ApplyHeadersCallbackAsync(StreamOptions options, Model model, HttpRequestMessage request, bool protectBedrockAuth = false)
    {
        if (options.TransformHeaders is null) return;
        // 1. 【AI】【头部快照】内容头与普通头一并交给调用方，并保持多值字段的标准合并形式
        var original = request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
            .ToDictionary(header => header.Key, header => (string?)string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, string?>(original, StringComparer.OrdinalIgnoreCase);
        var transformed = await options.TransformHeaders(headers, model).ConfigureAwait(false) ?? headers;
        // 2. 【AI】【头部应用】缺失或 null 表示删除；Bedrock 的认证字段由签名器独占
        foreach (var name in original.Keys.Concat(transformed.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (protectBedrockAuth && (name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("host", StringComparison.OrdinalIgnoreCase))) continue;
            var value = transformed.LastOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (request.Headers.Any(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) request.Headers.Remove(name);
            if (request.Content?.Headers.Any(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) == true) request.Content.Headers.Remove(name);
            if (value is not null && !request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// 将模型和请求中的额外采样参数合并到兼容 provider 请求体。
    /// </summary>
    /// <param name="body">已经组装好的请求体。</param>
    /// <param name="model">当前模型，提供默认采样参数。</param>
    /// <param name="options">当前请求，显式参数优先于模型默认值。</param>
    public static void ApplySamplingParams(Dictionary<string, object> body, Model model, StreamOptions options)
    {
        if (model.SamplingParams is not null)
        {
            foreach (var pair in model.SamplingParams) body[pair.Key] = pair.Value;
        }
        if (options.SamplingParams is not null)
        {
            foreach (var pair in options.SamplingParams) body[pair.Key] = pair.Value;
        }
    }

    public static void PushAborted(
        AssistantMessageStream stream,
        Model model,
        string api,
        AssistantMessage? partial = null)
    {
        var aborted = CreateAbortedMessage(model, api, partial);
        stream.Push(new ErrorEvent(AbortedErrorMessage, partial, aborted));
    }

    public static bool PushAbortedIfCanceled(
        StreamOptions options,
        AssistantMessageStream stream,
        Model model,
        string api,
        AssistantMessage? partial = null)
    {
        if (!options.Signal.IsCancellationRequested)
        {
            return false;
        }

        PushAborted(stream, model, api, partial);
        return true;
    }

    public static AssistantMessage CreateAbortedMessage(Model model, string api, AssistantMessage? partial = null)
    {
        var message = partial ?? new AssistantMessage
        {
            Api = api,
            Provider = model.Provider,
            Model = model.Id,
            Content = []
        };

        return message with
        {
            Api = message.Api ?? api,
            Provider = message.Provider ?? model.Provider,
            Model = message.Model ?? model.Id,
            StopReason = StopReason.Aborted,
            ErrorMessage = AbortedErrorMessage,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public static async ValueTask<Dictionary<string, object>> ApplyPayloadCallbackAsync(
        StreamOptions options,
        Model model,
        Dictionary<string, object> payload)
    {
        if (options.OnPayload is null)
        {
            return payload;
        }

        var replacement = await options.OnPayload(payload, model).ConfigureAwait(false);
        return replacement switch
        {
            null => payload,
            Dictionary<string, object> dictionary => dictionary,
            IDictionary<string, object> dictionary => new Dictionary<string, object>(dictionary, StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                "StreamOptions.OnPayload must return null or an object dictionary compatible with provider request-body serialization.")
        };
    }

    public static async ValueTask InvokeResponseCallbackAsync(
        StreamOptions options,
        Model model,
        HttpResponseMessage response)
    {
        if (options.OnResponse is null)
        {
            return;
        }

        await options.OnResponse(
            new ProviderResponse((int)response.StatusCode, ToHeaderDictionary(response)),
            model).ConfigureAwait(false);
    }

    public static ThinkingLevel ClampReasoning(ThinkingLevel level) =>
        level is ThinkingLevel.ExtraHigh or ThinkingLevel.Max ? ThinkingLevel.High : level;

    public static string ToReasoningEffortName(ThinkingLevel level, bool allowExtraHigh = false)
    {
        var normalized = allowExtraHigh ? level : ClampReasoning(level);
        return normalized switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            ThinkingLevel.Max => "max",
            _ => "medium"
        };
    }

    public static int GetThinkingBudget(
        ThinkingBudgets? budgets,
        ThinkingLevel level,
        int defaultMinimal,
        int defaultLow,
        int defaultMedium,
        int defaultHigh)
    {
        return GetCustomThinkingBudget(budgets, level) ??
               ClampReasoning(level) switch
               {
                   ThinkingLevel.Minimal => defaultMinimal,
                   ThinkingLevel.Low => defaultLow,
                   ThinkingLevel.Medium => defaultMedium,
                   _ => defaultHigh
               };
    }

    public static int? GetCustomThinkingBudget(ThinkingBudgets? budgets, ThinkingLevel level)
    {
        if (budgets is null)
        {
            return null;
        }

        return ClampReasoning(level) switch
        {
            ThinkingLevel.Minimal => budgets.Minimal,
            ThinkingLevel.Low => budgets.Low,
            ThinkingLevel.Medium => budgets.Medium,
            ThinkingLevel.High => budgets.High,
            _ => budgets.High
        };
    }

    private static IReadOnlyDictionary<string, string> ToHeaderDictionary(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyHeaders(headers, response.Headers);
        CopyHeaders(headers, response.Content.Headers);
        return headers;
    }

    private static void CopyHeaders(IDictionary<string, string> target, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            target[header.Key] = string.Join(",", header.Value);
        }
    }
}

internal sealed class StreamRequestTimeout : IDisposable
{
    private readonly CancellationToken _signal;
    private readonly CancellationTokenSource? _timeoutSource;
    private readonly CombinedCancellationToken _combined;
    private readonly TimeSpan _timeout;

    public StreamRequestTimeout(StreamOptions options)
    {
        _signal = options.Signal;
        _timeout = options.Timeout ?? TimeSpan.Zero;
        if (_timeout <= TimeSpan.Zero)
        {
            _combined = CancellationTokenUtilities.Combine(_signal);
            return;
        }

        _timeoutSource = new CancellationTokenSource(_timeout);
        _combined = CancellationTokenUtilities.Combine(_signal, _timeoutSource.Token);
    }

    public CancellationToken Token => _combined.Token;

    public bool IsTimeoutCancellation =>
        _timeoutSource?.IsCancellationRequested == true && !_signal.IsCancellationRequested;

    public TimeoutException CreateTimeoutException(OperationCanceledException cause) =>
        new($"Request timed out after {(int)_timeout.TotalMilliseconds}ms", cause);

    public void Dispose()
    {
        _combined.Dispose();
        _timeoutSource?.Dispose();
    }
}
