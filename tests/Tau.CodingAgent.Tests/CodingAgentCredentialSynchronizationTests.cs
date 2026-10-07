// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【凭据同步】SDK 保存 OAuth、切换密钥和登出后，扩展与宿主均立即读取新权限。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CredentialSynchronization_UpdatesSdkAndExtensionSnapshots()
    {
        using var fixture = new Fixture("credential-sync", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCredentialSyncOptions(fixture), deadline.Token);
        await session.Runner.SaveOAuthCredentialsAsync("sync-a", NativeMenuCredential("[\"yes\"]"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a yes", deadline.Token));
        await session.Runner.SaveOAuthCredentialsAsync("sync-a", NativeMenuCredential("[\"no\"]"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a no", deadline.Token));
        await session.Runner.SaveApiKeyCredentialAsync("sync-a", new("yes"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a yes", deadline.Token));
        Assert.Equal(["yes"], CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner).Where(model => model.Provider == "sync-a").Select(model => model.Id));
        Assert.True(await session.Runner.LogoutAsync("sync-a", deadline.Token));
        await DrainAsync(session.RunAsync("/sync-check sync-a none", deadline.Token));
        Assert.False(await session.Runner.LogoutAsync("sync-a", deadline.Token));
        Assert.DoesNotContain(CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner), model => model.Provider == "sync-a");
    }

    /// <summary>【CodingAgent】【同步失败】刷新、认证检查及模型过滤错误保留已提交凭据，并允许后续操作恢复。</summary>
    /// <param name="key">触发失败阶段的合成密钥。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("refresh-fail")]
    [InlineData("check-fail")]
    [InlineData("filter-fail")]
    public async Task CredentialSynchronization_ReportsCommittedFailure(string key)
    {
        using var fixture = new Fixture("credential-sync-error", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCredentialSyncOptions(fixture), deadline.Token);
        var error = await Assert.ThrowsAsync<CodingAgentCredentialSynchronizationException>(() =>
            session.Runner.SaveApiKeyCredentialAsync("sync-a", new(key), deadline.Token));
        Assert.Equal("sync-a", error.ProviderId);
        Assert.Equal("login", error.Operation);
        Assert.Equal(key, error.Credential!.Value.GetProperty("key").GetString());
        Assert.Equal("Credential login committed for sync-a, but local synchronization failed", error.Message);
        Assert.Contains(key, error.InnerException!.Message);
        Assert.Equal(key, ReadCredentialSyncKey(fixture, "sync-a"));
        await session.Runner.SaveApiKeyCredentialAsync("sync-a", new("no"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a no", deadline.Token));
    }

    /// <summary>【CodingAgent】【队列取消】同一提供方排队期间取消不写入磁盘，其他提供方仍可独立同步。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CredentialSynchronization_SerializesProviderAndCancelsQueuedMutation()
    {
        using var fixture = new Fixture("credential-sync-queue", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCredentialSyncOptions(fixture), deadline.Token);
        var first = session.Runner.SaveApiKeyCredentialAsync("sync-a", new("hold"), deadline.Token);
        await WaitForCredentialSyncHoldAsync(fixture, deadline.Token);
        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var queued = session.Runner.SaveApiKeyCredentialAsync("sync-a", new("no"), queuedCancellation.Token);
        await queuedCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal("hold", ReadCredentialSyncKey(fixture, "sync-a"));
        await session.Runner.SaveApiKeyCredentialAsync("sync-b", new("no"), deadline.Token);
        Assert.False(first.IsCompleted);
        await DrainAsync(session.RunAsync("/sync-check sync-b no", deadline.Token));
        await DrainAsync(session.RunAsync("/sync-release", deadline.Token));
        await first;
        await session.Runner.SaveApiKeyCredentialAsync("sync-a", new("yes"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a yes", deadline.Token));
    }

    /// <summary>【CodingAgent】【提交后取消】同步阶段取消报告已提交，释放队列后下一操作可完成。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CredentialSynchronization_CancellationAfterCommitKeepsCredential()
    {
        using var fixture = new Fixture("credential-sync-cancel", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCredentialSyncOptions(fixture), deadline.Token);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pending = session.Runner.SaveApiKeyCredentialAsync("sync-a", new("hold"), operation.Token);
        await WaitForCredentialSyncHoldAsync(fixture, deadline.Token);
        await operation.CancelAsync();
        var error = await Assert.ThrowsAsync<CodingAgentCredentialSynchronizationException>(() => pending);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal("hold", ReadCredentialSyncKey(fixture, "sync-a"));
        await session.Runner.SaveApiKeyCredentialAsync("sync-a", new("no"), deadline.Token);
        await DrainAsync(session.RunAsync("/sync-check sync-a no", deadline.Token));
    }

    /// <summary>【CodingAgent】【提交前取消】预取消保存和登出均不修改凭据，也不触发刷新。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CredentialSynchronization_PreCancellationDoesNotCommit()
    {
        using var fixture = new Fixture("credential-sync-precancel", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCredentialSyncOptions(fixture), deadline.Token);
        await session.Runner.SaveApiKeyCredentialAsync("sync-a", new("yes"), deadline.Token);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Runner.SaveApiKeyCredentialAsync("sync-a", new("no"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Runner.SaveOAuthCredentialsAsync("sync-a", NativeMenuCredential("[]"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Runner.LogoutAsync("sync-a", cancelled.Token));
        Assert.Equal("yes", ReadCredentialSyncKey(fixture, "sync-a"));
        await DrainAsync(session.RunAsync("/sync-check sync-a yes", deadline.Token));
    }

    /// <summary>【CodingAgent】【同步夹具】创建两个独立提供方，离线刷新和认证检查全部在真实扩展进程内执行。</summary>
    /// <param name="fixture">隔离目录。</param><returns>SDK 配置。</returns>
    private static CodingAgentSdkCreateSessionOptions CreateCredentialSyncOptions(Fixture fixture)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sync.js"), """
            import fs from 'node:fs';
            export default pi=>{
              let release;
              for(const id of ['sync-a','sync-b']){
                const models=['yes','no'].map(key=>({id:key,provider:id,api:'custom',baseUrl:'http://unit.invalid'}));
                pi.registerProvider({id,getModels:()=>models,auth:{
                  oauth:{name:'fixture',login:async()=>{},refresh:async()=>{throw Error('unexpected OAuth refresh');},toAuth:async()=>{throw Error('unexpected OAuth resolve');}},
                  apiKey:{name:'fixture',resolve:async()=>{throw Error('unexpected API key resolve');},check:async({credential})=>{
                    if(credential?.key==='check-fail')throw Error('check-fail');
                    return credential?.key?{type:'api_key',source:'fixture'}:undefined;
                  }}},
                  async refreshModels(ctx){
                    if(ctx.allowNetwork||ctx.force!==undefined)throw Error('credential sync must stay offline');
                    if(ctx.credential?.key==='refresh-fail')throw Error('refresh-fail');
                    if(ctx.credential?.key==='hold'){
                      fs.writeFileSync(__MARKER__,'started');
                      await new Promise((resolve,reject)=>{
                        release=resolve;
                        ctx.signal.addEventListener('abort',()=>reject(Error('cancelled hold')),{once:true});
                      });
                    }
                  },
                  filterModels:(items,credential)=>{
                    if(credential?.key==='filter-fail')throw Error('filter-fail');
                    const ids=credential?.type==='oauth'?credential.availableModelIds??[]:[credential?.key==='hold'?'yes':credential?.key];
                    return items.filter(model=>ids.includes(model.id));
                  }});
              }
              pi.registerCommand('sync-release',{handler:async()=>release?.()});
              pi.registerCommand('sync-check',{handler:async(args,ctx)=>{
                const [provider,expected]=args.split(' '), registry=ctx.modelRegistry;
                const actual=registry.getAvailable().filter(model=>model.provider===provider).map(model=>model.id).join(',')||'none';
                if(actual!==expected)throw Error('stale permission '+actual+' expected '+expected);
                if(registry.hasConfiguredAuth(provider)!==(expected!=='none'))throw Error('stale auth');
                if(registry.getError())throw Error(registry.getError());
              }});
            };
            """.Replace("__MARKER__", JsonValue.Create(Path.Combine(fixture.Root, "sync-hold"))!.ToJsonString()));
        return new() { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" };
    }

    /// <summary>【CodingAgent】【同步屏障】等待扩展进入刷新，避免用固定延时猜测并发顺序。</summary>
    /// <param name="fixture">测试目录。</param><param name="token">测试截止信号。</param><returns>扩展到达屏障的任务。</returns>
    private static async Task WaitForCredentialSyncHoldAsync(Fixture fixture, CancellationToken token)
    {
        while (!File.Exists(Path.Combine(fixture.Root, "sync-hold"))) await Task.Delay(10, token);
    }

    /// <summary>【CodingAgent】【持久校验】直接读取测试 auth.json 中的合成密钥。</summary>
    /// <param name="fixture">测试目录。</param><param name="provider">提供方。</param><returns>已持久化密钥。</returns>
    private static string? ReadCredentialSyncKey(Fixture fixture, string provider)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.AgentDirectory, "auth.json")));
        return json.RootElement.GetProperty(provider).GetProperty("key").GetString();
    }
}
