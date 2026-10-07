// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 提供方认证实测】真实 HTTP 逐请求读取最新令牌，不沿用提供方的模型地址与请求头，缺失凭据只发一次请求。</summary>
    /// <param name="missing">是否引用不存在的凭据提供方。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpProviderAuthUsesFreshSessionCredentialsWithoutCopyingModelSettings(bool missing)
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var extensions = Path.Combine(agent, "extensions"); Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "provider.js"), """
            // 作者：xxx
            export default pi=>{let calls=0;pi.registerProvider({id:'mcp-token-provider',getModels:()=>[],auth:{apiKey:{name:'fixture',
              resolve:async()=>({auth:{apiKey:'provider-'+(++calls),headers:{'X-Provider':'private'},baseUrl:'http://127.0.0.1:1'},env:{FIXTURE:'private'}})}}});};
            """);
        await using var server = await CodingAgentMcpHttpTests.Server.StartAsync();
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "provider", new()
        {
            ["url"] = new Uri(server.Address, missing ? "/provider-denied" : "/provider").AbsoluteUri,
            ["exposure"] = "direct", ["auth"] = new JsonObject { ["provider"] = missing ? "missing-provider" : "mcp-token-provider" }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true }, deadline.Token))
        {
            await session.Mcp!.WaitForServersAsync(deadline.Token);
            var status = Assert.Single(session.Mcp.GetStatus());
            if (missing) { Assert.Equal("needs-auth", status.State); Assert.Empty(session.Mcp.GetTools()); }
            else
            {
                Assert.Equal("connected", status.State);
                var tool = Assert.Single(session.Mcp.GetTools());
                var first = await tool.ExecuteAsync("one", ParseMcpSessionJson("{}"), deadline.Token);
                var second = await tool.ExecuteAsync("two", ParseMcpSessionJson("{}"), deadline.Token);
                var a = ParseMcpSessionJson(Assert.IsType<TextContent>(Assert.Single(first.Content)).Text);
                var b = ParseMcpSessionJson(Assert.IsType<TextContent>(Assert.Single(second.Content)).Text);
                Assert.StartsWith("Bearer provider-", a.GetProperty("authorization").GetString());
                Assert.NotEqual(a.GetProperty("authorization").GetString(), b.GetProperty("authorization").GetString());
                Assert.Equal(JsonValueKind.Null, a.GetProperty("providerHeader").ValueKind);
            }
        }
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var response = ParseMcpSessionJson(await http.GetStringAsync(new Uri(server.Address, "/status"), deadline.Token));
        var tokens = response.GetProperty("providerTokens").EnumerateArray().ToArray();
        if (missing) Assert.Equal(JsonValueKind.Null, Assert.Single(tokens).ValueKind);
        else { Assert.True(tokens.Length >= 5); Assert.Equal(tokens.Length, tokens.Select(value => value.GetString()).Distinct().Count()); }
        Assert.False(File.Exists(Path.Combine(agent, "mcp-auth.json")));
    }
}
