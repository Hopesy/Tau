// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【命令目录】扩展读取其他模块、JSON 命令、提示与技能的完整来源，返回值修改不影响后续查询。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task GetCommands_ContainsAllSessionResourcesAndTracksReload()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var extensions = Path.Combine(agent, "extensions");
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(Path.Combine(agent, "skills", "demo"));
        Directory.CreateDirectory(Path.Combine(agent, "prompts"));
        File.WriteAllText(Path.Combine(agent, "skills", "demo", "SKILL.md"), "---\nname: demo\ndescription: Demo skill\n---\nContent");
        File.WriteAllText(Path.Combine(agent, "prompts", "review.md"), "Review template");
        File.WriteAllText(Path.Combine(extensions, "second.js"), "export default pi=>pi.registerCommand('second',{description:'Other module',handler:()=>{}});");
        File.WriteAllText(Path.Combine(extensions, "static.json"), """{"name":"static","response":"static response"}""");
        File.WriteAllText(Path.Combine(extensions, "catalog.js"), """
            export default pi=>{
              pi.registerCommand('catalog',{description:'Read catalog',handler:()=>{
                const first=pi.getCommands();
                first[0].sourceInfo.source='tampered'; first.length=0;
                const commands=pi.getCommands();
                if(commands.some(c=>c.sourceInfo.source==='tampered'))throw Error('shared catalog');
                pi.appendEntry('command_catalog',commands);
              }});
              pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, ModelCatalog = CreateModelCatalog() });
        await foreach (var ignored in session.RunAsync("/catalog")) { }
        var entry = Assert.Single(session.TreeSessionController!.Store.ReadExtensionSnapshot().Entries, entry => entry.CustomType == "command_catalog");
        var commands = entry.Data!.Value.EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        Assert.Equal(6, commands.Count);
        Assert.Equal("extension", commands["second"].GetProperty("source").GetString());
        Assert.Equal("extension", commands["static"].GetProperty("source").GetString());
        Assert.Equal("prompt", commands["review"].GetProperty("source").GetString());
        Assert.Equal("skill", commands["skill:demo"].GetProperty("source").GetString());
        Assert.Equal(Path.Combine(extensions, "second.js"), commands["second"].GetProperty("sourceInfo").GetProperty("path").GetString());
        Assert.All(commands.Values, item => Assert.Equal("user", item.GetProperty("sourceInfo").GetProperty("scope").GetString()));
        Assert.Equal("local", Assert.Single(session.SkillStore.Load()).SourceInfo.Source);
        File.Delete(Path.Combine(extensions, "second.js"));
        File.Delete(Path.Combine(agent, "prompts", "review.md"));
        await foreach (var ignored in session.RunAsync("/reload")) { }
        await foreach (var ignored in session.RunAsync("/catalog")) { }
        var latest = session.TreeSessionController.Store.ReadExtensionSnapshot().Entries.Last(entry => entry.CustomType == "command_catalog").Data!.Value;
        Assert.Equal(4, latest.GetArrayLength());
        Assert.DoesNotContain(latest.EnumerateArray(), item => item.GetProperty("name").GetString() is "second" or "review");
    }
}
