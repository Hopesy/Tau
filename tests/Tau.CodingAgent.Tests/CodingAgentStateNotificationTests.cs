// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【关闭等待】关闭 SDK 会话必须等待已经开始的状态通知，返回后不再执行后台持久化。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StateNotifications_SdkDisposalWaitsForInFlightHostEvents()
    {
        using var temp = TempDirectory.Create();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = false, ModelCatalog = CreateModelCatalog()
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        session.Runner.BackgroundEvent += async (evt, _) =>
        {
            if (evt is not CodingAgentSessionInfoChangedEvent) return;
            entered.TrySetResult();
            await release.Task;
            session.Save();
            completed = true;
        };
        session.Runner.SetSessionName("pending");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposing = session.DisposeAsync().AsTask();
        try { Assert.False(disposing.IsCompleted); }
        finally { release.TrySetResult(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(completed);
        Assert.Equal("pending", session.SessionStore!.Load().Name);
    }
}

public sealed partial class CodingAgentExtensionRuntimeControlTests
{
    /// <summary>【CodingAgent】【模型快照】轮换通知读取已应用的作用域思考等级，处理器错误不会阻止后续处理器。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StateNotifications_CycleUsesEffectiveThinkingAndIsolatesErrors()
    {
        using var fixture = new Fixture(eventTypes: ["model_select"]);
        var file = fixture.Write("events.js", """
            module.exports=pi=>{
              pi.on('model_select',()=>{throw Error('expected handler failure')});
              pi.on('model_select',(e,ctx)=>pi.appendEntry('cycle_snapshot',{
                model:ctx.model.id,level:pi.getThinkingLevel(),source:e.source
              }));
            };
            """);
        await fixture.Runner.SelectModelWithSourceAsync("control-test", "next", "cycle", thinkingLevelOverride: "high");
        await fixture.Runner.WaitForStateNotificationsAsync();
        var entry = Assert.Single(fixture.Tree.Store.ReadExtensionSnapshot().Entries,
            entry => entry.Type == "custom" && entry.CustomType == "cycle_snapshot");
        var data = Assert.IsType<JsonElement>(entry.Data);
        Assert.Equal("next", data.GetProperty("model").GetString());
        Assert.Equal("high", data.GetProperty("level").GetString());
        Assert.Equal("cycle", data.GetProperty("source").GetString());
    }

    /// <summary>【CodingAgent】【绑定隔离】即使使用同一运行器重新绑定，旧绑定中尚未分发的通知也不能进入新扩展。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StateNotifications_DetachDropsQueuedNotifications()
    {
        using var fixture = new Fixture(eventTypes: ["session_info_changed"]);
        fixture.Write("events.js", """
            module.exports=pi=>pi.on('session_info_changed',e=>pi.appendEntry('name_event',{name:e.name}));
            """);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BackgroundEvent += async (evt, _) =>
        {
            if (evt is CodingAgentSessionInfoChangedEvent { Name: "old" })
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        fixture.Runner.SetSessionName("old");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Runner.SetSessionName("queued");
            fixture.Runner.DisableStateNotifications();
            fixture.Runner.EnableStateNotifications();
            fixture.Runner.SetSessionName("current");
        }
        finally { release.TrySetResult(); }
        await fixture.Runner.WaitForStateNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var entry = Assert.Single(fixture.Tree.Store.ReadExtensionSnapshot().Entries,
            entry => entry.Type == "custom" && entry.CustomType == "name_event");
        Assert.Equal("current", Assert.IsType<JsonElement>(entry.Data).GetProperty("name").GetString());
    }

    /// <summary>【CodingAgent】【状态通知】模型通知可重入宿主，思考去重、名称清除和恢复来源均使用真实会话状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StateNotifications_AllowReentryAndPreserveSources()
    {
        using var fixture = new Fixture(eventTypes: ["model_select", "thinking_level_select", "session_info_changed"]);
        var file = fixture.Write("events.js", """
            module.exports=pi=>{
              const seen=[];
              pi.on('model_select',async(e,ctx)=>{
                if(ctx.model.id!==e.model.id||!e.model.input.includes('text')||e.model.maxTokens!==200)throw Error('model snapshot');
                seen.push({type:e.type,model:e.model.id,previous:e.previousModel.id,source:e.source});
                pi.appendEntry('model_event',{model:e.model.id});
                if(e.source==='set'&&e.model.id==='next')await pi.setModel(ctx.modelRegistry.find('control-test','first'));
              });
              pi.on('thinking_level_select',e=>seen.push(e));
              pi.on('session_info_changed',e=>seen.push({...e,cleared:e.name===undefined}));
              pi.registerCommand('run',{handler:async(_,ctx)=>{
                await pi.setModel(ctx.modelRegistry.find('control-test','next'));
                if(seen.filter(e=>e.type==='model_select').length!==2||ctx.model.id!=='first')throw Error('model events not awaited');
                await pi.setModel(ctx.modelRegistry.find('control-test','first'));
                pi.setThinkingLevel('high'); pi.setThinkingLevel('high');
                pi.setSessionName('renamed');
                return 'done';
              }});
              pi.registerCommand('seen',{handler:()=>JSON.stringify(seen)});
            };
            """);
        var hostEvents = new List<string>();
        fixture.Runner.BackgroundEvent += (evt, _) => { lock (hostEvents) hostEvents.Add(evt.Type); return Task.CompletedTask; };
        var result = await Task.Run(() => fixture.Runtime.Invoke(file, "run", "")).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Success, result.Error);
        await fixture.Runner.WaitForStateNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Runner.SetSessionName(null);
        await fixture.Runner.WaitForStateNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Runner.SelectModelWithSourceAsync("control-test", "next", "cycle");
        fixture.Runner.RestoreSession(new(fixture.Runner.Messages, "control-test", "first", null));
        await fixture.Runner.WaitForStateNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var json = JsonDocument.Parse(fixture.Runtime.Invoke(file, "seen", "").StatusMessage!);
        var models = json.RootElement.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "model_select").ToArray();
        Assert.Equal(["set", "set", "cycle", "restore"], models.Select(item => item.GetProperty("source").GetString()));
        Assert.Equal(["first", "next", "first", "next"], models.Select(item => item.GetProperty("previous").GetString()));
        var thinking = Assert.Single(json.RootElement.EnumerateArray(), item => item.GetProperty("type").GetString() == "thinking_level_select");
        Assert.Equal("off", thinking.GetProperty("previousLevel").GetString());
        Assert.Equal("high", thinking.GetProperty("level").GetString());
        var names = json.RootElement.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "session_info_changed").ToArray();
        Assert.Equal("renamed", names[0].GetProperty("name").GetString());
        Assert.True(names[1].GetProperty("cleared").GetBoolean());
        Assert.Equal(2, hostEvents.Count(type => type == "session_info_changed"));
        Assert.Single(hostEvents, type => type == "thinking_level_changed");
        Assert.Equal(4, fixture.Tree.Store.ReadExtensionSnapshot().Entries.Count(entry => entry.Type == "custom" && entry.CustomType == "model_event"));
    }
}
