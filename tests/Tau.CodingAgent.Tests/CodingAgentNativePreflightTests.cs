// 作者：xxx
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【动态认证】初始快照未配置时，提示预检实时发现凭据，并保留 check 与 resolve 回退语义。</summary>
    /// <param name="hasCheck">是否声明独立检查回调。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativePromptPreflight_RechecksPreviouslyUnavailableCredentials(bool hasCheck)
    {
        using var fixture = new Fixture("preflight-native", "models");
        WriteBoundaryExtension(fixture, "auth.js", """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let ready=false,starts=0;
              const auth={name:'Native test',resolve:async()=>ready?{auth:{apiKey:'synthetic'}}:undefined};
              if(__CHECK__)auth.check=async()=>ready?{source:'runtime',type:'api_key'}:undefined;
              pi.registerProvider({id:'preflight-native',name:'Native',auth:{apiKey:auth},
                getModels:()=>[{id:'chat',provider:'preflight-native',api:'native-preflight',baseUrl:'http://127.0.0.1:1'}],
                streamSimple(model,context,options){
                  if(options.apiKey!=='synthetic')throw Error('request credential missing');
                  const stream=createAssistantMessageEventStream();
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,timestamp:Date.now(),
                    stopReason:'stop',content:[{type:'text',text:'accepted'}],
                    usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                  stream.push({type:'done',reason:'stop',message});return stream;
                }});
              pi.on('before_agent_start',()=>{starts++;});
              pi.registerCommand('enable',{handler:()=>{if(starts!==0)throw Error('preflight started agent');ready=true;}});
            };
            """.Replace("__CHECK__", hasCheck ? "true" : "false", StringComparison.Ordinal));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "preflight-native" });
        Assert.False(session.Runner.GetAuthStatus().IsConfigured);
        await Assert.ThrowsAsync<ProviderAuthException>(() => DrainAsync(session.RunAsync("unavailable")));
        Assert.Empty(session.Messages.OfType<UserMessage>());
        await foreach (var evt in session.RunAsync("/enable")) Assert.IsNotType<CodingAgentExtensionErrorEvent>(evt);
        Assert.False(session.Runner.GetAuthStatus().IsConfigured);
        await DrainAsync(session.RunAsync("now available"));
        Assert.Equal("accepted", Assert.IsType<TextContent>(Assert.Single(Assert.Single(session.Messages.OfType<AssistantMessage>()).Content)).Text);
        Assert.Single(session.Messages.OfType<UserMessage>());
    }

    /// <summary>【CodingAgent】【认证取消】原生异步检查收到运行取消，不发出 started，后续命令仍可使用同一会话。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task NativePromptPreflight_CancellationStopsCheckBeforeAgentStarts()
    {
        using var fixture = new Fixture("preflight-cancel", "models");
        var marker = Path.Combine(fixture.Root, "checking.txt");
        WriteBoundaryExtension(fixture, "auth.js", """
            import fs from 'node:fs';
            export default pi=>{
              let blocking=false,cancelled=false;
              pi.registerProvider({id:'preflight-cancel',name:'Native',
                getModels:()=>[{id:'chat',provider:'preflight-cancel',api:'openai-completions',baseUrl:'http://127.0.0.1:1'}],
                auth:{apiKey:{name:'Test',resolve:async()=>undefined,check:async({signal})=>{
                  if(!blocking)return undefined;
                  fs.writeFileSync(__MARKER__,'entered');
                  await new Promise((resolve,reject)=>signal.addEventListener('abort',()=>{cancelled=true;reject(Error('cancelled'));},{once:true}));
                }}}});
              pi.registerCommand('block',{handler:()=>{blocking=true;}});
              pi.registerCommand('verify',{handler:()=>{if(!cancelled)throw Error('check not cancelled');}});
            };
            """.Replace("__MARKER__", JsonValue.Create(marker)!.ToJsonString(), StringComparison.Ordinal));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "preflight-cancel" });
        await DrainAsync(session.RunAsync("/block"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispositions = new List<CodingAgentPromptDisposition>();
        var run = DrainAsync(session.Runner.RunWithDispositionAsync([new TextContent("wait")], disposition =>
        { dispositions.Add(disposition); return Task.CompletedTask; }, cancellationToken: cancellation.Token));
        while (!File.Exists(marker)) await Task.Delay(10, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(dispositions); Assert.Empty(session.Messages.OfType<UserMessage>());
        await foreach (var evt in session.RunAsync("/verify")) Assert.IsNotType<CodingAgentExtensionErrorEvent>(evt);
    }
}
