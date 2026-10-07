// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【扩展工具】覆盖多模态结果、实时进度和并发执行的真实 Node 通道。</summary>
public sealed class CodingAgentExtensionToolContentTests
{
    /// <summary>工具执行和结果拦截都保留图片、文字签名及结构化详情。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolResult_PreservesImagesAndHandlerMutations()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.registerTool({name:'picture', parameters:{type:'object'}, execute:async ()=>({
                content:[{type:'image', data:'YWJj', mimeType:'image/png'}, {type:'text',text:'caption',textSignature:'sig'}],
                details:{items:[1,{done:true}]}
              })});
              pi.on('tool_result', e => {
                if (e.content[0].type !== 'image' || e.content[1].textSignature !== 'sig') throw Error('lost content');
                return {content:[...e.content,{type:'image',data:'ZGVm',mimeType:'image/jpeg'}],details:{...e.details,patched:true}};
              });
              pi.on('tool_result', e => ({content:[...e.content,{type:'text',text:String(e.details.patched)}],isError:true}));
            };
            """);
        var tool = fixture.Tool;
        var result = await tool.ExecuteAsync("call", fixture.Args);
        Assert.Equal(new ImageContent("YWJj", "image/png"), result.Content[0]);
        Assert.Equal("sig", Assert.IsType<TextContent>(result.Content[1]).TextSignature);
        var interceptor = new CodingAgentExtensionToolEventInterceptor(
            [new(fixture.FilePath, "test", "javascript", false, true)], fixture.Runtime);
        var transformed = await interceptor.AfterToolCallAsync(new ToolCallContext("call", "picture", fixture.Args, []), result);
        Assert.Equal(4, transformed.Content.Count);
        Assert.Equal(result.Content[0], transformed.Content[0]);
        Assert.Equal(result.Content[1], transformed.Content[1]);
        Assert.Equal(new ImageContent("ZGVm", "image/jpeg"), transformed.Content[2]);
        Assert.Equal("true", Assert.IsType<TextContent>(transformed.Content[3]).Text);
        Assert.True(transformed.IsError);
        var details = Assert.IsType<JsonElement>(transformed.Details);
        Assert.True(details.GetProperty("items")[1].GetProperty("done").GetBoolean());
    }

    /// <summary>显式空内容不被宿主替换为占位文本，详情和错误状态仍然保留。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task EmptyContent_RemainsEmpty()
    {
        using var fixture = new Fixture("""
            export default pi => pi.registerTool({name:'empty',parameters:{type:'object'},execute:async()=>({content:[],details:null,isError:true})});
            """);
        var result = await fixture.Tool.ExecuteAsync("call", fixture.Args);
        Assert.Empty(result.Content);
        Assert.True(result.IsError);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonElement>(result.Details).ValueKind);
    }

    /// <summary>进度在工具完成前到达，回调可以重入扩展命令且不会堵塞输出管道。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Updates_ArriveBeforeCompletionAndAllowReentry()
    {
        using var fixture = new Fixture("""
            export default pi => {
              let release;
              pi.registerCommand('release',{handler:()=>{release(); return 'released';}});
              pi.registerTool({name:'stream',parameters:{type:'object'},execute:async(id,args,signal,update)=>{
                const waiting = new Promise(resolve=>release=resolve);
                update({content:[{type:'image',data:'YWJj',mimeType:'image/png'},{type:'text',text:'waiting'}],details:{step:1}});
                await waiting;
                update({content:[],details:{step:2}});
                return {content:[{type:'text',text:'finished'}]};
              }});
            };
            """);
        var updates = new List<ToolUpdate>();
        var result = await fixture.Tool.ExecuteAsync("call", fixture.Args, onUpdate: update =>
        {
            updates.Add(update);
            if (updates.Count == 1)
                Assert.Equal("released", fixture.Runtime.Invoke(fixture.FilePath, "release", "").StatusMessage);
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsError);
        Assert.Equal(2, updates.Count);
        Assert.Equal(new ImageContent("YWJj", "image/png"), updates[0].Content![0]);
        Assert.Equal("waiting", updates[0].Text);
        Assert.Equal(1, Assert.IsType<JsonElement>(updates[0].Details).GetProperty("step").GetInt32());
        Assert.Empty(updates[1].Content!);
        Assert.Equal("finished", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    /// <summary>异步进度按发送顺序完成，最终结果不会越过较慢的回调。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Completion_WaitsForOrderedUpdates()
    {
        using var fixture = new Fixture("""
            export default pi => pi.registerTool({name:'stream',parameters:{type:'object'},execute:async(id,args,signal,update)=>{
              for (let i=0;i<5;i++) update({content:[{type:'text',text:String(i)}]});
              return {content:[]};
            }});
            """);
        var updates = new List<string>();
        await fixture.Tool.ExecuteAsync("call", fixture.Args, onUpdate: async update =>
        {
            await Task.Delay(update.Text == "0" ? 100 : 1);
            updates.Add(update.Text);
        });
        Assert.Equal(["0", "1", "2", "3", "4"], updates);
    }

    /// <summary>两个工具可同时进入 Node 等待阶段，各自收到对应调用的进度。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConcurrentTools_KeepProgressIsolated()
    {
        using var fixture = new Fixture("""
            export default pi => {
              const arrivals=[];
              pi.registerTool({name:'parallel',parameters:{type:'object'},execute:async(id,args,signal,update)=>{
                await new Promise(resolve=>{arrivals.push(resolve); if(arrivals.length===2) arrivals.forEach(r=>r());});
                update({content:[{type:'text',text:id}]});
                return {content:[{type:'text',text:id}]};
              }});
            };
            """);
        var tool = fixture.Tool;
        var firstUpdates = new List<string>();
        var secondUpdates = new List<string>();
        var first = tool.ExecuteAsync("first", fixture.Args, onUpdate: update => { firstUpdates.Add(update.Text); return Task.CompletedTask; });
        var second = tool.ExecuteAsync("second", fixture.Args, onUpdate: update => { secondUpdates.Add(update.Text); return Task.CompletedTask; });
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(["first"], firstUpdates);
        Assert.Equal(["second"], secondUpdates);
    }

    /// <summary>进度消费失败只使当前工具失败，其他命令仍能复用进程。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task UpdateFailure_DoesNotTerminateSharedProcess()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.registerCommand('pid',{handler:()=>String(process.pid)});
              pi.registerTool({name:'stream',parameters:{type:'object'},execute:async(id,args,signal,update)=>{
                update({content:[]}); return {content:[]};
              }});
            };
            """);
        var pid = fixture.Runtime.Invoke(fixture.FilePath, "pid", "").StatusMessage;
        var result = await fixture.Tool.ExecuteAsync("call", fixture.Args,
            onUpdate: _ => throw new InvalidOperationException("consumer failed"));
        Assert.True(result.IsError);
        Assert.Contains("consumer failed", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(pid, fixture.Runtime.Invoke(fixture.FilePath, "pid", "").StatusMessage);
    }

    /// <summary>单个测试的扩展资源，先关闭进程再删除临时目录。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tau-tool-content-" + Guid.NewGuid().ToString("N"));
        private readonly JsonDocument _args = JsonDocument.Parse("{}");
        public string FilePath { get; }
        public CodingAgentJavaScriptExtensionRuntime Runtime { get; }
        public JsonElement Args => _args.RootElement;
        public CodingAgentExtensionToolAdapter Tool { get; }

        /// <summary>写入并加载测试扩展，创建真实工具适配器。</summary>
        /// <param name="source">JavaScript 扩展源码。</param>
        public Fixture(string source)
        {
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, "extension.mjs");
            File.WriteAllText(FilePath, source);
            Runtime = new CodingAgentJavaScriptExtensionRuntime(_directory);
            var loaded = Runtime.Load(FilePath);
            Assert.True(loaded.Success, loaded.Error);
            var tool = Assert.Single(loaded.Tools);
            Tool = new CodingAgentExtensionToolAdapter(new(tool.Name, tool.Label, tool.Description, tool.ParameterSchema,
                FilePath, "test", "javascript", tool.HasPrepareArguments, tool.ExecutionMode), Runtime);
        }

        /// <summary>释放运行时、参数文档和本测试目录。</summary>
        public void Dispose()
        {
            Runtime.Dispose();
            _args.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
