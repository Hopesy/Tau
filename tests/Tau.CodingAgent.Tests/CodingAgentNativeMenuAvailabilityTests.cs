// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【原生菜单】菜单按原生聊天规则过滤，不调用跨能力规则或刷新已过期的令牌。</summary>
    /// <param name="ids">当前账户允许的精确 ID。</param><param name="expected">期望保留的模型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("[\"yes\"]", "yes")]
    [InlineData("[\"no\",\"yes\"]", "yes,no")]
    [InlineData("[]", "")]
    [InlineData("[\"YES\"]", "")]
    public async Task NativeMenuAvailability_UsesChatFilter(string ids, string expected)
    {
        using var fixture = new Fixture("native-menu", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateNativeMenuOptions(fixture), deadline.Token);
        session.Runner.SaveOAuthCredentials("native-menu", NativeMenuCredential(ids));
        var all = session.Runner.GetModels("native-menu");
        Assert.Equal(["yes", "no"], all.Select(model => model.Id));
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','),
            CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner).Where(model => model.Provider == "native-menu").Select(model => model.Id));
        await DrainAsync(session.RunAsync("/native-menu-check", deadline.Token));
    }

    /// <summary>【CodingAgent】【实时凭据】每次宿主过滤读取当前存储，API key 和注销后不再使用之前的 OAuth 权限。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NativeMenuAvailability_ReadsFreshCredentials()
    {
        using var fixture = new Fixture("native-menu-change", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateNativeMenuOptions(fixture), deadline.Token);
        var all = session.Runner.GetModels("native-menu");
        session.Runner.SaveOAuthCredentials("native-menu", NativeMenuCredential("[\"yes\"]"));
        Assert.Equal(["yes"], session.Runner.FilterAvailableModels("native-menu", all).Select(model => model.Id));
        session.Runner.SaveOAuthCredentials("native-menu", NativeMenuCredential("[\"no\"]"));
        Assert.Equal(["no"], session.Runner.FilterAvailableModels("native-menu", all).Select(model => model.Id));
        session.Runner.SaveApiKeyCredential("native-menu", new ApiKeyCredential("yes"));
        Assert.Equal(["yes"], session.Runner.FilterAvailableModels("native-menu", all).Select(model => model.Id));
        Assert.True(session.Runner.Logout("native-menu"));
        Assert.Empty(session.Runner.FilterAvailableModels("native-menu", all));
        Assert.DoesNotContain(CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner), model => model.Provider == "native-menu");
    }

    /// <summary>【CodingAgent】【菜单子集】原生规则收到完整目录，宿主保留交错顺序和重复项。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NativeMenuAvailability_PreservesSubsetAndFilterContext()
    {
        using var fixture = new Fixture("native-menu-subset", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateNativeMenuOptions(fixture), deadline.Token);
        session.Runner.SaveOAuthCredentials("native-menu", NativeMenuCredential("[\"yes\"]"));
        await DrainAsync(session.RunAsync("/native-menu-mode first", deadline.Token));
        var all = session.Runner.GetModels("native-menu");
        var subset = new[] { all[1], session.Runner.Model, all[0], all[0], session.Runner.Model };
        Assert.Equal([session.Runner.Model, all[0], all[0], session.Runner.Model], CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner, subset));
    }

    /// <summary>【CodingAgent】【过滤错误】原生异常不能静默开放目录，修改、覆盖和注销提供方后采用当前规则。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NativeMenuAvailability_TracksProviderChangesAndErrors()
    {
        using var fixture = new Fixture("native-menu-registration", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateNativeMenuOptions(fixture), deadline.Token);
        session.Runner.SaveOAuthCredentials("native-menu", NativeMenuCredential("[\"yes\"]"));
        var all = session.Runner.GetModels("native-menu");
        await DrainAsync(session.RunAsync("/native-menu-mode error", deadline.Token));
        Assert.Contains("menu filter failure", Assert.Throws<InvalidOperationException>(() => session.Runner.FilterAvailableModels("native-menu", all)).Message);
        await DrainAsync(session.RunAsync("/native-menu-mode replace", deadline.Token));
        Assert.Equal(["no"], session.Runner.FilterAvailableModels("native-menu", all).Select(model => model.Id));
        await DrainAsync(session.RunAsync("/native-menu-mode unfiltered", deadline.Token));
        Assert.Equal(all, session.Runner.FilterAvailableModels("native-menu", all));
        await DrainAsync(session.RunAsync("/native-menu-mode unregister", deadline.Token));
        Assert.Empty(session.Runner.GetModels("native-menu"));
        Assert.Equal(all, session.Runner.FilterAvailableModels("native-menu", all));
    }

    /// <summary>【CodingAgent】【菜单夹具】创建纯内存原生扩展，认证和跨能力回调禁止被聊天菜单触发。</summary>
    /// <param name="fixture">隔离文件目录。</param><returns>SDK 会话选项。</returns>
    private static CodingAgentSdkCreateSessionOptions CreateNativeMenuOptions(Fixture fixture)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native-menu.js"), """
            export default pi=>{
              let mode='credential';
              const models=['yes','no'].map(id=>({id,provider:'native-menu',api:'custom',baseUrl:'http://unit.invalid'}));
              const provider={id:'native-menu',getModels:()=>models,auth:{oauth:{name:'fixture',
                login:async()=>{throw Error('unexpected login');},refresh:async()=>{throw Error('unexpected refresh');},
                toAuth:async()=>{throw Error('unexpected resolve');}}},
                filterModels:(items,credential)=>{
                  if(mode==='error')throw Error('menu filter failure');
                  if(mode==='first')return items.slice(0,1);
                  const ids=credential?.type==='api_key'?[credential.key]:credential?.availableModelIds??[];
                  return items.filter(model=>ids.includes(model.id));
                },filterAllModels:()=>{throw Error('chat menu called all-model filter');}};
              pi.registerProvider(provider);
              pi.registerCommand('native-menu-mode',{handler:async(value,ctx)=>{
                mode=value;
                if(value==='replace')ctx.modelRegistry.registerProvider({...provider,filterModels:items=>items.filter(model=>model.id==='no')});
                if(value==='unfiltered')ctx.modelRegistry.registerProvider({...provider,filterModels:undefined});
                if(value==='unregister')ctx.modelRegistry.unregisterProvider(provider.id);
              }});
              pi.registerCommand('native-menu-check',{handler:async(_,ctx)=>{
                if(ctx.modelRegistry.getError())throw Error(ctx.modelRegistry.getError());
              }});
            };
            """);
        return new() { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" };
    }

    /// <summary>【CodingAgent】【合成凭据】生成已过期但保留原生权限字段的离线测试凭据。</summary>
    /// <param name="ids">模型 ID 数组 JSON。</param><returns>合成 OAuth 凭据。</returns>
    private static OAuthCredentials NativeMenuCredential(string ids)
    {
        using var document = JsonDocument.Parse(ids);
        return new() { Access = "synthetic-menu-access", Refresh = "synthetic-menu-refresh", ExpiresAt = DateTimeOffset.UnixEpoch,
            Properties = new Dictionary<string, JsonElement> { ["availableModelIds"] = document.RootElement.Clone() } };
    }
}
