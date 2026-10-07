// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentCacheWarmer? _cacheWarmer;
    private TimeProvider _cacheWarmingClock = TimeProvider.System;
    private CodingAgentCacheWarmingMode _cacheWarmingMode = CodingAgentCacheWarmingMode.Streaming;
    private long _cacheRequestGeneration;

    /// <summary>【CodingAgent】【预热状态】读取当前预热调度及收益；尚未绑定会话时为空。</summary>
    public CodingAgentCacheWarmingStatus? CacheWarmingStatus => _cacheWarmer?.Status;

    /// <summary>【CodingAgent】【预热模式】读取全局模式；独立运行器使用内存设置。</summary>
    public CodingAgentCacheWarmingMode CacheWarmingMode => _sessionSettings?.GetCacheWarmingMode() ?? _cacheWarmingMode;

    /// <summary>【CodingAgent】【预热模式】保存全局设置并立即取消被新模式禁止的请求。</summary>
    /// <param name="mode">新的全局模式。</param>
    public void SetCacheWarmingMode(CodingAgentCacheWarmingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _sessionSettings?.SetCacheWarmingMode(mode); _cacheWarmingMode = mode; _cacheWarmer?.OnModeChanged();
    }

    /// <summary>【CodingAgent】【预热时钟】在绑定会话前为 SDK 设置可控调度时钟。</summary>
    /// <param name="clock">SDK 指定时钟；空值保留系统时钟。</param>
    internal void ConfigureCacheWarmingClock(TimeProvider? clock) => _cacheWarmingClock = clock ?? TimeProvider.System;

    /// <summary>【CodingAgent】【预热绑定】连接原始分支用量与独立记账，旧平面会话不启动无法持久化的预热。</summary>
    /// <param name="session">当前会话存储桥接。</param>
    private void ConfigureCacheWarmingSession(CodingAgentExtensionSessionBridge session)
    {
        CancelCacheWarming();
        _cacheWarmer ??= new(SendCacheRequestAsync, () => _boundarySession?.ReadCachePromptTokens() ?? 0,
            () => CacheWarmingMode, (_, _) => { }, async decision => _extensionLifecycleEventSink is { } sink
                ? await sink.EmitCacheWarmingDecisionAsync(decision, error => LogExtensionEventError(error, CreateRunLogContext())).ConfigureAwait(false)
                : decision.Action, _cacheWarmingClock);
    }

    /// <summary>【CodingAgent】【直接请求】沿用模型配置及认证发送重放，避免递归启动预热。</summary>
    /// <param name="model">实际模型。</param><param name="context">实际上下文。</param><param name="options">重放参数。</param>
    /// <returns>提供方响应流。</returns>
    private ValueTask<AssistantMessageStream> SendCacheRequestAsync(Model model, LlmContext context, SimpleStreamOptions options) =>
        ValueTask.FromResult(StreamFunctions.StreamSimple(_config.ProviderRegistry, model, context, options, _modelCatalog.ConfigurationStore, _authResolver));

    /// <summary>【CodingAgent】【捕获入口】内置发送等待认证及模型参数合并，自定义函数按其收到的原始参数捕获。</summary>
    /// <returns>本次运行的发送函数。</returns>
    private AgentStreamFunction CreateCacheWarmingStreamFunction()
    {
        var custom = _config.StreamFunction ?? AgentStreaming.GetDefaultStreamFunction();
        if (custom is not null) return (model, context, options) =>
        {
            CaptureCacheRequest(model, context, options, options, custom);
            return custom(model, context, options);
        };
        return (model, context, options) => ValueTask.FromResult(StreamFunctions.StreamSimple(_config.ProviderRegistry,
            model, context, options, _modelCatalog.ConfigurationStore, _authResolver,
            (actualModel, actualContext, simple, actual) => CaptureCacheRequest(actualModel, actualContext, simple, actual, SendCacheRequestAsync)));
    }

    /// <summary>【CodingAgent】【捕获请求】记录最终上下文及参数；参数解析或计费失败不能妨碍真实请求。</summary>
    /// <param name="model">实际模型。</param><param name="context">最终上下文。</param><param name="options">已合并简化选项。</param>
    /// <param name="actualOptions">最终协议选项。</param><param name="send">直接发送函数。</param>
    private void CaptureCacheRequest(Model model, LlmContext context, SimpleStreamOptions options, StreamOptions actualOptions, AgentStreamFunction send)
    {
        try
        {
            if (actualOptions is Tau.Ai.Providers.Anthropic.AnthropicOptions { ThinkingEnabled: true } && model.Compat?.ForceAdaptiveThinking != true)
            { CancelCacheWarming(); return; }
            var session = _boundarySession;
            if (session?.CanPersistCacheUsage == true && _cacheWarmer is { } warmer)
            {
                var generation = Interlocked.Increment(ref _cacheRequestGeneration);
                var id = SessionId;
                var messages = Messages.ToArray();
                // 1. 【CodingAgent】【上下文身份】按引用前缀检查新增消息，替换、编辑、压缩或切换会话使旧请求失效
                /// <summary>【CodingAgent】【请求身份】验证当前请求仍属于原会话上下文。</summary><returns>是否可继续预热及记账。</returns>
                bool IsCurrent()
                {
                    if (generation != Interlocked.Read(ref _cacheRequestGeneration) || SessionId != id || !ReferenceEquals(session, _boundarySession)
                        || Model.Provider != model.Provider || Model.Id != model.Id) return false;
                    try { return messages.Length <= Messages.Count && messages.Select((message, index) => ReferenceEquals(message, Messages[index])).All(value => value); }
                    catch (ArgumentOutOfRangeException) { return false; }
                }
                warmer.Start(model, context, options, IsCurrent, send, (message, note) =>
                {
                    var entry = session.AppendCacheUsage(message, note, IsCurrent);
                    if (entry is null) return;
                    var json = JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
                    QueueStateNotification(JsonSerializer.SerializeToElement(new { type = "entry_appended", entry = json }), new CodingAgentEntryAppendedEvent(json));
                });
            }
        }
        catch (Exception error) { CancelCacheWarming(); LogRunError(error, CreateRunLogContext()); }
    }

    /// <summary>【CodingAgent】【上下文失效】取消旧预热并使已经返回但尚未记账的响应失效。</summary>
    private void CancelCacheWarming() { Interlocked.Increment(ref _cacheRequestGeneration); _cacheWarmer?.Cancel(); }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    internal bool CanPersistCacheUsage => _tree is not null || _flat is null;

    /// <summary>【CodingAgent】【真实输入计费】只采用原始当前分支最近一条助手消息，不回退旧用量或预热用量。</summary>
    /// <returns>输入、缓存读和缓存写 token 总量。</returns>
    internal int ReadCachePromptTokens()
    {
        lock (_gate)
        {
            var last = GetSnapshotBranch(Snapshot()).LastOrDefault(entry => entry.Type == "message" && entry.Message?.Role == "assistant");
            if (last?.Message is null || CodingAgentSessionStore.ToMessage(last.Message) is not AssistantMessage { Usage: { } usage }) return 0;
            return (int)Math.Clamp((long)usage.InputTokens + usage.CacheReadTokens.GetValueOrDefault() + usage.CacheWriteTokens.GetValueOrDefault(), 0, int.MaxValue);
        }
    }

    /// <summary>【CodingAgent】【预热记账】持有会话锁重新验证请求身份，只追加不参与上下文的用量条目。</summary>
    /// <param name="message">重放结果。</param><param name="note">扩展覆盖说明。</param><param name="isCurrent">请求身份检查。</param>
    /// <returns>保存的条目；身份已失效时为空。</returns>
    internal CodingAgentTreeSessionEntry? AppendCacheUsage(AssistantMessage message, string? note, Func<bool> isCurrent)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            if (!CanPersistCacheUsage || !isCurrent()) return null;
            var usage = message.Usage ?? new Usage();
            if (_tree is not null)
            {
                lock (_tree.Store.SyncRoot)
                {
                    if (!isCurrent()) return null;
                    var id = _tree.Store.AppendUsage("cache_warm", message.Provider ?? _runner.Model.Provider,
                        message.ResponseModel ?? message.Model ?? _runner.Model.Id, usage, note);
                    _tree.LoadSnapshot();
                    return _tree.Store.ReadExtensionSnapshot().Entries.Single(entry => entry.Id == id);
                }
            }
            var serialized = JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(message with { Usage = usage }),
                CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
            var entry = new CodingAgentTreeSessionEntry { Type = "usage", Id = Guid.NewGuid().ToString("N"), ParentId = snapshot.LeafId,
                Timestamp = DateTimeOffset.UtcNow, Kind = "cache_warm", Provider = message.Provider ?? _runner.Model.Provider,
                Model = message.ResponseModel ?? message.Model ?? _runner.Model.Id, Usage = serialized.GetProperty("usage").Clone(), Note = note };
            AppendMemoryEntry(entry); return entry;
        }
    }
}
