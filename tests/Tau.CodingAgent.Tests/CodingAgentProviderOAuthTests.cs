// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【OAuth 回归】验证完整交互、附加字段、离线模型投影、刷新派生和注销恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_OAuthLoginRefreshAndLogoutPreserveCredentialTypes()
    {
        using var fixture = new Fixture("provider-oauth", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "oauth.js"), """
            export default pi=>{
              let refreshes=0;
              pi.registerProvider('oauth-extension',{api:'openai-completions',baseUrl:'https://oauth.invalid',models:[{id:'chat',name:'Public'}],
                oauth:{name:'Extension Login',usesCallbackServer:true,
                  async login(cb){
                    cb.onAuth({url:'https://oauth.invalid/authorize',instructions:'Authorize'});
                    cb.onDeviceCode({userCode:'DEVICE',verificationUri:'https://oauth.invalid/device',intervalSeconds:3,expiresInSeconds:40});
                    cb.onProgress('ready');
                    if(await cb.onPrompt({message:'Code',placeholder:'Enter code',allowEmpty:true})!=='code')throw Error('prompt result');
                    if(await cb.onSelect({message:'Account',options:[{id:'account',label:'Account One'}]})!=='account')throw Error('selection result');
                    if(await cb.onManualCodeInput()!=='manual')throw Error('manual result');
                    return {access:'expired',refresh:'',expires:Date.now()-1000,account:{id:42,roles:['reader']},enabled:true,region:'test'};
                  },
                  async refreshToken(credentials,signal){
                    if(!(signal instanceof AbortSignal)||credentials.account.id!==42||!credentials.enabled||credentials.refresh!=='')throw Error('refresh fields lost');
                    refreshes++;return {...credentials,access:'renewed',expires:Date.now()+3600000};
                  },
                  getApiKey:credentials=>credentials.access+'-'+credentials.account.id,
                  modifyModels:(models,credentials)=>models.map(model=>({...model,name:credentials.access+'-'+credentials.account.id}))
                }
              });
              pi.registerCommand('oauth-check',{handler:async(arg,ctx)=>{
                const result=await ctx.modelRegistry.refresh({providers:['oauth-extension'],allowNetwork:arg==='network'});
                if(result.errors.size)throw [...result.errors.values()][0];
                const model=ctx.modelRegistry.find('oauth-extension','chat');
                if(arg==='offline' && (refreshes!==0||model.name!=='expired-42'))throw Error('offline refreshed token or missing projection');
                if(arg==='network' && (refreshes!==1||model.name!=='renewed-42'))throw Error('network refresh or projection failed');
                if(arg==='network' && await ctx.modelRegistry.getApiKey(model)!=='renewed-42')throw Error('key derivation failed');
              }});
              pi.registerCommand('remove-oauth',{handler:()=>pi.unregisterProvider('oauth-extension')});
            };
            """);
        var catalog = fixture.Catalog();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, ModelCatalog = catalog, NoSession = true, ProviderId = "oauth-extension", ModelId = "chat" });
        Assert.Null(catalog.AuthResolver.GetOAuthProvider("oauth-extension"));
        Assert.True(session.Runner.GetAuthStatus().CanLogin);
        var oauth = Assert.IsAssignableFrom<IOAuthProvider>(session.Runner.GetOAuthProvider("oauth-extension"));
        Assert.Equal("Extension Login", oauth.Name);
        Assert.True(oauth.UsesCallbackServer);
        var callbacks = new ProviderLoginCallbacks();
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, oauthLoginCallbacksFactory: () => callbacks);
        var login = await router.TryHandleAsync("/login oauth-extension").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("authenticated successfully", login!.Message);
        var credentials = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).Load()["oauth-extension"];
        Assert.Equal(["auth", "device", "ready", "prompt", "select", "manual", "cancel-manual"], callbacks.Events);
        Assert.Equal(42, credentials.Properties["account"].GetProperty("id").GetInt32());
        Assert.Equal("expired-42", Assert.Single(session.Runner.GetModels("oauth-extension")).Name);
        await DrainAsync(session.RunAsync("/oauth-check offline"));
        await DrainAsync(session.RunAsync("/oauth-check network"));
        var stored = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).Load()["oauth-extension"];
        Assert.Equal("renewed", stored.Access);
        Assert.Equal(string.Empty, stored.Refresh);
        Assert.True(stored.Properties["enabled"].GetBoolean());
        Assert.Equal("reader", stored.Properties["account"].GetProperty("roles")[0].GetString());
        var logout = await router.TryHandleAsync("/logout oauth-extension");
        Assert.Contains("credentials removed", logout!.Message);
        Assert.Equal("Public", Assert.Single(session.Runner.GetModels("oauth-extension")).Name);
        Assert.False(session.Runner.GetAuthStatus("oauth-extension").IsConfigured);
        await DrainAsync(session.RunAsync("/remove-oauth"));
        Assert.Null(session.Runner.GetOAuthProvider("oauth-extension"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.LoginAsync(callbacks));
    }

    /// <summary>【CodingAgent】【OAuth 取消】登录取消传递真实信号且不持久化凭据，随后仍可在同一工厂实例登录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_OAuthCancellationKeepsRuntimeReusable()
    {
        using var fixture = new Fixture("oauth-cancel", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cancel.js"), """
            export default pi=>{
              let calls=0;
              pi.registerProvider('cancel-oauth',{api:'openai-completions',baseUrl:'https://oauth.invalid',models:[{id:'chat'}],oauth:{name:'Cancel',
                async login(cb){
                  if(++calls===1){
                    cb.onProgress('ready');
                    await new Promise(resolve=>{if(cb.signal.aborted)resolve();else cb.signal.addEventListener('abort',resolve,{once:true});});
                    throw Error('cancelled');
                  }
                  return {access:'reused',refresh:'',expires:Date.now()+3600000};
                },refreshToken:async c=>c,getApiKey:c=>c.access
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "cancel-oauth" });
        var oauth = session.Runner.GetOAuthProvider("cancel-oauth")!;
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var callbacks = new ProviderLoginCallbacks { Progress = source.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oauth.LoginAsync(callbacks, source.Token));
        var credentials = await oauth.LoginAsync(new ProviderLoginCallbacks()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("reused", credentials.Access);
        Assert.Empty(new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).Load());
    }

    /// <summary>【CodingAgent】【OAuth 交互回归】记录六类登录交互与手动输入清理。</summary>
    private sealed class ProviderLoginCallbacks : IOAuthLoginCallbacks, IOAuthManualCodeInputController
    {
        public List<string> Events { get; } = [];
        public Action? Progress { get; init; }
        /// <summary>记录授权地址及说明。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) { Assert.Equal("https://oauth.invalid/authorize", url); Assert.Equal("Authorize", instructions); Events.Add("auth"); }
        /// <summary>记录设备码及轮询参数。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">有效期。</param>
        public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null)
        { Assert.Equal("DEVICE", userCode); Assert.Equal("https://oauth.invalid/device", verificationUri); Assert.Equal(3, intervalSeconds); Assert.Equal(40, expiresInSeconds); Events.Add("device"); }
        /// <summary>返回测试验证码。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">允许空值。</param><returns>验证码。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false)
        { Assert.Equal("Code", message); Assert.Equal("Enter code", placeholder); Assert.True(allowEmpty); Events.Add("prompt"); return Task.FromResult("code"); }
        /// <summary>记录进度并执行可选取消。</summary><param name="message">进度。</param>
        public void OnProgress(string message) { Events.Add(message); Progress?.Invoke(); }
        /// <summary>返回手动代码。</summary><returns>输入代码。</returns>
        public Task<string>? OnManualCodeInputAsync() { Events.Add("manual"); return Task.FromResult("manual"); }
        /// <summary>返回选中账户。</summary><param name="message">提示。</param><param name="options">候选。</param><returns>账户 ID。</returns>
        public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options)
        { Assert.Equal("Account", message); Assert.Equal(new("account", "Account One"), Assert.Single(options)); Events.Add("select"); return Task.FromResult<string?>("account"); }
        /// <summary>记录手动输入资源清理。</summary>
        public void CancelManualCodeInput() => Events.Add("cancel-manual");
    }
}
