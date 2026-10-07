// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【提供方名称】扩展即时注册、重命名和注销在宿主及 Node 查询中使用同一名称优先级。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ProviderNames_TrackRuntimeRegistrationAndRestoreBuiltInNames()
    {
        using var fixture = new Fixture("provider-names", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "names.js"), """
            export default pi=>{
              pi.registerProvider('openai',{name:'Custom OpenAI'});
              pi.registerProvider('custom-name',{name:'Custom Name',api:'openai-completions',baseUrl:'https://custom.invalid',models:[{id:'chat'}]});
              pi.registerCommand('rename-providers',{handler:(_,ctx)=>{
                const registry=ctx.modelRegistry;
                if(registry.getProviderDisplayName('openai')!=='Custom OpenAI')throw Error('registered name lost');
                if(registry.getProviderDisplayName('anthropic')!=='Anthropic')throw Error('auth method replaced provider name');
                if(registry.getProviderDisplayName('google')!=='Google')throw Error('stale builtin name');
                registry.registerProvider('openai',{name:'Renamed OpenAI'});
                registry.registerProvider('custom-name',{name:'Renamed Custom'});
                if(registry.getProviderDisplayName('openai')!=='Renamed OpenAI'||registry.getProviderDisplayName('custom-name')!=='Renamed Custom')throw Error('rename not immediate');
              }});
              pi.registerCommand('remove-providers',{handler:(_,ctx)=>{
                const registry=ctx.modelRegistry;
                registry.unregisterProvider('openai'); registry.unregisterProvider('custom-name');
                if(registry.getProviderDisplayName('openai')!=='OpenAI')throw Error('builtin name not restored');
                if(registry.getProviderDisplayName('custom-name')!=='custom-name')throw Error('stale custom name');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        Assert.Equal("Custom OpenAI", session.Runner.GetProviderDisplayName("openai"));
        Assert.Equal("Custom Name", session.Runner.GetProviderDisplayName("custom-name"));
        await DrainAsync(session.RunAsync("/rename-providers"));
        Assert.Equal("Renamed OpenAI", session.Runner.GetProviderDisplayName("openai"));
        Assert.Equal("Renamed Custom", session.Runner.GetProviderDisplayName("custom-name"));
        var router = new CodingAgentCommandRouter(session.Runner, authSelector: (state, _) =>
        {
            Assert.Equal("auth", state.Mode);
            var option = Assert.Single(state.Options, option => option.Provider == "custom-name");
            Assert.Equal("Renamed Custom", option.Name);
            return Task.FromResult<string?>(option.SelectionKey);
        });
        var auth = await router.TryHandleAsync("/auth select");
        Assert.False(auth!.IsError, auth.Message);
        Assert.Contains("auth custom-name:", auth.Message);
        await DrainAsync(session.RunAsync("/remove-providers"));
        Assert.Equal("OpenAI", session.Runner.GetProviderDisplayName("openai"));
        Assert.Equal("custom-name", session.Runner.GetProviderDisplayName("custom-name"));
    }
}
