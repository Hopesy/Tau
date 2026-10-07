// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【扩展发现】启动后合并所有处理器资源，错误隔离，重载替换旧路径，删除扩展后清空其资源。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ResourcesDiscover_UpdatesSdkResourcesAcrossReloadAndRemoval()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var extensions = Path.Combine(agent, "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "control.js"), "export default pi=>pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});");
        foreach (var name in new[] { "first", "second" })
        {
            var folder = Path.Combine(temp.Path, "resources", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: discovered-{name}\ndescription: Skill {name}\n---\nUse {name}");
            File.WriteAllText(Path.Combine(folder, name + ".md"), "Template " + name);
            File.WriteAllText(Path.Combine(folder, name + ".json"), "{}");
        }
        File.WriteAllText(Path.Combine(temp.Path, "choice.txt"), "first");
        var extension = Path.Combine(extensions, "discover.js");
        File.WriteAllText(extension, """
            import fs from 'node:fs';
            import path from 'node:path';
            export default pi=>{
              let started=false;
              pi.on('session_start',()=>{started=true;});
              pi.on('resources_discover',()=>{throw Error('isolated discovery failure')});
              pi.on('resources_discover',(event,ctx)=>{
                if(!started||ctx.cwd!==event.cwd||!ctx.model)throw Error('discovery before session start');
                const name=fs.readFileSync(path.join(ctx.cwd,'choice.txt'),'utf8');
                pi.appendEntry('discovery',{reason:event.reason,name});
                event.reason='mutated';
                return {skillPaths:['resources/'+name],promptPaths:[`resources/${name}/${name}.md`],themePaths:[`resources/${name}/${name}.json`]};
              });
              pi.on('resources_discover',(event,ctx)=>{
                if(!['startup','reload'].includes(event.reason))throw Error('mutated event reused');
                const name=fs.readFileSync(path.join(ctx.cwd,'choice.txt'),'utf8');
                return {skillPaths:['resources/'+name]};
              });
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = false, ModelCatalog = CreateModelCatalog(),
            IncludeSkills = false, IncludePromptTemplates = false
        });
        Assert.Equal("resources_discover", Assert.Single(session.StartupExtensionErrors).EventType);
        Assert.Equal("discovered-first", Assert.Single(session.SkillStore.Load()).Name);
        Assert.Equal("first", Assert.Single(session.PromptTemplateStore.Load()).Name);
        Assert.Equal("extension:discover", Assert.Single(session.SkillStore.Load()).SourceInfo.Source);
        Assert.Equal("extension:discover", Assert.Single(session.PromptTemplateStore.Load()).SourceInfo.Source);
        Assert.Equal("temporary", Assert.Single(session.SkillStore.Load()).SourceInfo.Scope);
        Assert.Equal(extensions, Assert.Single(session.SkillStore.Load()).SourceInfo.BaseDir);
        Assert.Contains("discovered-first", session.Runner.GetSystemPrompt());
        var resources = session.ExtensionCommandStore.LoadResources();
        var skillPath = Assert.Single(resources.SkillPaths);
        Assert.Equal(Path.Combine(temp.Path, "resources", "first"), skillPath);
        Assert.Single(resources.ThemePaths);
        var source = resources.SourceInfos[skillPath];
        Assert.Equal("extension:discover", source.Source);
        Assert.Equal("temporary", source.Scope);
        Assert.Equal(extensions, source.BaseDir);
        File.WriteAllText(Path.Combine(temp.Path, "choice.txt"), "second");
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal("discovered-second", Assert.Single(session.SkillStore.Load()).Name);
        Assert.Equal("second", Assert.Single(session.PromptTemplateStore.Load()).Name);
        Assert.DoesNotContain("discovered-first", session.Runner.GetSystemPrompt());
        Assert.Contains("discovered-second", session.Runner.GetSystemPrompt());
        Assert.DoesNotContain(session.ExtensionCommandStore.LoadResources().SkillPaths, path => path.EndsWith("first"));
        var discoveries = session.TreeSessionController!.Store.ReadExtensionSnapshot().Entries.Where(entry => entry.CustomType == "discovery").ToArray();
        Assert.Equal(2, discoveries.Length);
        Assert.Equal("startup", discoveries[0].Data!.Value.GetProperty("reason").GetString());
        Assert.Equal("reload", discoveries[1].Data!.Value.GetProperty("reason").GetString());
        File.Delete(extension);
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Empty(session.SkillStore.Load());
        Assert.Empty(session.PromptTemplateStore.Load());
        Assert.Empty(session.ExtensionCommandStore.LoadResources().SourceInfos);
        Assert.DoesNotContain("discovered-second", session.Runner.GetSystemPrompt());
    }
}
