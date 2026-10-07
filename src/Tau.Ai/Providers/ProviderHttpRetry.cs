// 作者：xxx
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tau.Ai.Providers;

/// <summary>【AI】【请求重试】仅重试获取响应之前的 HTTP 失败，流开始后由调用方处理终态。</summary>
internal static class ProviderHttpRetry
{
    /// <summary>【AI】【HTTP 重试】复制已组装请求，按状态和服务端提示等待，可随时取消。</summary>
    /// <param name="client">HTTP 客户端。</param><param name="request">已完成回调和头部转换的请求模板。</param>
    /// <param name="model">目标模型。</param><param name="options">重试次数、最长等待及响应观察器。</param>
    /// <param name="signal">包含用户取消与超时的令牌。</param><param name="retryTransportErrors">是否按 SDK 策略重试连接及传输超时。</param>
    /// <returns>最终响应，调用方负责释放。</returns>
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        Model model, StreamOptions options, CancellationToken signal, bool retryTransportErrors = false)
    {
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(signal).ConfigureAwait(false);
        var retries = Math.Max(0, options.MaxRetries ?? 0);
        for (var attempt = 0; ; attempt++)
        {
            signal.ThrowIfCancellationRequested();
            // 1. 【AI】【请求复制】HttpRequestMessage 不可重复发送，每次复制完整正文及已转换的头部
            using var next = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (content is not null)
            {
                next.Content = new ByteArrayContent(content);
                foreach (var header in request.Content!.Headers) next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, signal).ConfigureAwait(false);
            }
            catch (Exception error) when (retryTransportErrors && attempt < retries && !signal.IsCancellationRequested && IsRetryableTransportError(error))
            {
                await Task.Delay(ExponentialDelay(attempt), signal).ConfigureAwait(false);
                continue;
            }
            try
            {
                await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();
                if (response.IsSuccessStatusCode || attempt >= retries || !ShouldRetry(response)) return response;
                // 2. 【AI】【重试等待】失败响应先完整读取，超长服务端等待立即失败，取消不再发送下一次请求
                var body = await response.Content.ReadAsStringAsync(signal).ConfigureAwait(false);
                var delay = ResolveDelay(response, attempt, options.MaxRetryDelay, body);
                response.Dispose();
                await Task.Delay(delay, signal).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    /// <summary>【AI】【重试条件】显式服务端指令优先，默认只重试超时、冲突、限流和服务端错误。</summary>
    /// <param name="response">失败响应。</param><returns>是否允许重试。</returns>
    private static bool ShouldRetry(HttpResponseMessage response) => Header(response, "x-should-retry") switch
    {
        "true" => true, "false" => false, _ => (int)response.StatusCode is 408 or 409 or 429 or >= 500
    };

    /// <summary>【AI】【等待策略】优先毫秒与秒数或日期头，否则使用带抖动的指数退避。</summary>
    /// <param name="response">响应。</param><param name="attempt">重试序号。</param><param name="maximum">可选最长服务端等待。</param>
    /// <param name="body">错误正文。</param><returns>非负等待时长。</returns>
    private static TimeSpan ResolveDelay(HttpResponseMessage response, int attempt, TimeSpan? maximum, string body)
    {
        double? serverDelay = ParseNumber(Header(response, "retry-after-ms"));
        if (serverDelay is null && Header(response, "retry-after") is { } after)
        {
            if (ParseNumber(after) is { } seconds) serverDelay = seconds * 1000;
            else if (DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                serverDelay = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
        }
        if (serverDelay is { } delay && double.IsFinite(delay))
        {
            var limit = maximum?.TotalMilliseconds ?? 60000;
            if (limit > 0 && delay > limit)
                throw new InvalidOperationException($"Server requested {Math.Ceiling(delay / 1000)}s retry delay (max: {Math.Ceiling(limit / 1000)}s). {body}");
            return TimeSpan.FromMilliseconds(Math.Max(0, delay));
        }
        return ExponentialDelay(attempt);
    }

    /// <summary>【AI】【连接失败】只重试客户端传输异常，不重试业务回调异常及明确的非重试状态。</summary>
    /// <param name="error">传输阶段异常。</param><returns>是否允许再次连接。</returns>
    private static bool IsRetryableTransportError(Exception error) => error is TaskCanceledException ||
        error is HttpRequestException requestError && (requestError.StatusCode is null || (int)requestError.StatusCode is 408 or 409 or 429 or >= 500);

    /// <summary>【AI】【指数退避】从半秒开始翻倍，最多八秒，并施加主线四分之一范围的抖动。</summary>
    /// <param name="attempt">重试序号。</param><returns>等待时长。</returns>
    private static TimeSpan ExponentialDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt), 8000) * (1 - Random.Shared.NextDouble() * 0.25));

    /// <summary>【AI】【重试提示】按 JavaScript parseFloat 接受数值前缀，排除非有限数。</summary>
    /// <param name="value">头部值。</param><returns>有限数或 null。</returns>
    private static double? ParseNumber(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var match = Regex.Match(value.TrimStart(), @"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }

    /// <summary>【AI】【响应头】读取服务端单值重试提示。</summary>
    /// <param name="response">响应。</param><param name="name">头名称。</param><returns>首个值或 null。</returns>
    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
