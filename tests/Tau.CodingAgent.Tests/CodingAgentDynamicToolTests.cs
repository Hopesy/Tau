// 作者：xxx
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【工具注册】拒绝无效参数 Schema，跨扩展重名始终保持加载顺序中的首个定义。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task DynamicTools_ValidateSchemaAndRetainFirstExtensionOwner()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerTool({name:'shared',parameters:{type:'object'},description:'first',execute:async()=>({content:[]})});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async()=>({content:[],terminate:true})});
              pi.registerCommand('invalid',{handler:()=>{
                for(const parameters of [undefined,null,[],false,'object']){
                  let rejected=false;
                  try{pi.registerTool({name:'invalid',parameters,execute:async()=>({content:[]})});}catch(e){rejected=e.message.includes('object parameter schema');}
                  if(!rejected)throw Error('invalid schema accepted');
                }
                return 'rejected';
              }});
            };
            """, """
            export default pi=>pi.registerCommand('duplicate',{handler:()=>{
              pi.registerTool({name:'shared',parameters:{type:'object'},description:'second',execute:async()=>({content:[]})});
              return pi.getAllTools().find(t=>t.name==='shared').description;
            }});
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        {
            Assert.Equal("rejected", fixture.Runtime.Invoke(fixture.Files[0], "invalid", "").StatusMessage);
            Assert.DoesNotContain(runner.GetRegisteredTools(), tool => tool.Name == "invalid");
            Assert.Equal("first", fixture.Runtime.Invoke(fixture.Files[1], "duplicate", "").StatusMessage);
            Assert.Equal("first", Assert.Single(runner.GetRegisteredTools(), tool => tool.Name == "shared").Description);
            await foreach (var ignored in runner.RunAsync("use surviving definition")) { }
            Assert.False(Assert.Single(runner.Messages.OfType<ToolResultMessage>()).IsError);
        }
    }

    /// <summary>【CodingAgent】【动态工具】运行中新增、替换与策略变更即时可调用，关闭过的默认工具保持关闭。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task DynamicTools_RegisterAndCallWithinSameExecution()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const tool=(name,exposure='direct')=>({name,exposure,description:'Original '+name,parameters:{type:'object'},execute:async()=>({content:[]})});
              pi.registerTool(tool('off'));
              pi.registerTool(tool('hidden','hidden'));
              pi.registerTool({...tool('root_tool','model-only'),prepareLoadout:loadout=>({descriptions:{root_tool:loadout.callable.map(t=>t.name).join(',')}}),
                execute:async(id,a,s,u,ctx)=>{
                  pi.registerTool({...tool('late','deferred'),outputSchema:{type:'object'},namespace:{name:'runtime'},
                    execute:async()=>({content:[],structuredContent:{ok:42}})});
                  const late=await ctx.executeTool('late',{});
                  if(late.isError||late.result.structuredContent.ok!==42||pi.getActiveTools().includes('late'))throw Error('late unavailable');
                  pi.registerTool({...tool('off'),description:'Updated off'});
                  if(pi.getActiveTools().includes('off'))throw Error('off reactivated');
                  pi.registerTool(tool('hidden'));
                  if(!pi.getActiveTools().includes('hidden')||(await ctx.executeTool('hidden',{})).isError)throw Error('hidden not activated');
                  const info=pi.getAllTools().find(t=>t.name==='late');
                  if(info.namespace.name!=='runtime'||info.outputSchema.type!=='object')throw Error('metadata lost');
                  return {content:[],terminate:true};
                }});
            };
            """);
        var (runner, provider, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            runner.SetActiveTools(["root_tool"]);
            await foreach (var ignored in runner.RunAsync("dynamic tools", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal(2, parent.NestedCalls?.Calls.Count);
            Assert.Equal(["root_tool", "hidden"], runner.GetActiveToolNames());
            Assert.Equal(["off", "hidden", "root_tool", "late"], runner.GetRegisteredTools().Select(tool => tool.Name));
            Assert.Equal("Updated off", Assert.Single(runner.GetRegisteredTools(), tool => tool.Name == "off").Description);
            Assert.Single(commands.LoadToolDefinitions(), tool => tool.Name == "late");
            await foreach (var ignored in runner.RunAsync("next request", timeout.Token)) { }
            var tools = Transcript.GetCurrentTools(provider.Contexts[1].Messages);
            Assert.Equal(["root_tool", "hidden"], tools.Select(tool => tool.Name));
            Assert.Equal("hidden,late", Assert.Single(tools, tool => tool.Name == "root_tool").Description);
        }
    }

    /// <summary>【CodingAgent】【延迟恢复】未知历史工具注册后恢复启用，主动缩减组合或开始回合会清理待恢复名称。</summary>
    /// <param name="operation">keep 保留、drop 缩减工具、run 先开始回合。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("keep")]
    [InlineData("drop")]
    [InlineData("run")]
    public async Task DynamicTools_RestorePendingNamesUntilLoadoutIsReplaced(string operation)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const tool=name=>({name,parameters:{type:'object'},execute:async()=>({content:[],terminate:true})});
              pi.registerTool(tool('root_tool')); pi.registerTool(tool('extra'));
              pi.registerCommand('register',{handler:()=>{
                pi.registerTool({...tool('late'),defaultActive:false});
                return pi.getActiveTools().join(',');
              }});
            };
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var schema = runner.GetRegisteredTools()[0].ParameterSchema;
            runner.RestoreSession(new([new SystemMessage("") { ToolsAdded = [new("root_tool", "", schema), new("extra", "", schema), new("late", "", schema)] }], null, null, null));
            Assert.Equal(["root_tool", "extra"], runner.GetActiveToolNames());
            if (operation == "drop") runner.SetActiveTools(["root_tool"]);
            if (operation == "run") await foreach (var ignored in runner.RunAsync("clear pending", timeout.Token)) { }
            var result = fixture.Runtime.Invoke(fixture.Files[0], "register", "", timeout.Token);
            Assert.True(result.Success, result.Error);
            Assert.Equal(operation == "keep", runner.GetActiveToolNames().Contains("late"));
            Assert.Equal(operation == "keep", result.StatusMessage!.Split(',').Contains("late"));
            Assert.Single(runner.GetRegisteredTools(), tool => tool.Name == "late");
        }
    }
}
