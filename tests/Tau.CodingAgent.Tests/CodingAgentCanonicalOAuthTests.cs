// 作者：xxx
using System.Net;
using System.Net.Sockets;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【原生 OAuth】登录提示独立取消、秘密输入、刷新和完整认证派生最终送入真实 HTTP 请求。</summary>
    /// <param name="headersOnly">是否只通过请求头提供认证。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_CanonicalOAuthUsesCredentialEndpointAndHeaders(bool headersOnly)
    {
        using var fixture = new Fixture("native-oauth", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.js"), $$$$"""
            export default pi=>pi.registerProvider({id:'native-oauth',name:'Native',
              getModels:()=>[{id:'chat',api:'openai-completions',baseUrl:'http://127.0.0.1:1',provider:'native-oauth',name:'Native Chat',input:['text']}],
              headers:{'X-Precedence':'configured'},auth:{oauth:{name:'Native Login',isSubscription:true,loginLabel:'Choose native account',
                async login(interaction){
                  interaction.notify({type:'info',message:'Native instructions',links:[{label:'Docs',url:'https://oauth.invalid/docs'}]});
                  const promptAbort=new AbortController();
                  const pending=interaction.prompt({type:'text',message:'Waiting',signal:promptAbort.signal});
                  const secret=await interaction.prompt({type:'secret',message:'Secret',placeholder:'hidden'});
                  promptAbort.abort();
                  let cancelled=false;try{await pending;}catch{cancelled=true;}
                  if(!cancelled||interaction.signal.aborted)throw Error('per-prompt cancellation failed');
                  return {type:'oauth',access:secret,refresh:'rotation',expires:Date.now()-1000,endpoint:'{{{{endpoint}}}}'};
                },
                async refresh(credential,signal){
                  if(!(signal instanceof AbortSignal)||credential.type!=='oauth'||credential.access!=='secret-value')throw Error('native refresh contract');
                  return {...credential,access:'native-token',expires:Date.now()+3600000};
                },
                async toAuth(credential){return {apiKey:{{{{(headersOnly ? "undefined" : "credential.access")}}}},
                  headers:{Authorization:'Bearer '+credential.access,'X-Account':'native','X-Precedence':'auth'},baseUrl:credential.endpoint};}
              }}});
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "native-oauth", ModelId = "chat" }, deadline.Token);
        var callbacks = new NativeLoginCallbacks();
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, oauthLoginCallbacksFactory: () => callbacks);
        var oauth = session.Runner.GetOAuthProvider("native-oauth")!;
        Assert.True(oauth.IsSubscription);
        Assert.Equal("Choose native account", oauth.LoginLabel);
        var login = await router.TryHandleAsync("/login native-oauth", deadline.Token);
        Assert.Contains("authenticated successfully", login!.Message);
        Assert.True(callbacks.PromptCancelled);
        Assert.Contains("Native instructions", callbacks.Info);
        Assert.Contains("https://oauth.invalid/docs", callbacks.Info);
        var served = ServeChatAsync(server, deadline.Token);
        await DrainAsync(session.RunAsync("test native oauth request", deadline.Token));
        var headers = await served;
        Assert.Contains("Authorization: Bearer native-token", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Account: native", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Precedence: configured", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("native-token", new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).Load()["native-oauth"].Access);
    }

    /// <summary>【CodingAgent】【原生交互测试】验证提示信号和秘密输入类型不丢失。</summary>
    private sealed class NativeLoginCallbacks : IOAuthLoginCallbacks
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PromptCancelled { get; private set; }
        public string Info { get; private set; } = string.Empty;
        /// <summary>此场景不请求授权链接。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Unexpected auth link.");
        /// <summary>此场景必须使用带取消信号的重载。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值选项。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("Missing prompt signal.");
        /// <summary>等待单独的提示取消信号。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值选项。</param><param name="token">信号。</param><returns>未完成输入。</returns>
        public async Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token)
        {
            _started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { PromptCancelled = true; throw; }
            return string.Empty;
        }
        /// <summary>确认秘密输入走独立回调，再返回测试值。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">取消信号。</param><returns>测试秘密值。</returns>
        public async Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token)
        { Assert.Equal("Secret", message); Assert.Equal("hidden", placeholder); await _started.Task.WaitAsync(token); return "secret-value"; }
        /// <summary>记录包含链接的登录说明。</summary><param name="message">说明。</param>
        public void OnProgress(string message) => Info = message;
        /// <summary>原生提示通过带类型输入回调处理。</summary><returns>没有后台手动输入。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
