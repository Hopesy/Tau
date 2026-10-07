// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【目录关闭】命令启动未等待的刷新后立即关闭会话，宿主任务必须退出并释放凭据文件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_DisposeDrainsDetachedModelRefresh()
    {
        using var fixture = new Fixture("registry-dispose", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dispose.js"), """
            export default pi=>{
              let started;
              pi.registerProvider('dispose-refresh',{api:'openai-completions',baseUrl:'http://127.0.0.1:1',apiKey:'test',models:[{id:'chat'}],
                async refreshModels(ctx){
                  if(ctx.allowNetwork){
                    started();
                    await new Promise(resolve=>ctx.signal.addEventListener('abort',resolve,{once:true}));
                  }
                  return [{id:'chat'}];
                }
              });
              pi.registerCommand('background-refresh',{handler:async(_,ctx)=>{
                const ready=new Promise(resolve=>{started=resolve;});
                void ctx.modelRegistry.refresh({allowNetwork:true}).catch(()=>{});
                await ready;
              }});
            };
            """);
        var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "dispose-refresh" });
        try
        {
            await DrainAsync(session.RunAsync("/background-refresh"));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var lease = new FileStream(Path.Combine(fixture.AgentDirectory, "auth.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { await session.DisposeAsync(); }
    }

    /// <summary>【CodingAgent】【目录认证】原生认证保留请求来源和可选字段，失败返回实际原因，注册查询保留对象身份。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_ModelRegistryNativeAuthPreservesSourceAndErrors()
    {
        using var fixture = new Fixture("registry-native-auth", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.js"), """
            export default pi=>{
              const provider={id:'headers-only',name:'Headers Only',
                getModels:()=>[{id:'chat',provider:'headers-only',api:'openai-completions',baseUrl:'http://127.0.0.1:1'}],
                auth:{apiKey:{name:'Headers',check:async()=>({type:'api_key',source:'check source'}),
                  resolve:async()=>({auth:{headers:{'X-Auth':'provided'}},source:'request source'})}}};
              pi.registerProvider(provider);
              pi.registerProvider({id:'broken-auth',
                getModels:()=>[{id:'chat',provider:'broken-auth',api:'openai-completions',baseUrl:'http://127.0.0.1:1'}],
                auth:{apiKey:{name:'Broken',check:async()=>undefined,resolve:async()=>{throw Error('exact failure reason');}}}});
              pi.registerCommand('native-auth',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry;
                if(registry.getRegisteredNativeProvider('headers-only')!==provider||registry.getRegisteredProviderConfig('headers-only'))throw Error('native instance lost');
                const status=registry.getProviderAuthStatus('headers-only');
                if(status.label!=='check source'||status.source!=='environment')throw Error('status source mismatch');
                const value=await registry.getProviderAuth('headers-only');
                if(value.source!=='request source'||value.auth.apiKey!==undefined||value.auth.headers['X-Auth']!=='provided')throw Error('request source/optional fields mismatch');
                const missing=await registry.getApiKeyAndHeaders(registry.find('broken-auth','chat'));
                if(missing.ok||missing.error!=='exact failure reason')throw Error('cause was hidden');
                if(await registry.getApiKeyForProvider('broken-auth')!==undefined)throw Error('error fallback');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "headers-only" });
        await DrainAsync(session.RunAsync("/native-auth"));
    }

    /// <summary>【CodingAgent】【目录认证】完整请求认证、显示状态、原始注册和增量重注册与宿主目录一致。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_ModelRegistryAuthAndIncrementalRegistrationMatchRequests()
    {
        using var fixture = new Fixture("registry-auth", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "registry.js"), """
            export default pi=>{
              pi.registerProvider('registry-auth',{name:'Registry Auth',api:'openai-completions',baseUrl:'http://127.0.0.1:1',
                apiKey:'initial-key',headers:{'X-Provider':'provider'},models:[{id:'chat',headers:{'X-Model':'model'}}]});
              pi.registerCommand('registry-auth',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry,model=registry.find('registry-auth','chat');
                if(registry.getProviderDisplayName('registry-auth')!=='Registry Auth'||registry.isUsingOAuth(model))throw Error('display/auth metadata');
                const status=registry.getProviderAuthStatus('registry-auth');
                if(!status.configured||status.source!=='models_json_key'||JSON.stringify(status).includes('initial-key'))throw Error('bad nonsecret status');
                const auth=await registry.getApiKeyAndHeaders(model);
                if(!auth.ok||auth.apiKey!=='initial-key'||auth.headers['X-Model']!=='model'||auth.headers['X-Provider']!=='provider')throw Error('request auth');
                const provider=await registry.getProviderAuth('registry-auth');
                if(provider.auth.apiKey!=='initial-key'||provider.auth.headers['X-Model'])throw Error('provider scope');
                registry.registerProvider('registry-auth',{apiKey:'updated-key',name:undefined});
                if(!registry.find('registry-auth','chat')||registry.getProviderDisplayName('registry-auth')!=='Registry Auth')throw Error('incremental registration lost model');
                const config=registry.getRegisteredProviderConfig('registry-auth');
                if(config.apiKey!=='updated-key'||config.models[0].id!=='chat'||registry.getRegisteredNativeProvider('registry-auth'))throw Error('registered config');
                if(await registry.getApiKeyForProvider('registry-auth')!=='updated-key')throw Error('stale request key');
                registry.unregisterProvider('registry-auth');
                if(registry.find('registry-auth','chat')||registry.getRegisteredProviderConfig('registry-auth'))throw Error('unregister failed');
                if(await registry.getApiKeyForProvider('registry-auth')!==undefined)throw Error('removed provider key');
              }});
              pi.registerProvider('required-auth',{api:'openai-completions',baseUrl:'http://127.0.0.1:1',authHeader:true,models:[{id:'chat'}]});
              pi.registerCommand('required-auth',{handler:async(_,ctx)=>{
                const auth=await ctx.modelRegistry.getApiKeyAndHeaders(ctx.modelRegistry.find('required-auth','chat'));
                if(auth.ok||!auth.error.includes('No API key found'))throw Error('missing required auth error');
                if(await ctx.modelRegistry.getApiKeyForProvider('required-auth')!==undefined)throw Error('provider key helper must absorb auth errors');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "registry-auth" });
        await DrainAsync(session.RunAsync("/required-auth"));
        await DrainAsync(session.RunAsync("/registry-auth"));
        Assert.Empty(session.Runner.GetModels("registry-auth"));
    }
}
