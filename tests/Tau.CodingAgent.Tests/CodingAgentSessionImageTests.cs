// 作者：xxx
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【提示图片模型】启动钩子看到原图并选择新模型，随后按新模型及实时设置处理并保存图片。</summary>
    /// <param name="resize">是否允许自动缩放。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task SessionImages_UseModelSelectedByBeforeAgentStart(bool resize)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.on('before_agent_start',async(e,ctx)=>{
              if(Buffer.from(e.images[0].data,'base64').readUInt32BE(16)!==40)throw Error('image normalized too early');
              if(!await pi.setModel(ctx.modelRegistry.find('synthetic','small')))throw Error('cannot select model');
            });
            """);
        var (runner, provider, commands) = CreateImageRunner(fixture, false);
        using (commands)
        {
            var settings = new CodingAgentSettingsStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "image-settings.json"));
            settings.Save(new(null, null, ImagesAutoResize: resize)); runner.ConfigureSessionSettings(settings);
            var image = new ImageContent(Convert.ToBase64String(ImageTestData.CreatePng(40, 20)), "image/png");
            await foreach (var _ in runner.RunAsync([new TextContent("inspect"), image])) { }
            Assert.Equal("small", runner.Model.Id);
            var stored = Assert.Single(runner.Messages.OfType<UserMessage>());
            var actual = Assert.Single(stored.Content.OfType<ImageContent>());
            Assert.Equal(new TuiImageDimensions(resize ? 10 : 40, resize ? 5 : 20), TuiTerminalImage.GetPngDimensions(actual.Data));
            Assert.Equal(resize, Assert.Single(stored.Content.OfType<TextContent>()).Text.Contains("Multiply coordinates by 4.00", StringComparison.Ordinal));
            Assert.Equal(actual.Data, Assert.Single(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>()).Content.OfType<ImageContent>().Single().Data);
        }
    }

    /// <summary>【CodingAgent】【坏图片提示】无效提示图片被省略并留下说明，不让提供方拒绝整个会话。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task SessionImages_OmitInvalidPromptImageWithPersistentHint()
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync([new ImageContent("YWJj", "image/png")])) { }
        var message = Assert.Single(runner.Messages.OfType<UserMessage>());
        Assert.Empty(message.Content.OfType<ImageContent>());
        Assert.Contains(CodingAgentImageProcessing.ResizeFailure, Assert.Single(message.Content.OfType<TextContent>()).Text);
        Assert.Empty(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>().SelectMany(user => user.Content).OfType<ImageContent>());
    }

    /// <summary>【CodingAgent】【工具图片顺序】扩展替换的图片在最终结果事件、历史和下一次请求中一致受限，重载不改变处理顺序。</summary>
    /// <param name="reload">是否重建扩展绑定。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SessionImages_NormalizeAfterToolResultHooksAndReload(bool reload)
    {
        var image = Convert.ToBase64String(ImageTestData.CreatePng(40, 20));
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async()=>({content:[{type:'text',text:'original'}],details:{kept:true},usage:{input:7,output:0,totalTokens:7}})});
              pi.on('tool_result',e=>({content:[{type:'image',data:'__IMAGE__',mimeType:'image/png'}],structuredContent:{current:true}}));
            };
            """.Replace("__IMAGE__", image, StringComparison.Ordinal));
        var (runner, provider, commands) = CreateImageRunner(fixture, true);
        using (commands)
        {
            runner.SelectModel("synthetic", "small");
            if (reload) runner.RefreshExtensionBindings(commands);
            var events = new List<AgentEvent>(); await foreach (var evt in runner.RunAsync("tool image")) events.Add(evt);
            var message = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            var normalized = Assert.Single(message.Content.OfType<ImageContent>());
            Assert.Equal(new TuiImageDimensions(10, 5), TuiTerminalImage.GetPngDimensions(normalized.Data));
            Assert.Contains("displayed at 10x5", Assert.Single(message.Content.OfType<TextContent>()).Text);
            var result = Assert.Single(events.OfType<ToolExecutionEndEvent>()).Result;
            Assert.False(result.IsError); Assert.Equal(7, result.Usage?.InputTokens);
            Assert.True(result.StructuredContent!.Value.GetProperty("current").GetBoolean()); Assert.NotNull(result.Details);
            Assert.Equal(normalized.Data, Assert.Single(Assert.Single(provider.Contexts[1].Messages.OfType<ToolResultMessage>()).Content.OfType<ImageContent>()).Data);
        }
    }

    /// <summary>【CodingAgent】【嵌套图片】子工具调用返回父工具之前也应用模型限制，父结果不重复添加说明。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task SessionImages_NormalizeNestedToolResultsBeforeParentReadsThem()
    {
        var image = Convert.ToBase64String(ImageTestData.CreatePng(40, 20));
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerTool({name:'leaf',parameters:{type:'object'},execute:async()=>({content:[{type:'image',data:'__IMAGE__',mimeType:'image/png'}]})});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                const child=await ctx.executeTool('leaf',{}),content=child.result.content;
                if(child.isError||Buffer.from(content[0].data,'base64').readUInt32BE(16)!==10)throw Error('nested image not normalized');
                return {content,terminate:true};
              }});
            };
            """.Replace("__IMAGE__", image, StringComparison.Ordinal));
        var (runner, _, commands) = CreateImageRunner(fixture, true);
        using (commands)
        {
            runner.SelectModel("synthetic", "small"); await foreach (var _ in runner.RunAsync("nested image")) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Join("\n", parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Single(parent.Content.OfType<TextContent>()); Assert.Single(parent.Content.OfType<ImageContent>());
        }
    }

    /// <summary>【CodingAgent】【图片会话夹具】创建两个尺寸策略的模型及真实扩展工具流水线。</summary>
    /// <param name="fixture">扩展夹具。</param><param name="toolCall">是否先产生工具调用。</param><returns>运行器、捕获器和命令存储。</returns>
    private static (RuntimeCodingAgentRunner Runner, CaptureProvider Provider, CodingAgentExtensionCommandStore Commands) CreateImageRunner(Fixture fixture, bool toolCall)
    {
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var commands = new CodingAgentExtensionCommandStore(root, explicitPaths: fixture.Files, includeDefaults: false, javaScriptRuntime: fixture.Runtime);
        var provider = new CaptureProvider(toolCall ? [new AssistantMessage([new ToolCallContent("call", "root_tool", "{}")]) { StopReason = StopReason.ToolUse }] : []);
        var registry = new ProviderRegistry(); registry.Register(provider.Api, provider);
        var model = new Model { Provider = "synthetic", Id = "large", Name = "Large", Api = provider.Api };
        var catalog = new ModelCatalog(); catalog.RegisterModel(model);
        catalog.RegisterModel(model with { Id = "small", InputLimits = new() { Images = new() { Resize = new() { MaxWidth = 10, MaxHeight = 10 } } } });
        var runner = new RuntimeCodingAgentRunner(new AgentRuntime(), new AgentLoopConfig
        {
            Model = model, ProviderRegistry = registry, Tools = commands.LoadTools(), Interceptors = commands.LoadToolInterceptors(),
            StreamOptions = new() { ApiKey = "synthetic" }, ConvertToLlm = AgentHarnessMessages.ConvertToLlm
        }, catalog, workingDirectory: root, extensionLifecycleEventSink: fixture.Sink, systemPromptOptions: new() { Cwd = root });
        commands.BindSession(runner); return (runner, provider, commands);
    }
}
