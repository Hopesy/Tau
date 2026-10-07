// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentAuthOptionsTests
{
    /// <summary>【CodingAgent】【认证显示】核对认证来源及另一种已配置方式的显示语义。</summary>
    /// <param name="selected">所选方式。</param><param name="configured">已配置方式，空值表示缺失。</param>
    /// <param name="source">配置来源。</param><param name="subscription">是否订阅。</param><param name="expected">预期后缀。</param>
    [Theory]
    [InlineData("oauth", null, "none", null, " • not configured")]
    [InlineData("api_key", "oauth", "OAuth", null, " • subscription configured")]
    [InlineData("api_key", "oauth", "OAuth", false, " • account configured")]
    [InlineData("oauth", "api_key", "KEY", true, " • API key configured")]
    [InlineData("oauth", "oauth", "OAuth", true, " ✓ configured")]
    [InlineData("api_key", "api_key", "stored credential", false, " ✓ configured")]
    [InlineData("api_key", "api_key", "auth.json api_key", false, " ✓ configured")]
    [InlineData("api_key", "api_key", "", false, " ✓ configured")]
    [InlineData("api_key", "api_key", "MY_KEY", false, " ✓ env: MY_KEY")]
    [InlineData("api_key", "api_key", "KEY, REGION_2", false, " ✓ env: KEY, REGION_2")]
    [InlineData("api_key", "api_key", "KEY,REGION", false, " ✓ KEY,REGION")]
    [InlineData("api_key", "api_key", "ambient credentials", false, " ✓ ambient credentials")]
    public void Status_MatchesConfiguredTypeAndSource(string selected, string? configured, string source, bool? subscription, string expected)
    {
        var status = configured is null ? null : new ProviderAuthStatus("custom", true, source, configured == "oauth", true, "");
        Assert.Equal(expected, CodingAgentAuthSelector.FormatOptionStatus(new("custom", "Custom", selected, status, Subscription: subscription)));
    }

    /// <summary>【CodingAgent】【认证搜索】两种方式有独立选择值，可用提供方标识、方式和实现名搜索。</summary>
    /// <param name="query">搜索词。</param><param name="authType">预期匹配方式。</param>
    [Theory]
    [InlineData("keylogin", "api_key")]
    [InlineData("api_key", "api_key")]
    [InlineData("subscriptionlogin", "oauth")]
    public void Selector_SeparatesMethodsAndSearchesHiddenNames(string query, string authType)
    {
        CodingAgentAuthOption[] options = [new("same-id", "Custom Name", "oauth", MethodName: "subscriptionlogin", Subscription: false),
            new("same-id", "Custom Name", "api_key", MethodName: "keylogin")];
        var selector = CodingAgentAuthSelector.CreateSelectList(new("same-id", []) { Options = options, Mode = "login" });
        Assert.Equal(2, selector.FilteredItems.Select(item => item.Value).Distinct().Count());
        Assert.Contains(selector.FilteredItems, item => item.Description == "[account] • not configured");
        selector.SetFilter(query);
        Assert.Equal(options.Single(option => option.AuthType == authType).SelectionKey, Assert.Single(selector.FilteredItems).Value);
        selector.SetFilter("same-id");
        Assert.Equal(2, selector.FilteredItems.Count);
    }

    /// <summary>【CodingAgent】【认证标签】单一方式列表省略重复类型标签。</summary>
    [Fact]
    public void Selector_OnlyLabelsMixedTypes()
    {
        var selector = CodingAgentAuthSelector.CreateSelectList(new("p", []) { Options = [new("p", "Provider", "oauth")] });
        Assert.Equal(" • not configured", Assert.Single(selector.FilteredItems).Description);
    }
}

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【双方式登录】真实 Node 提供方按所选方式登录，允许替换另一种已保存认证。</summary>
    /// <param name="authType">本次选择方式。</param><param name="stored">是否预存另一种凭据。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("oauth", false)]
    [InlineData("oauth", true)]
    [InlineData("api_key", false)]
    [InlineData("api_key", true)]
    public async Task AuthSelector_LoginPersistsSelectedMethod(string authType, bool stored)
    {
        using var fixture = new Fixture("dual-login", "models");
        WriteDualAuthExtension(fixture);
        var credentials = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]);
        if (stored)
        {
            if (authType == "oauth") credentials.SaveApiKey("dual", new ApiKeyCredential("old-key"));
            else credentials.Save("dual", new OAuthCredentials { Access = "old-access", Refresh = "old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) });
        }
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Equal("Dual Provider", session.Runner.GetProviderDisplayName("dual"));
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, authSelector: (state, _) =>
        {
            Assert.Equal("login", state.Mode);
            var options = state.Options.Where(option => option.Provider == "dual").ToArray();
            Assert.Equal(2, options.Length);
            Assert.All(options, option => Assert.Equal("Dual Provider", option.Name));
            Assert.All(options, option => Assert.Equal(stored, option.Status!.IsConfigured));
            return Task.FromResult<string?>(options.Single(option => option.AuthType == authType).SelectionKey);
        });
        var result = await router.TryHandleAsync(stored ? "/login dual" : "/login");
        Assert.False(result!.IsError, result.Message);
        Assert.Contains("authenticated successfully", result.Message);
        var saved = credentials.LoadEntries()["dual"];
        Assert.Equal(authType == "oauth" ? "oauth-access" : null, saved.OAuth?.Access);
        Assert.Equal(authType == "api_key" ? "api-key" : null, saved.ApiKey);
        Assert.Contains(session.Runner.ListStoredCredentials(), item => item.ProviderId == "dual" && item.Type == authType);
    }

    /// <summary>【CodingAgent】【认证选择边界】取消、伪造选择及旧回调歧义均不写入凭据。</summary>
    /// <param name="selection">选择器返回内容。</param><param name="error">是否应报告错误。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(null, false)]
    [InlineData("dual", true)]
    [InlineData("unknown", true)]
    public async Task AuthSelector_InvalidOrCancelledSelectionDoesNotLogin(string? selection, bool error)
    {
        using var fixture = new Fixture("dual-cancel", "models");
        WriteDualAuthExtension(fixture);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, authSelector: (_, _) => Task.FromResult(selection));
        var result = await router.TryHandleAsync("/login dual");
        Assert.Equal(error, result!.IsError);
        Assert.DoesNotContain(session.Runner.ListStoredCredentials(), item => item.ProviderId == "dual");
    }

    /// <summary>【CodingAgent】【遗留凭据】登出列表保留已卸载提供方，排除仅存在于 models.json 的密钥。</summary>
    /// <param name="oauth">是否保存已过期 OAuth 凭据。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthSelector_LogoutIncludesUnregisteredStoredCredentials(bool oauth)
    {
        using var fixture = new Fixture("orphan-logout", "models");
        var store = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]);
        if (oauth) store.Save("uninstalled", new OAuthCredentials { Access = "expired", Refresh = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        else store.SaveApiKey("uninstalled", new ApiKeyCredential("saved"));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, authSelector: (state, _) =>
        {
            Assert.Equal("logout", state.Mode);
            var option = Assert.Single(state.Options);
            Assert.Equal("uninstalled", option.Provider);
            Assert.Equal(oauth ? "oauth" : "api_key", option.AuthType);
            Assert.Equal(" ✓ configured", CodingAgentAuthSelector.FormatOptionStatus(option));
            return Task.FromResult<string?>(option.SelectionKey);
        });
        var result = await router.TryHandleAsync("/logout");
        Assert.False(result!.IsError, result.Message);
        Assert.Empty(store.LoadEntries());
        Assert.True(session.Runner.GetAuthStatus("session-provider").IsConfigured);
    }

    /// <summary>【CodingAgent】【登录夹具】写入同时支持 OAuth 和 API key 的原生扩展定义。</summary>
    /// <param name="fixture">独立资源目录。</param>
    private static void WriteDualAuthExtension(Fixture fixture)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dual.js"), """
            export default pi=>pi.registerProvider({id:'dual',name:'Dual Provider',
              getModels:()=>[{id:'chat',provider:'dual',name:'Dual Chat',api:'openai-completions',baseUrl:'http://127.0.0.1:1',input:['text']}],
              auth:{
                apiKey:{name:'Key Login',check:async()=>undefined,resolve:async()=>undefined,
                  login:async()=>({type:'api_key',key:'api-key'})},
                oauth:{name:'Account Login',isSubscription:false,
                  login:async()=>({type:'oauth',access:'oauth-access',refresh:'refresh',expires:Date.now()+3600000}),
                  refresh:async c=>c,toAuth:async c=>({apiKey:c.access})}
              }});
            """);
    }
}
