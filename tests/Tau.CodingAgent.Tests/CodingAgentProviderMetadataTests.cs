// 作者：xxx
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【提供方事务】失败工厂恢复它删除或覆盖的其他提供方，拒绝无效运行中注册后仍可继续。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_FailedFactoriesRollbackRegistrationAndRemoval()
    {
        using var fixture = new Fixture("provider-transaction", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a-good.js"), """
            export default pi=>{
              pi.registerProvider('good',{api:'openai-completions',baseUrl:'https://good.invalid',models:[{id:'good'}]});
              pi.registerCommand('validate',{handler:()=>{
                let caught=false;
                try{pi.registerProvider('bad',{models:[{id:'broken'}]});}catch{caught=true;}
                if(!caught)throw Error('registration was not validated synchronously');
                pi.registerProvider('after',{api:'openai-completions',baseUrl:'https://after.invalid',models:[{id:'after'}]});
              }});
            };
            """);
        File.WriteAllText(Path.Combine(directory, "b-fail.js"), """
            export default pi=>{
              pi.unregisterProvider('good');
              pi.registerProvider('partial',{api:'openai-completions',baseUrl:'https://partial.invalid',models:[{id:'partial'}]});
              throw Error('expected factory failure');
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Single(session.Runner.GetModels("good"));
        Assert.Empty(session.Runner.GetModels("partial"));
        Assert.Contains(session.ExtensionStatus.Diagnostics, diagnostic => diagnostic.Message.Contains("expected factory failure", StringComparison.Ordinal));
        await DrainAsync(session.RunAsync("/validate"));
        Assert.Empty(session.Runner.GetModels("bad"));
        Assert.Single(session.Runner.GetModels("after"));
        Assert.Single(session.Runner.GetModels("good"));
    }

    /// <summary>【CodingAgent】【模型元数据】原生费用、输入限制和缓存寿命完整传入扩展，并可持久化模型快照。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_PreservesNativeMetadataAndCostFieldNames()
    {
        using var fixture = new Fixture("provider-metadata", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "metadata.js"), """
            export default pi=>{
              pi.registerProvider('metadata',{api:'openai-completions',baseUrl:'https://metadata.invalid',apiKey:'test',models:[{
                id:'metadata',input:['text','image'],contextWindow:32768,maxTokens:4096,reasoning:true,
                inputLimits:{maxRequestBytes:9000000,images:{maxPerMessage:4,maxPerRequest:12,resize:{maxWidth:1200,maxHeight:800,maxBytes:1000000,jpegQuality:85}}},
                promptCache:{short:300,long:3600},thinkingLevelMap:{low:'small',xhigh:null},
                cost:{input:1,output:2,cacheRead:0.1,cacheWrite:0.2,tiers:[{inputTokensAbove:100000,input:3,output:4,cacheRead:0.3,cacheWrite:0.4}]}
              }]});
              pi.on('session_start',(_,ctx)=>{
                const m=ctx.modelRegistry.find('metadata','metadata');
                if(m.api!=='openai-completions'||m.maxTokens!==4096||m.cost.input!==1||m.cost.tiers[0].output!==4)throw Error('native fields lost');
                if(m.inputLimits.images.resize.jpegQuality!==85||m.promptCache.long!==3600)throw Error('metadata lost');
              });
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "metadata" });
        Assert.Empty(session.StartupExtensionErrors);
        var model = session.Runner.Model;
        Assert.Equal(9000000, model.InputLimits?.MaxRequestBytes);
        Assert.Equal(1200, model.InputLimits?.Images?.Resize?.MaxWidth);
        Assert.Equal(300, model.PromptCache?.Short);
        var store = new Tau.Ai.Registry.InMemoryModelsStore();
        await store.WriteAsync("metadata", new ModelsStoreEntry([model]));
        var restored = Assert.Single((await store.ReadAsync("metadata"))!.Models);
        Assert.Equal(model.InputLimits, restored.InputLimits);
        Assert.Equal(model.PromptCache, restored.PromptCache);
    }
}
