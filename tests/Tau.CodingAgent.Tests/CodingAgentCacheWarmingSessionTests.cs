// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【设置联动】设置命令切换全局模式并立即停掉已经安排的空闲预热。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task CacheWarming_SettingsCommandStopsScheduledIdleRefresh()
    {
        using var fixture = new Fixture("cache-ui", "models"); WriteCacheProvider(fixture);
        var clock = new CodingAgentCacheWarmerTests.ManualClock();
        await using var session = await CreateCacheSessionAsync(fixture, clock, new BoundaryLogSink(), false);
        session.Runner.SetCacheWarmingMode(CodingAgentCacheWarmingMode.Idle);
        await DrainAsync(session.RunAsync("first"));
        var router = new CodingAgentCommandRouter(session.Runner, session.SettingsStore, settingsSelector: (state, _) =>
        {
            Assert.Equal(CodingAgentCacheWarmingMode.Idle, state.CacheWarmingMode);
            return Task.FromResult<string?>(CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.CacheWarmingAction, "off"));
        });
        var result = await router.TryHandleAsync("/settings select");
        Assert.Contains("cache warming: off", result.Message);
        Assert.Equal(CodingAgentCacheWarmingMode.Off, session.SettingsStore.GetCacheWarmingMode());
        Assert.Equal("cache warming disabled", session.Runner.CacheWarmingStatus!.Reason);
        Assert.Contains("cache warming: off", (await router.TryHandleAsync("/session")).Message);
        clock.Advance(50000); Assert.False(File.Exists(Path.Combine(fixture.Root, "warm-options.json")));
    }
    /// <summary>【CodingAgent】【预热会话】真实扩展提供方的预热写入独立用量，并保持消息及会话可继续。</summary>
    /// <param name="persistent">是否保存 JSONL。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CacheWarming_PersistsUsageWithoutChangingConversation(bool persistent)
    {
        using var fixture = new Fixture("cache-usage", "models");
        WriteCacheProvider(fixture);
        var clock = new CodingAgentCacheWarmerTests.ManualClock(); var log = new BoundaryLogSink();
        await using var session = await CreateCacheSessionAsync(fixture, clock, log, persistent);
        session.Runner.SetCacheWarmingMode(CodingAgentCacheWarmingMode.Idle);
        await DrainAsync(session.RunAsync("first"));
        var messages = session.Messages.ToArray();
        var before = session.Runner.GetSessionStats().Cost;
        var appended = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Runner.BackgroundEvent += (evt, _) =>
        {
            if (evt is CodingAgentEntryAppendedEvent entry) appended.TrySetResult(entry.Entry);
            return Task.CompletedTask;
        };
        Assert.Equal("scheduled", session.Runner.CacheWarmingStatus!.State);
        clock.Advance(50000);
        var usage = await appended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("usage", usage.GetProperty("type").GetString());
        Assert.Equal("cache_warm", usage.GetProperty("kind").GetString());
        Assert.Equal("billed", usage.GetProperty("model").GetString());
        Assert.Equal(before + .100003m, session.Runner.GetSessionStats().Cost);
        Assert.Equal(messages, session.Messages);
        using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "warm-options.json")));
        Assert.Equal(1, request.RootElement.GetProperty("maxTokens").GetInt32());
        Assert.Equal(0, request.RootElement.GetProperty("maxRetries").GetInt32());
        Assert.Equal("short", request.RootElement.GetProperty("cacheRetention").GetString());
        if (persistent) Assert.Contains("\"kind\":\"cache_warm\"", File.ReadAllText(session.TreeSessionController!.Store.Path));
        await DrainAsync(session.RunAsync("second"));
        Assert.Equal(messages.Length + 2, session.Messages.Count);
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
    }

    /// <summary>【CodingAgent】【模式及切换】运行结束默认停止，关闭模式不调度，重置使空闲预热失效。</summary>
    /// <param name="mode">全局预热模式。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(CodingAgentCacheWarmingMode.Off)] [InlineData(CodingAgentCacheWarmingMode.Streaming)]
    [InlineData(CodingAgentCacheWarmingMode.Idle)]
    public async Task CacheWarming_StopsOnSettlementOrSessionReset(CodingAgentCacheWarmingMode mode)
    {
        using var fixture = new Fixture("cache-mode", "models"); WriteCacheProvider(fixture);
        var clock = new CodingAgentCacheWarmerTests.ManualClock();
        await using var session = await CreateCacheSessionAsync(fixture, clock, new BoundaryLogSink(), false);
        session.Runner.SetCacheWarmingMode(mode);
        await DrainAsync(session.RunAsync("first"));
        if (mode == CodingAgentCacheWarmingMode.Idle) session.Runner.ResetSession();
        clock.Advance(50000);
        Assert.Equal("inactive", session.Runner.CacheWarmingStatus!.State);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "warm-options.json")));
    }

    /// <summary>【CodingAgent】【扩展覆盖】跨模块最后有效动作胜出，所有处理器收到原始决策，覆盖来源被持久记录。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task CacheWarming_ExtensionsOverrideUnknownEconomicsAcrossModules()
    {
        using var fixture = new Fixture("cache-extension", "models"); WriteCacheProvider(fixture, unknownUsage: true);
        WriteBoundaryExtension(fixture, "a-decision.js", """
            export default pi=>pi.on('cache_warming_decision',event=>{
              if(event.action!=='stop'||event.continuationProbability!==0.15)throw Error('wrong original decision');
              return {action:'warm'};
            });
            """);
        WriteBoundaryExtension(fixture, "z-decision.js", """
            import fs from 'node:fs';
            export default pi=>{
              pi.on('cache_warming_decision',event=>{
                if(event.action!=='stop')throw Error('decision was mutated');
                fs.writeFileSync('decision-seen','');return {action:'stop'};
              });
              pi.on('cache_warming_decision',()=>({action:'warm'}));
              pi.on('cache_warming_decision',()=>({action:'invalid'}));
              pi.on('cache_warming_decision',()=>{throw Error('expected decision failure');});
            };
            """);
        var clock = new CodingAgentCacheWarmerTests.ManualClock(); var log = new BoundaryLogSink();
        await using var session = await CreateCacheSessionAsync(fixture, clock, log, true);
        session.Runner.SetCacheWarmingMode(CodingAgentCacheWarmingMode.Idle);
        await DrainAsync(session.RunAsync("first"));
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Runner.BackgroundEvent += (evt, _) => { if (evt is CodingAgentEntryAppendedEvent entry) completed.TrySetResult(entry.Entry); return Task.CompletedTask; };
        clock.Advance(50000);
        var usage = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("extension override", usage.GetProperty("note").GetString());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "decision-seen")));
        Assert.Single(log.Events, item => item.Event == "event.error");
    }

    /// <summary>【CodingAgent】【全局配置】项目覆盖不能开启或关闭付费刷新，保存时保留项目及其他全局字段。</summary>
    [Fact]
    public void CacheWarmingSettings_OnlyUseGlobalLayer()
    {
        using var fixture = new Fixture("cache-settings", "models");
        var global = Path.Combine(fixture.AgentDirectory, "settings.json");
        var project = Path.Combine(fixture.Root, "project-settings.json");
        File.WriteAllText(global, "{\"cacheWarming\":\"off\",\"theme\":\"dark\"}");
        File.WriteAllText(project, "{\"cacheWarming\":\"idle\"}");
        var settings = CodingAgentSettingsStore.CreateLayered(global, project);
        Assert.Equal(CodingAgentCacheWarmingMode.Off, settings.GetCacheWarmingMode());
        settings.SetCacheWarmingMode(CodingAgentCacheWarmingMode.Streaming);
        Assert.Equal(CodingAgentCacheWarmingMode.Streaming, settings.GetCacheWarmingMode());
        Assert.Equal("dark", settings.LoadGlobal().Theme);
        Assert.Contains("idle", File.ReadAllText(project));
        File.WriteAllText(global, "{\"cacheWarming\":true}");
        Assert.Equal(CodingAgentCacheWarmingMode.Streaming, settings.GetCacheWarmingMode());
    }

    /// <summary>【CodingAgent】【预热夹具】创建真实 Node 提供方和可控时钟，不访问网络。</summary>
    /// <param name="fixture">临时工作目录。</param><param name="clock">手动时钟。</param>
    /// <param name="log">错误收集器。</param><param name="persistent">是否使用 JSONL。</param><returns>SDK 会话。</returns>
    private static async Task<CodingAgentSdkSession> CreateCacheSessionAsync(Fixture fixture,
        CodingAgentCacheWarmerTests.ManualClock clock, BoundaryLogSink log, bool persistent)
    {
        var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = !persistent,
            SessionPath = persistent ? Path.Combine(fixture.Root, "cache.jsonl") : null,
            ProviderId = "cache-fixture", ModelId = "test", NoTools = CodingAgentSdkNoToolsMode.All,
            CacheWarmingTimeProvider = clock, LogSink = log
        });
        session.Runner.SetAutoCompactionEnabled(false); return session;
    }

    /// <summary>【CodingAgent】【预热提供方】构造能分辨真实请求与单 token 重放的扩展提供方。</summary>
    /// <param name="fixture">临时目录。</param><param name="unknownUsage">是否让真实请求返回零输入用量。</param>
    private static void WriteCacheProvider(Fixture fixture, bool unknownUsage = false)
    {
        WriteBoundaryExtension(fixture, "provider.js", """
            import fs from 'node:fs';
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>pi.registerProvider('cache-fixture',{api:'cache-fixture',baseUrl:'https://fixture.test',apiKey:'fixture',
              models:[{id:'test',name:'Test',contextWindow:1000000,maxTokens:1000,promptCache:{short:60,long:3600},
                cost:{input:10,output:3,cacheRead:1,cacheWrite:12.5}}],streamSimple:(model,context,options)=>{
                const warm=options.maxTokens===1;
                if(warm)fs.writeFileSync('warm-options.json',JSON.stringify(options));
                const usage={input:warm?0:__TOKENS__,output:1,cacheRead:warm?100000:0,cacheWrite:0,totalTokens:100001,
                  cost:{input:warm?0:1,output:0.000003,cacheRead:warm?0.1:0,cacheWrite:0,total:warm?0.100003:1.000003}};
                const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,responseModel:'billed',
                  content:[{type:'text',text:warm?'warm':'answer'}],stopReason:'stop',timestamp:Date.now(),usage};
                const output=createAssistantMessageEventStream(); output.push({type:'done',reason:'stop',message});return output;
              }});
            """.Replace("__TOKENS__", unknownUsage ? "0" : "100000", StringComparison.Ordinal));
    }
}
