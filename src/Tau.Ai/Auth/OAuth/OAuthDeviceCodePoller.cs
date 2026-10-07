// 作者：xxx
namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【设备码轮询】统一表达等待、限速、失败及完成。</summary>
/// <typeparam name="T">成功结果。</typeparam><param name="Status">状态。</param><param name="Value">结果。</param>
/// <param name="Message">失败信息。</param><param name="IntervalSeconds">服务端建议的秒数。</param>
internal sealed record OAuthDeviceCodePollResult<T>(string Status, T? Value = default, string? Message = null, double? IntervalSeconds = null);

/// <summary>【AI】【设备码时钟】提供真实时间与可取消等待，测试可注入虚拟时间。</summary>
internal sealed class OAuthFlowClock
{
    public static OAuthFlowClock System { get; } = new();
    public Func<double> NowMilliseconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Func<double, CancellationToken, Task> SleepAsync { get; init; } = SleepInChunksAsync;

    /// <summary>【AI】【设备码等待】分段处理超长期限，每段均可被取消。</summary>
    /// <param name="milliseconds">等待毫秒数。</param><param name="token">取消信号。</param><returns>等待任务。</returns>
    private static async Task SleepInChunksAsync(double milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (milliseconds > 0)
        {
            var chunk = Math.Min(milliseconds, int.MaxValue);
            await Task.Delay(TimeSpan.FromMilliseconds(chunk), token).ConfigureAwait(false);
            milliseconds -= chunk;
        }
    }
}

/// <summary>【AI】【设备码轮询】对齐 RFC 8628 的默认间隔、首次等待、限速响应与有界期限。</summary>
internal static class OAuthDeviceCodePoller
{
    internal const string SlowDownTimeoutMessage = "Device flow timed out after one or more slow_down responses. This is often caused by clock drift in WSL or VM environments. Please sync or restart the VM clock and try again.";

    /// <summary>【AI】【设备码轮询】按服务端状态调整间隔，等待不会超出设备码剩余有效期。</summary>
    /// <typeparam name="T">成功结果。</typeparam><param name="poll">单次轮询函数。</param><param name="token">取消信号。</param>
    /// <param name="intervalSeconds">间隔，缺省五秒，最小一秒。</param><param name="expiresInSeconds">可选有效期。</param>
    /// <param name="waitBeforeFirstPoll">首次请求前是否等待。</param><param name="clock">可选测试时钟。</param><returns>成功结果。</returns>
    internal static async Task<T> PollAsync<T>(Func<Task<OAuthDeviceCodePollResult<T>>> poll, CancellationToken token,
        double? intervalSeconds = null, double? expiresInSeconds = null, bool waitBeforeFirstPoll = false, OAuthFlowClock? clock = null)
    {
        clock ??= OAuthFlowClock.System;
        var deadline = expiresInSeconds is { } expires ? clock.NowMilliseconds() + expires * 1000 : double.PositiveInfinity;
        var interval = Math.Max(1000, Math.Floor((intervalSeconds ?? 5) * 1000));
        var slowed = false;
        try
        {
            // 1. 【AI】【设备码轮询】需要首轮等待的提供方同样受授权有效期约束
            if (waitBeforeFirstPoll && deadline - clock.NowMilliseconds() is var firstRemaining && firstRemaining > 0)
                await clock.SleepAsync(Math.Min(interval, firstRemaining), token).ConfigureAwait(false);
            while (clock.NowMilliseconds() < deadline)
            {
                token.ThrowIfCancellationRequested();
                var result = await poll().ConfigureAwait(false);
                if (result.Status == "complete") return result.Value!;
                if (result.Status == "failed") throw new InvalidOperationException(result.Message);
                // 2. 【AI】【设备码轮询】服务端明确给出间隔时采用该值，否则增加五秒
                if (result.Status == "slow_down")
                {
                    slowed = true;
                    interval = result.IntervalSeconds is { } suggested && double.IsFinite(suggested) && suggested > 0
                        ? Math.Max(1000, Math.Floor(suggested * 1000)) : Math.Max(1000, interval + 5000);
                }
                var remaining = deadline - clock.NowMilliseconds();
                if (remaining <= 0) break;
                await clock.SleepAsync(Math.Min(interval, remaining), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Login cancelled", error, token); }
        throw new TimeoutException(slowed ? SlowDownTimeoutMessage : "Device flow timed out");
    }
}
