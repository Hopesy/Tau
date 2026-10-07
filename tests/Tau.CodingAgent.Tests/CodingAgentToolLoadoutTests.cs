// 作者：xxx
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【工具组合】钩子跨扩展有序合并，隐藏声明不妨碍调用和恢复，强制提示保持投影。</summary>
    /// <param name="fromJavaScript">是否由 Node 同步切换工具组合。</param>
    /// <param name="forced">是否在启动钩子中覆盖系统提示。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolLoadout_ProjectsRequestsAndRetainsCallableHistory(bool fromJavaScript, bool forced)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let count=0;
              pi.registerTool({name:'leaf',description:'Original leaf',promptSnippet:'LEAF_SNIPPET',namespace:{name:'group'},parameters:{type:'object'},
                execute:async()=>({content:[{type:'text',text:'leaf result'}]})});
              pi.registerTool({name:'root_tool',exposure:'model-only',description:'Original root',parameters:{type:'object'},
                prepareLoadout:loadout=>{
                  count++;
                  if(loadout.declared.find(t=>t.name==='root_tool').description!=='Original root')throw Error('description leaked');
                  if(loadout.getExposure('root_tool')!=='model-only'||loadout.getNamespace('leaf').name!=='group'||loadout.getNamespace('missing')!==undefined)throw Error('metadata missing');
                  if(!loadout.callable.some(t=>t.name==='leaf')||loadout.callable.some(t=>t.name==='root_tool'))throw Error('callable set');
                  return {descriptions:{root_tool:'First replacement'},hiddenDeclarations:['leaf']};
                },execute:async(id,a,s,u,ctx)=>{
                  const result=await ctx.executeTool('leaf',{});
                  if(result.isError||result.result.content[0].text!=='leaf result')throw Error('hidden tool unavailable');
                  return {content:[],terminate:true};
                }});
              pi.registerCommand('select',{handler:()=>{
                const before=count; pi.setActiveTools(['leaf','root_tool','broken','last']);
                if(count!==before+1)throw Error('loadout not prepared synchronously');
                return 'selected';
              }});
              pi.registerTool({name:'broken',parameters:{type:'object'},prepareLoadout:()=>{throw Error('isolated hook failure');},execute:async()=>({content:[]})});
              pi.registerTool({name:'inactive',defaultActive:false,parameters:{type:'object'},prepareLoadout:()=>{throw Error('inactive hook ran');},execute:async()=>({content:[]})});
            };
            """, """
            export default pi=>{
              pi.registerTool({name:'last',parameters:{type:'object'},prepareLoadout:loadout=>{
                if(loadout.declared.find(t=>t.name==='root_tool')?.description!=='Original root')throw Error('not original');
                return {descriptions:{root_tool:'Final replacement'},hiddenDeclarations:['broken']};
              },execute:async()=>({content:[]})});
            };
            """, forced ? "export default pi=>pi.on('before_agent_start',()=>({systemPrompt:'FORCED'}));" : "export default pi=>{};");
        var options = new CodingAgentSystemPromptOptions
        {
            Cwd = Path.GetDirectoryName(fixture.Files[0])!,
            ToolSnippets = new() { ["leaf"] = "LEAF_SNIPPET", ["root_tool"] = "ROOT_SNIPPET" }
        };
        var (runner, provider, commands) = CreateNestedRunner(fixture, promptOptions: options);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            if (fromJavaScript)
            {
                var selected = fixture.Runtime.Invoke(fixture.Files[0], "select", "", timeout.Token);
                Assert.True(selected.Success, selected.Error);
                Assert.Equal("selected", selected.StatusMessage);
            }
            else runner.SetActiveTools(["leaf", "root_tool", "broken", "last"]);
            await foreach (var ignored in runner.RunAsync("use hidden tool", timeout.Token)) { }
            Assert.False(Assert.Single(runner.Messages.OfType<ToolResultMessage>()).IsError);
            var first = Assert.Single(provider.Contexts);
            Assert.Equal(["root_tool", "last"], Transcript.GetCurrentTools(first.Messages).Select(tool => tool.Name));
            Assert.Equal("Final replacement", Assert.Single(Transcript.GetCurrentTools(first.Messages), tool => tool.Name == "root_tool").Description);
            Assert.All(first.Messages.OfType<SystemMessage>(), system =>
            {
                Assert.DoesNotContain(system.ToolsAdded ?? [], tool => tool.Name is "leaf" or "broken");
                Assert.DoesNotContain(system.ToolsRemoved ?? [], tool => tool.Name is "leaf" or "broken");
            });
            var firstPrompt = Transcript.GetCurrentSystemPrompt(first.Messages);
            Assert.DoesNotContain("LEAF_SNIPPET", firstPrompt);
            if (forced) Assert.Equal("FORCED", firstPrompt);
            Assert.Contains(Transcript.GetCurrentTools(runner.Messages), tool => tool.Name == "leaf");
            Assert.Contains("leaf", runner.GetActiveToolNames());
            Assert.Equal("Original root", Assert.Single(runner.GetRegisteredTools(), tool => tool.Name == "root_tool").Description);

            // 1. 【CodingAgent】【组合恢复】停用准备钩子后恢复隐藏声明和简介，历史保持原有定义
            runner.SetActiveTools(["leaf"]);
            await foreach (var ignored in runner.RunAsync("plain loadout", timeout.Token)) { }
            var second = provider.Contexts[1];
            Assert.Equal("leaf", Assert.Single(Transcript.GetCurrentTools(second.Messages)).Name);
            if (!forced) Assert.Contains("LEAF_SNIPPET", Transcript.GetCurrentSystemPrompt(second.Messages));
        }
    }
}
