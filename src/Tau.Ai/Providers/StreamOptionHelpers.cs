using System.Net.Http.Headers;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

internal static class StreamOptionHelpers
{
    public const string AbortedErrorMessage = "Request was aborted";

    public static StreamRequestTimeout CreateRequestTimeout(StreamOptions options) => new(options);

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
        level == ThinkingLevel.ExtraHigh ? ThinkingLevel.High : level;

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
