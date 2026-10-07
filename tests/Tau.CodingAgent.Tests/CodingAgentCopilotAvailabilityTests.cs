// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【Copilot 权限】真实扩展进程、宿主模型菜单及跨能力查询共享离线账户清单。</summary>
    /// <param name="ids">保存的原生清单，空引用表示旧凭据没有该字段。</param><param name="mode">期望的聊天目录模式。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("[\"fixture-allowed\",\"fixture-allowed\"]", "restricted")]
    [InlineData("[]", "empty")]
    [InlineData("[\"FIXTURE-ALLOWED\"]", "empty")]
    [InlineData(null, "all")]
    [InlineData("null", "all")]
    [InlineData("\"fixture-allowed\"", "all")]
    [InlineData("[\"fixture-allowed\",7]", "all")]
    public async Task CopilotAvailability_FiltersHostAndExtensionQueries(string? ids, string mode)
    {
        using var fixture = new Fixture("copilot-access", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = CreateCopilotAvailabilityOptions(fixture);
        await using var session = await CodingAgentSdk.CreateSessionAsync(options, deadline.Token);
        session.Runner.SaveOAuthCredentials("github-copilot", CopilotAvailabilityCredential(ids));
        var registered = session.Runner.GetModels("github-copilot");
        Assert.Contains(registered, model => model.Id == "fixture-denied");
        var expected = mode == "restricted" ? ["fixture-allowed"] : mode == "empty" ? Array.Empty<string>() : registered.Select(model => model.Id).ToArray();
        var available = CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner).Where(model => model.Provider == "github-copilot");
        Assert.Equal(expected, available.Select(model => model.Id));
        // 1. 【CodingAgent】【权限子集】菜单传入的交错子集应保留顺序和重复，不重新扩充目录
        var subset = new[] { registered.First(model => model.Id == "fixture-denied"), session.Runner.Model, registered.First(model => model.Id == "fixture-allowed"), session.Runner.Model };
        Assert.Equal(subset.Where(model => model.Provider != "github-copilot" || expected.Contains(model.Id)), CodingAgentModelAvailability.GetAuthConfiguredModels(session.Runner, subset));
        await DrainAsync(session.RunAsync("/copilot-access " + mode, deadline.Token));
    }

    /// <summary>【CodingAgent】【动态凭据】同一扩展进程在保存 OAuth、换成密钥和再次保存空权限后不保留旧目录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CopilotAvailability_UpdatesAcrossCredentialChanges()
    {
        using var fixture = new Fixture("copilot-changes", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(CreateCopilotAvailabilityOptions(fixture), deadline.Token);
        session.Runner.SaveOAuthCredentials("github-copilot", CopilotAvailabilityCredential("[\"fixture-allowed\"]"));
        await DrainAsync(session.RunAsync("/copilot-access restricted", deadline.Token));
        session.Runner.SaveApiKeyCredential("github-copilot", new ApiKeyCredential("synthetic-copilot-key"));
        await DrainAsync(session.RunAsync("/copilot-access all", deadline.Token));
        session.Runner.SaveOAuthCredentials("github-copilot", CopilotAvailabilityCredential("[]"));
        await DrainAsync(session.RunAsync("/copilot-access empty", deadline.Token));
    }

    /// <summary>【CodingAgent】【原生归属】相同 ID 的原生提供方使用自己的规则，注销后恢复内置账户权限。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CopilotAvailability_NativeOverrideOwnsFiltering()
    {
        using var fixture = new Fixture("copilot-native", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = CreateCopilotAvailabilityOptions(fixture);
        File.WriteAllText(Path.Combine(fixture.AgentDirectory, "extensions", "native.js"), """
            export default pi=>pi.registerCommand('copilot-native',{handler:async(_,ctx)=>{
              const registry=ctx.modelRegistry,provider='github-copilot';
              const models=['native-yes','native-no'].map(id=>({id,provider,api:'openai-completions',baseUrl:'http://unit.invalid'}));
              registry.registerProvider({id:provider,getModels:()=>models,
                auth:{oauth:{name:'test',login:async()=>{},refresh:async()=>{throw Error('must not refresh');},toAuth:async()=>({})}},
                filterModels:items=>items.filter(model=>model.id==='native-yes')});
              const ids=()=>registry.getAvailable().filter(model=>model.provider===provider).map(model=>model.id).join(',');
              if(ids()!=='native-yes')throw Error('native sync filter '+ids());
              const asyncModels=await registry.getAvailableOfType('chat',provider);
              if(asyncModels.length!==1||asyncModels[0].id!=='native-yes')throw Error('native async filter');
              registry.unregisterProvider(provider);
              if(ids()!=='fixture-allowed')throw Error('builtin permission not restored '+ids());
            }});
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(options, deadline.Token);
        session.Runner.SaveOAuthCredentials("github-copilot", CopilotAvailabilityCredential("[\"fixture-allowed\"]"));
        await DrainAsync(session.RunAsync("/copilot-native", deadline.Token));
    }

    /// <summary>【CodingAgent】【权限夹具】注册纯内存模型及真实 Node 断言命令，不访问任何模型服务。</summary>
    /// <param name="fixture">隔离文件夹具。</param><returns>SDK 会话配置。</returns>
    private static CodingAgentSdkCreateSessionOptions CreateCopilotAvailabilityOptions(Fixture fixture)
    {
        var catalog = fixture.Catalog();
        foreach (var type in new[] { ModelTypes.Chat, ModelTypes.Image, ModelTypes.Classifier })
            foreach (var id in new[] { "fixture-allowed", "fixture-denied" })
                catalog.RegisterModel(new Model { Id = id, Name = id, Type = type, Provider = "github-copilot", Api = "openai-completions", BaseUrl = "http://unit.invalid" });
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "access.js"), """
            export default pi=>pi.registerCommand('copilot-access',{handler:async(mode,ctx)=>{
              const registry=ctx.modelRegistry,provider='github-copilot';
              const all=registry.getAll().filter(model=>model.provider===provider);
              if(!all.some(model=>model.id==='fixture-denied')||!all.some(model=>model.id==='fixture-allowed'))throw Error('full catalog was filtered');
              const expected=mode==='restricted'?['fixture-allowed']:mode==='empty'?[]:all.map(model=>model.id);
              for(const models of [registry.getAvailable().filter(model=>model.provider===provider),await registry.getAvailableOfType('chat',provider)])
                if(JSON.stringify(models.map(model=>model.id))!==JSON.stringify(expected))throw Error('chat permission '+mode+' '+models.map(model=>model.id));
              for(const type of ['image','classifier']){
                const models=await registry.getAvailableOfType(type,provider);
                if(models.length!==2||!models.some(model=>model.id==='fixture-denied'))throw Error('nonchat filtered '+type);
              }
              if(!registry.hasConfiguredAuth(provider)||!registry.getProviderAuthStatus(provider).configured)throw Error('empty permission removed auth');
            }});
            """);
        return new() { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model", ModelCatalog = catalog };
    }

    /// <summary>【CodingAgent】【权限凭据】创建已过期的合成令牌，查询成功同时证明没有触发在线刷新。</summary>
    /// <param name="ids">可选原生清单 JSON。</param><returns>用于隔离测试的 OAuth 凭据。</returns>
    private static OAuthCredentials CopilotAvailabilityCredential(string? ids)
    {
        using var document = ids is null ? null : JsonDocument.Parse(ids);
        return new()
        {
            Access = "synthetic-copilot-access", Refresh = "synthetic-copilot-refresh", ExpiresAt = DateTimeOffset.UnixEpoch,
            Properties = document is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement> { ["availableModelIds"] = document.RootElement.Clone() }
        };
    }
}
