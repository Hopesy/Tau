// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【缓存预热】全局预热模式；空闲模式同时允许运行期间预热。</summary>
public enum CodingAgentCacheWarmingMode { Off, Streaming, Idle }

/// <summary>【CodingAgent】【预热决策】一次预热的成本、收益及默认动作。</summary>
/// <param name="Phase">streaming 或 idle。</param><param name="WarmCost">预热费用，美元。</param>
/// <param name="MissCost">缓存丢失增加的费用，美元。</param><param name="ContinuationProbability">继续请求概率。</param>
/// <param name="ExpectedSavings">预计净收益，美元。</param><param name="EconomicsAvailable">计费数据是否已知。</param>
/// <param name="Action">warm 或 stop。</param>
public sealed record CodingAgentCacheWarmingDecision(string Phase, decimal WarmCost, decimal MissCost,
    decimal ContinuationProbability, decimal ExpectedSavings, bool EconomicsAvailable, string Action);

/// <summary>【CodingAgent】【预热状态】用于会话查询的调度状态。</summary>
/// <param name="State">inactive、scheduled 或 refreshing。</param><param name="Reason">停止原因。</param>
/// <param name="NextWarmAt">下次决策时间。</param><param name="Decision">当前或最后的决策。</param>
/// <param name="ExtensionOverride">扩展是否改变默认动作。</param>
public sealed record CodingAgentCacheWarmingStatus(string State, string? Reason = null, DateTimeOffset? NextWarmAt = null,
    CodingAgentCacheWarmingDecision? Decision = null, bool ExtensionOverride = false);

/// <summary>【CodingAgent】【缓存预热】重放最新请求，限制费用、缓存过期及固定预热期限。</summary>
public sealed class CodingAgentCacheWarmer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AgentStreamFunction _stream;
    private readonly Func<int> _getPromptTokens;
    private readonly Func<CodingAgentCacheWarmingMode> _getMode;
    private readonly Func<CodingAgentCacheWarmingDecision, Task<string>> _decide;
    private readonly Action<AssistantMessage, string?> _onWarmed;
    private readonly TimeProvider _clock;
    private readonly List<Task> _retirements = [];
    private ActiveRun? _run;
    private bool _disposed;
    private CodingAgentCacheWarmingStatus _inactive = new("inactive", "waiting for first request");

    /// <summary>【CodingAgent】【预热初始化】接入实际请求函数、分支计费和全局设置。</summary>
    /// <param name="stream">直接发送模型请求的函数，不得再次启动预热。</param>
    /// <param name="getPromptTokens">最近一条真实助手消息的输入及缓存 token 总量。</param>
    /// <param name="getMode">读取全局模式。</param><param name="onWarmed">成功后保存独立用量及可选说明。</param>
    /// <param name="decide">扩展动作覆盖；异常或无效动作保留默认决定。</param><param name="clock">调度时钟。</param>
    public CodingAgentCacheWarmer(AgentStreamFunction stream, Func<int> getPromptTokens,
        Func<CodingAgentCacheWarmingMode> getMode, Action<AssistantMessage, string?> onWarmed,
        Func<CodingAgentCacheWarmingDecision, Task<string>>? decide = null, TimeProvider? clock = null)
    {
        _stream = stream; _getPromptTokens = getPromptTokens; _getMode = getMode; _onWarmed = onWarmed;
        _decide = decide ?? (decision => Task.FromResult(decision.Action)); _clock = clock ?? TimeProvider.System;
    }

    /// <summary>【CodingAgent】【缓存寿命】按显式选项、请求环境及模型元数据读取缓存毫秒数。</summary>
    /// <param name="model">请求模型。</param><param name="options">请求选项。</param><returns>未知或关闭时为空。</returns>
    public static double? GetPromptCacheTtlMs(Model model, SimpleStreamOptions? options = null)
    {
        var retention = options?.HasExplicitCacheRetention == true ? options.CacheRetention
            : ProviderEnvironment.GetValue("PI_CACHE_RETENTION", options?.Env) == "long" ? CacheRetention.Long : CacheRetention.Short;
        return retention switch { CacheRetention.Short => model.PromptCache?.Short * 1000,
            CacheRetention.Long => model.PromptCache?.Long * 1000, _ => null };
    }

    /// <summary>【CodingAgent】【预热间隔】保留至少十秒余量，在缓存寿命的九成处调度。</summary>
    /// <param name="ttlMs">缓存寿命毫秒数。</param><returns>有效间隔；过短或非有限寿命返回空。</returns>
    public static double? GetCacheWarmingDelayMs(double ttlMs) => !double.IsFinite(ttlMs) || ttlMs <= 10000
        ? null : Math.Max(1, Math.Floor(Math.Min(ttlMs * .9, ttlMs - 10000)));

    /// <summary>【CodingAgent】【安全重放】排除会随输出上限改变思考预算的 Anthropic 请求。</summary>
    /// <param name="model">实际模型。</param><param name="options">请求思考设置。</param><returns>能否安全重放。</returns>
    public static bool IsReplayable(Model model, SimpleStreamOptions? options = null) =>
        options?.Reasoning is null or ThinkingLevel.Off || model.Api != "anthropic-messages" || model.Compat?.ForceAdaptiveThinking == true;

    /// <summary>【CodingAgent】【预热查询】读取状态；没有真实计费数据时隐藏待决策定时器。</summary>
    public CodingAgentCacheWarmingStatus Status
    {
        get
        {
            if (_getMode() == CodingAgentCacheWarmingMode.Off) return new("inactive", "cache warming disabled");
            ActiveRun? run;
            lock (_gate) { run = _run; if (run is null) return _inactive; }
            if (!run.IsCurrent()) return new("inactive", "conversation context changed");
            var decision = Evaluate(run);
            lock (_gate)
            {
                if (_run != run) return _inactive;
                if (!decision.EconomicsAvailable && run.Timer is not null) return new("inactive", "cache economics unavailable");
                return new(run.Timer is null ? "refreshing" : "scheduled", NextWarmAt: run.NextWarmAt,
                    Decision: decision, ExtensionOverride: run.ExtensionOverride);
            }
        }
    }

    /// <summary>【CodingAgent】【启动预热】替换旧请求；保留独立取消信号和不会被刷新延长的起始时间。</summary>
    /// <param name="model">实际发送模型。</param><param name="context">实际发送上下文。</param>
    /// <param name="options">实际请求参数。</param><param name="isCurrent">会话、模型及上下文是否仍匹配。</param>
    /// <param name="streamFunction">本次真实请求的发送函数覆盖。</param><param name="onWarmed">绑定本次会话身份的记账覆盖。</param>
    public void Start(Model model, LlmContext context, SimpleStreamOptions options, Func<bool> isCurrent,
        AgentStreamFunction? streamFunction = null, Action<AssistantMessage, string?>? onWarmed = null)
    {
        var mode = _getMode();
        var ttl = GetPromptCacheTtlMs(model, options);
        var delay = ttl is { } duration ? GetCacheWarmingDelayMs(duration) : null;
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked("inactive");
            var reason = mode == CodingAgentCacheWarmingMode.Off ? "cache warming disabled"
                : !IsReplayable(model, options) ? "request cannot be replayed safely"
                : options.HasExplicitCacheRetention && options.CacheRetention == CacheRetention.None ? "request disabled prompt caching"
                : delay is null ? "cache lifetime unavailable" : null;
            if (reason is not null) { _inactive = new("inactive", reason); return; }
            var run = new ActiveRun(model, context, options, isCurrent, ttl!.Value, delay!.Value, _clock.GetUtcNow(),
                streamFunction ?? _stream, onWarmed ?? _onWarmed);
            _run = run;
            ScheduleLocked(run);
        }
    }

    /// <summary>【CodingAgent】【结算预热】运行结束后按模式停止，或切入更短的空闲安全期限。</summary>
    public void OnAgentSettled()
    {
        var mode = _getMode();
        lock (_gate)
        {
            if (_run is not { } run) return;
            if (mode != CodingAgentCacheWarmingMode.Idle) { StopLocked(mode == CodingAgentCacheWarmingMode.Off ? "cache warming disabled" : "agent run settled"); return; }
            run.Phase = "idle";
            if (run.NextWarmAt > run.StartedAt.AddMinutes(30) || _clock.GetUtcNow() >= run.StartedAt.AddMinutes(30))
                StopLocked("30-minute idle safety limit reached");
        }
    }

    /// <summary>【CodingAgent】【模式变更】立即取消新模式不允许继续的预热。</summary>
    public void OnModeChanged()
    {
        var mode = _getMode();
        lock (_gate) if (_run is { } run && ModeStopReason(run, mode) is { } reason) StopLocked(reason);
    }

    /// <summary>【CodingAgent】【取消预热】取消计时和进行中的请求，不影响真实 Agent 的取消信号。</summary>
    public void Cancel() { lock (_gate) StopLocked("inactive"); }

    /// <summary>【CodingAgent】【预热释放】停止新调度并等待已启动的回调和请求结束。</summary>
    /// <returns>所有已退休请求完成后的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        Task[] retired;
        lock (_gate) { _disposed = true; StopLocked("inactive"); retired = _retirements.ToArray(); }
        await Task.WhenAll(retired).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【预热退休】在持锁状态摘除旧请求，异步取消回调避免同步重入。</summary>
    /// <param name="reason">停止原因。</param><param name="decision">最后决策。</param><param name="extensionOverride">是否为扩展覆盖。</param>
    private void StopLocked(string reason, CodingAgentCacheWarmingDecision? decision = null, bool extensionOverride = false)
    {
        var run = _run; _run = null;
        _inactive = new("inactive", reason, Decision: decision, ExtensionOverride: extensionOverride);
        _retirements.RemoveAll(task => task.IsCompleted);
        if (run is null) return;
        run.Timer?.Dispose(); run.Timer = null;
        _retirements.Add(RetireAsync(run, run.Controller.CancelAsync(), run.Operations.ToArray()));
    }

    /// <summary>【CodingAgent】【取消回收】观察取消处理器异常并在操作结束后释放取消源。</summary>
    /// <param name="run">已退休请求。</param><param name="cancellation">异步取消操作。</param><param name="operations">启动的刷新操作。</param>
    /// <returns>资源释放任务。</returns>
    private static async Task RetireAsync(ActiveRun run, Task cancellation, Task[] operations)
    {
        try { await cancellation.ConfigureAwait(false); } catch { }
        await Task.WhenAll(operations).ConfigureAwait(false);
        run.Controller.Dispose();
    }

    /// <summary>【CodingAgent】【刷新调度】固定起始期限，不因成功预热延长一小时或半小时窗口。</summary>
    /// <param name="run">仍为当前的请求。</param>
    private void ScheduleLocked(ActiveRun run)
    {
        var now = _clock.GetUtcNow();
        run.ExtensionOverride = false;
        var horizon = run.StartedAt.AddMinutes(run.Phase == "idle" ? 30 : 60);
        if (run.DelayMs > (horizon - now).TotalMilliseconds || now >= horizon)
        { StopLocked(run.Phase == "idle" ? "30-minute idle safety limit reached" : "one-hour safety limit reached"); return; }
        run.NextWarmAt = now.AddMilliseconds(run.DelayMs);
        run.RefreshDeadlineAt = run.NextWarmAt.AddMilliseconds(Math.Floor((run.TtlMs - run.DelayMs) / 2));
        run.Timer = _clock.CreateTimer(_ => BeginRefresh(run), null, TimeSpan.FromMilliseconds(run.DelayMs), Timeout.InfiniteTimeSpan);
    }

    /// <summary>【CodingAgent】【启动刷新】先登记完成任务，再执行异步代码，避免取消与释放遗漏请求。</summary>
    /// <param name="run">定时器关联请求。</param>
    private void BeginRefresh(ActiveRun run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_run != run || run.Timer is null) return;
            run.Timer.Dispose(); run.Timer = null;
            run.Operations.RemoveAll(task => task.IsCompleted); run.Operations.Add(completion.Task);
        }
        _ = RefreshAsync(run, completion);
    }

    /// <summary>【CodingAgent】【执行刷新】决策后再次检查身份和时限；成功只记用量，不写入对话。</summary>
    /// <param name="run">待刷新请求。</param><param name="completion">资源回收等待的完成信号。</param><returns>刷新任务。</returns>
    private async Task RefreshAsync(ActiveRun run, TaskCompletionSource completion)
    {
        try
        {
            // 1. 【CodingAgent】【刷新决策】阻止休眠唤醒后的迟到定时器触发全价请求
            if (!Validate(run, checkDeadline: true)) return;
            var decision = Evaluate(run);
            var action = decision.Action;
            try { var selected = await _decide(decision).ConfigureAwait(false); if (selected is "warm" or "stop") action = selected; } catch { }
            if (!Validate(run, checkDeadline: true)) return;
            var overridden = action != decision.Action;
            lock (_gate)
            {
                if (_run != run) return;
                if (action == "stop")
                {
                    StopLocked(overridden ? "stopped by extension" : decision.EconomicsAvailable
                        ? "expected savings below threshold" : "cache economics unavailable", decision, overridden);
                    return;
                }
                run.ExtensionOverride = overridden;
            }
            // 2. 【CodingAgent】【请求重放】仅改变输出上限、重试次数与取消信号，保留工具及请求回调
            try
            {
                var stream = await run.Stream(run.Model, run.Context, run.Options with
                { MaxTokens = 1, MaxRetries = 0, Signal = run.Signal }).ConfigureAwait(false);
                var message = await stream.ResultAsync.ConfigureAwait(false);
                if (!Validate(run)) return;
                if (message.StopReason is not (StopReason.Error or StopReason.Aborted))
                    run.OnWarmed(message, overridden ? "extension override" : null);
            }
            catch { }
            if (!Validate(run)) return;
            lock (_gate) if (_run == run) ScheduleLocked(run);
        }
        catch
        {
            // 3. 【CodingAgent】【预热隔离】计费或宿主回调失败不得影响真实运行，也不得遗留无调度的活动状态
            lock (_gate) if (_run == run) StopLocked("cache warming failed");
        }
        finally { completion.TrySetResult(); }
    }

    /// <summary>【CodingAgent】【刷新校验】异步扩展或网络返回后核对模式、上下文和发送时限。</summary>
    /// <param name="run">操作归属请求。</param><param name="checkDeadline">是否检查尚未发送的刷新期限。</param><returns>能否继续。</returns>
    private bool Validate(ActiveRun run, bool checkDeadline = false)
    {
        lock (_gate) if (_run != run) return false;
        var reason = ModeStopReason(run, _getMode()) ?? (!run.IsCurrent() ? "conversation context changed" : null);
        if (reason is null && checkDeadline && _clock.GetUtcNow() > run.RefreshDeadlineAt) reason = "cache refresh deadline missed";
        lock (_gate)
        {
            if (_run != run) return false;
            if (reason is null) return true;
            StopLocked(reason); return false;
        }
    }

    /// <summary>【CodingAgent】【模式校验】确定当前阶段是否被全局模式禁止。</summary>
    /// <param name="run">活动请求。</param><param name="mode">全局模式。</param><returns>停止原因或空。</returns>
    private static string? ModeStopReason(ActiveRun run, CodingAgentCacheWarmingMode mode) => mode == CodingAgentCacheWarmingMode.Off
        ? "cache warming disabled" : mode == CodingAgentCacheWarmingMode.Streaming && run.Phase == "idle" ? "agent run settled" : null;

    /// <summary>【CodingAgent】【预热计费】按最新真实输入用量和阶梯价格计算预期净节省。</summary>
    /// <param name="run">活动请求。</param><returns>阈值为五美分的默认决策。</returns>
    private CodingAgentCacheWarmingDecision Evaluate(ActiveRun run)
    {
        var tokens = Math.Max(0, _getPromptTokens());
        var hit = ModelCatalog.CalculateCost(run.Model, new Usage(0, 0, CacheReadTokens: tokens)).Total;
        var miss = ModelCatalog.CalculateCost(run.Model, run.Model.Cost?.CacheWritePerMillion > 0
            ? new Usage(0, 0, CacheWriteTokens: tokens) : new Usage(tokens, 0)).Total;
        var warm = ModelCatalog.CalculateCost(run.Model, new Usage(0, 1, CacheReadTokens: tokens)).Total;
        var extra = Math.Max(0, miss - hit);
        var probability = run.Phase == "idle" ? .15m : 1m;
        var savings = probability * extra - warm;
        return new(run.Phase, warm, extra, probability, savings, tokens > 0 && (hit > 0 || miss > 0), savings >= .05m ? "warm" : "stop");
    }

    /// <summary>【CodingAgent】【预热请求】保存一次真实请求的固定起点和所有进行中的刷新任务。</summary>
    private sealed class ActiveRun(Model model, LlmContext context, SimpleStreamOptions options, Func<bool> isCurrent,
        double ttlMs, double delayMs, DateTimeOffset startedAt, AgentStreamFunction stream, Action<AssistantMessage, string?> onWarmed)
    {
        public Model Model { get; } = model;
        public LlmContext Context { get; } = context;
        public SimpleStreamOptions Options { get; } = options;
        public AgentStreamFunction Stream { get; } = stream;
        public Action<AssistantMessage, string?> OnWarmed { get; } = onWarmed;
        public Func<bool> IsCurrent { get; } = isCurrent;
        public double TtlMs { get; } = ttlMs;
        public double DelayMs { get; } = delayMs;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public CancellationTokenSource Controller { get; } = new();
        public CancellationToken Signal => Controller.Token;
        public List<Task> Operations { get; } = [];
        public string Phase { get; set; } = "streaming";
        public DateTimeOffset NextWarmAt { get; set; }
        public DateTimeOffset RefreshDeadlineAt { get; set; }
        public bool ExtensionOverride { get; set; }
        public ITimer? Timer { get; set; }
    }
}
