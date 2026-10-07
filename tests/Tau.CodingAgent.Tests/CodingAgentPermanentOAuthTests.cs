// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【长期凭据】新旧扩展协议都能保存并恢复 JavaScript 最大安全整数期限与结构化字段。</summary>
    /// <param name="native">是否使用原生 provider-owned auth 契约。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentOAuth_PreservesExpiryAcrossNodeAndSessionRestart(bool native)
    {
        using var fixture = new Fixture("permanent-oauth", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        var definition = native
            ? "pi.registerProvider({id:'permanent-oauth',name:'Permanent',getModels:()=>[],auth:{oauth:{name:'Permanent Login',login,refresh,toAuth:async c=>({apiKey:check(c),headers:{'X-Account':'42'},baseUrl:'https://account.invalid/v1'})}}});"
            : "pi.registerProvider('permanent-oauth',{api:'openai-completions',baseUrl:'https://unused.invalid',models:[{id:'chat'}],oauth:{name:'Permanent Login',login,refreshToken:refresh,getApiKey:check}});";
        File.WriteAllText(Path.Combine(directory, "permanent.js"), """
            export default pi=>{
              const login=async()=>({type:'oauth',access:'test-permanent',refresh:'',expires:Number.MAX_SAFE_INTEGER,
                account:{id:42},scopes:['chat'],enabled:true,env:{REGION:'test'}});
              const refresh=async()=>{throw Error('Permanent credentials must not refresh');};
              const check=c=>{
                if(c.expires!==Number.MAX_SAFE_INTEGER||c.account.id!==42||c.scopes[0]!=='chat'||!c.enabled||c.env.REGION!=='test')throw Error('credential fields lost');
                return c.access+'-'+c.account.id;
              };
            """ + definition + "}");
        var path = Path.Combine(fixture.AgentDirectory, "auth.json");
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }))
        {
            Assert.Empty(session.StartupExtensionErrors);
            var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore);
            var result = await router.TryHandleAsync("/login permanent-oauth");
            Assert.False(result!.IsError, result.Message);
            Assert.True(session.Runner.GetAuthStatus("permanent-oauth").IsConfigured);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(9007199254740991L, saved.RootElement.GetProperty("permanent-oauth").GetProperty("expires").GetInt64());
        }
        await using (var restarted = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }))
        {
            Assert.Empty(restarted.StartupExtensionErrors);
            var credentials = new OAuthCredentialStore([path]).Load()["permanent-oauth"];
            Assert.False(credentials.IsExpired());
            var provider = restarted.Runner.GetOAuthProvider("permanent-oauth")!;
            var auth = provider.ResolveAuth(credentials);
            Assert.Equal("test-permanent-42", auth.ApiKey);
            if (native)
            {
                Assert.Equal("https://account.invalid/v1", auth.BaseUrl);
                Assert.Equal("42", auth.Headers!["X-Account"]);
            }
        }
    }
}
