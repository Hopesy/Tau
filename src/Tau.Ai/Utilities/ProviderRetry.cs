using System.Net;
using System.Net.Http.Headers;

namespace Tau.Ai.Utilities;

/// <summary>
/// 提供与 pi provider-retry.ts 对齐的 HTTP 重试判定、延迟解析和可取消退避工具。
/// </summary>
public static class ProviderRetry
{
    /// <summary>
    /// 判断异常是否代表服务端建议或通常允许重试的临时失败。
    /// </summary>
    /// <param name="exception">要判断的异常。</param>
    /// <returns>如果应当重试则返回 true，否则返回 false。</returns>
    public static bool ShouldRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is HttpRequestException http && http.StatusCode is { } status)
        {
            return ShouldRetry(status, http.Data["headers"] as HttpResponseHeaders);
        }

        return exception is TimeoutException or IOException or TaskCanceledException;
    }

    /// <summary>
    /// 根据 HTTP 状态码和响应头判断请求是否可重试。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="headers">服务端响应头，可为空。</param>
    /// <returns>如果响应表示临时失败则返回 true。</returns>
    public static bool ShouldRetry(HttpStatusCode statusCode, HttpHeaders? headers = null)
    {
        if (headers is not null && headers.TryGetValues("x-should-retry", out var values))
        {
            var value = values.FirstOrDefault();
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }

        var code = (int)statusCode;
        return code is 408 or 409 or 429 or >= 500;
    }

    /// <summary>
    /// 从响应头解析服务端建议的重试等待时间。
    /// </summary>
    /// <param name="headers">服务端响应头。</param>
    /// <param name="fallback">无法解析时使用的退避时间。</param>
    /// <param name="maximum">返回值允许的最大退避时间。</param>
    /// <returns>解析后的非负退避时间。</returns>
    public static TimeSpan ResolveDelay(HttpHeaders? headers, TimeSpan fallback, TimeSpan maximum)
    {
        var delay = fallback;
        if (headers is not null)
        {
            if (headers.TryGetValues("retry-after-ms", out var milliseconds) &&
                double.TryParse(milliseconds.FirstOrDefault(), out var ms))
            {
                delay = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            }
            else if (headers.TryGetValues("retry-after", out var retryAfter) &&
                     double.TryParse(retryAfter.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
            else if (headers.TryGetValues("retry-after", out retryAfter) &&
                     DateTimeOffset.TryParse(retryAfter.FirstOrDefault(), out var date))
            {
                delay = date - DateTimeOffset.UtcNow;
            }
        }

        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        if (maximum > TimeSpan.Zero && delay > maximum)
            throw new InvalidOperationException($"Server requested {Math.Ceiling(delay.TotalSeconds):0}s retry delay (max: {Math.Ceiling(maximum.TotalSeconds):0}s).");
        return delay;
    }

    /// <summary>
    /// 按指数退避重试异步 provider 请求，并响应取消信号。
    /// </summary>
    /// <typeparam name="T">请求结果类型。</typeparam>
    /// <param name="operation">一次请求操作。</param>
    /// <param name="maxRetries">最多额外重试次数。</param>
    /// <param name="baseDelay">首次等待时间。</param>
    /// <param name="maximumDelay">单次等待上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功的请求结果。</returns>
    public static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries = 2,
        TimeSpan? baseDelay = null,
        TimeSpan? maximumDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var initial = baseDelay ?? TimeSpan.FromMilliseconds(250);
        var maximum = maximumDelay ?? TimeSpan.FromSeconds(30);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Math.Max(0, maxRetries) && ShouldRetry(ex))
            {
                var delay = ResolveDelay(null,
                    TimeSpan.FromMilliseconds(Math.Min(8_000, initial.TotalMilliseconds * Math.Pow(2, attempt))), maximum);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
