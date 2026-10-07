// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>五种访问策略区分模型声明与嵌套调用，显式启用不突破 hidden 限制，工具结构元数据完整。</summary>
    /// <param name="explicitSelection">是否显式启用默认关闭及延迟工具。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolExposure_SeparatesDeclarationsFromCallableTools(bool explicitSelection)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              for(const [name,exposure,defaultActive] of [['direct','direct',true],['only_model','model-only',true],['code','codemode',true],['defer','deferred',true],['hidden','hidden',true],['off','direct',false]]){
                pi.registerTool({name,exposure,defaultActive,description:'tool '+name,parameters:{type:'object'},outputSchema:{type:'object',properties:{ok:{type:'boolean'}}},
                  namespace:{name:'group',description:'Grouped tools',instructions:'Group instructions'},annotations:{readOnlyHint:true},
                  constrainedSampling:{type:'json_schema',strict:'always'},execute:async()=>({content:[],structuredContent:{ok:true}})});
              }
              pi.registerTool({name:'root_tool',exposure:'model-only',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                const active=pi.getActiveTools(), callable=ctx.tools.map(t=>t.name);
                const expected=active.includes('off')?['code','defer','off']:['direct','code','defer'];
                if(JSON.stringify(callable)!==JSON.stringify(expected))throw Error('wrong callable set '+callable);
                if(active.includes('hidden')||callable.includes('root_tool')||callable.includes('only_model'))throw Error('policy bypass');
                const info=pi.getAllTools().find(t=>t.name==='defer');
                if(info.exposure!=='deferred'||info.namespace.instructions!=='Group instructions'||!info.annotations.readOnlyHint||info.outputSchema.type!=='object')throw Error('metadata missing');
                info.namespace.instructions='changed';
                if(pi.getAllTools().find(t=>t.name==='defer').namespace.instructions==='changed')throw Error('shared metadata');
                for(const name of expected){const result=await ctx.executeTool(name,{});if(result.isError||!result.result.structuredContent.ok)throw Error('callable failed '+name);}
                for(const name of ['hidden','only_model',active.includes('off')?'direct':'off']){
                  if(!(await ctx.executeTool(name,{})).isError)throw Error('unreachable tool executed '+name);
                }
                return {content:[],terminate:true};
              }});
            };
            """);
        var (runner, provider, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            Assert.Equal(["direct", "only_model", "root_tool"], runner.GetActiveToolNames());
            if (explicitSelection) runner.SetActiveTools(["root_tool", "code", "defer", "off", "hidden", "missing"]);
            await foreach (var ignored in runner.RunAsync("check access", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal(6, parent.NestedCalls?.Calls.Count);
            var request = Assert.Single(provider.Contexts);
            var declared = Transcript.GetCurrentTools(request.Messages);
            string[] expected = explicitSelection ? ["root_tool", "code", "defer", "off"] : ["direct", "only_model", "root_tool"];
            Assert.Equal(expected, declared.Select(tool => tool.Name));
            Assert.Equal("always", Assert.Single(declared, tool => tool.Name == (explicitSelection ? "defer" : "direct")).ConstrainedSampling?.Strict);
        }
    }
}
