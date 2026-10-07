// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【API key 登录】取消不写入，随后登录持久化密钥和环境，状态刷新与注销立即生效。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_CanonicalApiKeyLoginPersistsEnvironmentAndHandlesCancellation()
    {
        using var fixture = new Fixture("native-key-login", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "login.js"), """
            export default pi=>pi.registerProvider({id:'key-login',name:'Key Login',
              getModels:()=>[{id:'chat',provider:'key-login',api:'openai-completions',baseUrl:'http://127.0.0.1:1'}],
              auth:{apiKey:{name:'Test key',
                async login(interaction){
                  const key=await interaction.prompt({type:'secret',message:'API key',placeholder:'hidden'});
                  return {type:'api_key',key,env:{ACCOUNT:'account-one'}};
                },
                check:async({credential})=>credential?.key&&credential.env?.ACCOUNT?{type:'api_key',source:'Saved login'}:undefined,
                resolve:async({credential})=>credential?{auth:{apiKey:credential.key},env:credential.env,source:'Saved login'}:undefined
              }}
            });
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "key-login" });
        Assert.True(session.Runner.GetAuthStatus().CanLogin);
        Assert.False(session.Runner.GetAuthStatus().IsConfigured);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var callbacks = new ApiKeyLoginCallbacks { Cancel = cancellation.Cancel };
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, oauthLoginCallbacksFactory: () => callbacks);
        var authPath = Path.Combine(fixture.AgentDirectory, "auth.json");
        var before = File.ReadAllText(authPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TryHandleAsync("/login key-login", cancellation.Token));
        Assert.Equal(before, File.ReadAllText(authPath));
        callbacks.Cancel = null;
        var login = await router.TryHandleAsync("/login key-login").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("authenticated successfully", login!.Message);
        Assert.DoesNotContain("secret-key", login.Message);
        Assert.True(session.Runner.GetAuthStatus().IsConfigured);
        Assert.Equal("Saved login", session.Runner.GetAuthStatus().Source);
        var stored = new OAuthCredentialStore([authPath]).LoadEntries()["key-login"];
        Assert.Equal("secret-key", stored.ApiKey);
        Assert.Equal("account-one", stored.Env!["ACCOUNT"]);
        var logout = await router.TryHandleAsync("/logout key-login");
        Assert.Contains("credentials removed", logout!.Message);
        Assert.False(session.Runner.GetAuthStatus().IsConfigured);
        Assert.False(new OAuthCredentialStore([authPath]).LoadEntries().ContainsKey("key-login"));
    }

    /// <summary>【CodingAgent】【原生 API key】验证无密钥环境凭据、认证上下文、状态检查隔离及真实请求认证。</summary>
    /// <param name="hasCheck">是否提供独立的无副作用检查。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtensionProvider_CanonicalApiKeySeparatesCheckFromRequestResolution(bool hasCheck)
    {
        using var fixture = new Fixture("native-key", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(fixture.AgentDirectory, "auth.json"), new JsonObject
        {
            ["native-key"] = new JsonObject { ["type"] = "api_key", ["env"] = new JsonObject { ["ENDPOINT"] = endpoint, ["TENANT"] = "one" } }
        }.ToJsonString());
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "key.js"), $$$$"""
            export default pi=>{
              let checks=0,resolves=0;
              process.env.TAU_NATIVE_TEST_KEY='ambient-token';
              const key=async input=>{
                if(!(input.signal instanceof AbortSignal)||input.credential?.type!=='api_key'||input.credential.env.TENANT!=='one')throw Error('credential context lost');
                if(!await input.ctx.fileExists('~')||await input.ctx.fileExists('~/.tau-nonexistent-'+process.pid))throw Error('fileExists contract');
                return input.credential.key ?? await input.ctx.env('TAU_NATIVE_TEST_KEY');
              };
              const auth={name:'Native key',resolve:async input=>{
                resolves++;const value=await key(input);
                if(!value)return undefined;
                return {auth:{apiKey:value,headers:{'X-Tenant':input.credential.env.TENANT},baseUrl:input.credential.env.ENDPOINT},
                  env:{TENANT:input.credential.env.TENANT},source:'native fixture'};
              }};
              if({{{{(hasCheck ? "true" : "false")}}}})auth.check=async input=>{
                checks++;return await key(input)?{type:'api_key',source:'native check'}:undefined;
              };
              pi.registerProvider({id:'native-key',name:'Native',auth:{apiKey:auth},
                getModels:()=>[{id:'chat',provider:'native-key',api:'openai-completions',baseUrl:'http://127.0.0.1:1',name:'Native',input:['text']}]
              });
              pi.registerCommand('counts',{handler:async arg=>{
                const expected=Number(arg)+{{{{(hasCheck ? 0 : 1)}}}};
                if(resolves!==expected || checks!=={{{{(hasCheck ? 1 : 0)}}}})throw Error('unexpected side effects '+checks+'/'+resolves);
              }});
              pi.registerCommand('remove-native-key',{handler:async(_,ctx)=>{
                delete process.env.TAU_NATIVE_TEST_KEY;
                const result=await ctx.modelRegistry.refresh({providers:['native-key'],allowNetwork:false});
                if(result.errors.size)throw [...result.errors.values()][0];
                if(ctx.modelRegistry.hasConfiguredAuth({provider:'native-key'}))throw Error('stale auth status');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "native-key" }, deadline.Token);
        for (var index = 0; index < 25; index++) Assert.True(session.Runner.GetAuthStatus().IsConfigured);
        Assert.Equal(hasCheck ? "native check" : "native fixture", session.Runner.GetAuthStatus().Source);
        await DrainAsync(session.RunAsync("/counts 0", deadline.Token));
        var served = ServeChatAsync(server, deadline.Token);
        await DrainAsync(session.RunAsync("test native key request", deadline.Token));
        var headers = await served;
        Assert.Contains("Authorization: Bearer ambient-token", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Tenant: one", headers, StringComparison.OrdinalIgnoreCase);
        await DrainAsync(session.RunAsync("/counts 1", deadline.Token));
        await DrainAsync(session.RunAsync("/remove-native-key", deadline.Token));
        Assert.False(session.Runner.GetAuthStatus().IsConfigured);
    }

    /// <summary>【CodingAgent】【API key 输入】只接受秘密输入，验证整个登录的取消信号。</summary>
    private sealed class ApiKeyLoginCallbacks : IOAuthLoginCallbacks
    {
        public Action? Cancel { get; set; }
        /// <summary>本测试不使用浏览器。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Unexpected auth link.");
        /// <summary>API key 不能作为普通文本输入。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值选项。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("API key must use secret input.");
        /// <summary>首次取消、后续返回测试密钥。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">取消信号。</param><returns>测试密钥。</returns>
        public async Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token)
        {
            Assert.Equal("API key", message);
            if (Cancel is { } cancel) { cancel(); await Task.Delay(Timeout.Infinite, token); }
            return "secret-key";
        }
        /// <summary>接收进度。</summary><param name="message">进度。</param>
        public void OnProgress(string message) { }
        /// <summary>没有手动代码输入。</summary><returns>空值。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
