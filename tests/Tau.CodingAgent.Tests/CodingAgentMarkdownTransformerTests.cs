// 作者：xxx
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Components;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【Markdown 注册】同模块覆盖、模块顺序、异常隔离与上下文传参遵守原生契约。</summary>
    [Fact]
    public void MarkdownTransformers_ComposeModulesAndIsolateInvalidResults()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerMarkdownTransformer(()=> 'superseded');
              pi.registerMarkdownTransformer((md,ctx)=>md+'|'+[ctx.messageType,ctx.isStreaming,ctx.availableWidth].join(','));
            };
            """, "export default pi=>pi.registerMarkdownTransformer(()=>{throw Error('display only')});",
            "export default pi=>pi.registerMarkdownTransformer(()=>({invalid:true}));",
            "export default pi=>pi.registerMarkdownTransformer(md=>md+'|last');");
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        Assert.Equal(4, commands.LoadStatus().Modules.Count(module => module.HasMarkdownTransformer));
        var transform = Assert.IsType<Func<string, TuiMarkdownTransformContext, string>>(commands.CreateMarkdownTransform());
        Assert.Equal("original|assistant,true,43|last", transform("original", new("assistant", true, 43)));
        Assert.Equal("original|assistant-thinking,false,67|last", transform("original", new("assistant-thinking", false, 67)));
        Assert.Equal("original|user,false,21|last", transform("original", new("user", false, 21)));
        Assert.DoesNotContain(runner.Messages, message => message is not SystemMessage);
    }

    /// <summary>【CodingAgent】【Markdown 重载】重复帧复用转换结果，宽度改变重新转换，重载后移除旧注册与缓存。</summary>
    [Fact]
    public void MarkdownTransformers_CacheFramesAndReloadRegistrations()
    {
        using var fixture = new Fixture("""
            let factories=0;
            export default pi=>{
              factories++; let calls=0;
              pi.registerMarkdownTransformer(md=>md+'|'+factories+'|'+(++calls));
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var transform = commands.CreateMarkdownTransform()!;
        Assert.Equal("text|1|1", transform("text", new("assistant", true, 40)));
        Assert.Equal("text|1|1", transform("text", new("assistant", true, 40)));
        Assert.Equal("text|1|2", transform("text", new("assistant", true, 50)));
        Assert.Equal("text|1|3", transform("text", new("assistant", false, 50)));
        File.WriteAllText(fixture.Files[0], "export default pi=>pi.registerMarkdownTransformer(md=>'reloaded:'+md);");
        commands.Reload();
        Assert.Equal("reloaded:text", commands.CreateMarkdownTransform()!("text", new("user", false, 50)));
        Assert.Equal("reloaded:text", transform("text", new("assistant", true, 40)));
        File.WriteAllText(fixture.Files[0], "export default pi=>{};");
        commands.Reload();
        Assert.Null(commands.CreateMarkdownTransform());
    }

    /// <summary>【CodingAgent】【Markdown 空值】空字符串可以隐藏正文，不返回字符串则沿用当前正文。</summary>
    [Fact]
    public void MarkdownTransformers_AllowEmptyStringAndPreserveUndefined()
    {
        using var fixture = new Fixture("export default pi=>pi.registerMarkdownTransformer(md=>md==='hide'?'':undefined);");
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var transform = commands.CreateMarkdownTransform()!;
        Assert.Equal("", transform("hide", new("user", false, 80)));
        Assert.Equal("keep", transform("keep", new("assistant", false, 80)));
    }

    /// <summary>【CodingAgent】【Markdown 宿主】真实 Node 转换同时接入实时与恢复显示，模型与 JSONL 内容保持原文。</summary>
    /// <param name="restore">是否从已有历史恢复。</param><param name="hideThinking">是否隐藏思考。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MarkdownTransformers_HostKeepsModelAndPersistedContent(bool restore, bool hideThinking)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerMarkdownTransformer((md,ctx)=>
              'DISPLAY '+ctx.messageType+' '+ctx.isStreaming+' '+ctx.availableWidth+' '+md.toUpperCase());
            """);
        var answer = new AssistantMessage([new ThinkingContent("private plan"), new TextContent("raw answer")]);
        var (runner, provider) = fixture.CreateRunner(answer);
        provider.StreamFactory = _ =>
        {
            var stream = new AssistantMessageStream();
            stream.Push(new ThinkingDeltaEvent(0, "private plan", answer));
            stream.Push(new TextDeltaEvent(1, "raw ", answer));
            stream.Push(new TextDeltaEvent(1, "answer", answer));
            stream.Push(new DoneEvent(answer));
            return stream;
        };
        using var commands = fixture.BindCommands(runner);
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "markdown.jsonl")));
        commands.BindSession(runner, tree);
        if (restore)
        {
            runner.AppendMessage(new UserMessage("raw question"));
            runner.AppendMessage(answer);
            tree.SyncFromRunner(runner);
        }
        var terminal = new FakeTerminal();
        if (!restore) terminal.QueueInput("raw question");
        terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        var frames = new List<string>();
        ui.TranscriptChanged += () => frames.Add(string.Join("\n", TuiMessageArea.RenderMessages(ui.SnapshotMessages(), 80,
            displayOptions: new TuiMessageDisplayOptions(renderMarkdown: true))));
        await new CodingAgentHost(ui, runner, extensionCommandStore: commands, treeSessionController: tree, hideThinkingBlock: hideThinking).RunAsync();
        Assert.Contains(frames, frame => frame.Contains("DISPLAY user false 75 RAW QUESTION", StringComparison.Ordinal));
        Assert.Contains(frames, frame => frame.Contains("DISPLAY assistant false 75 RAW ANSWER", StringComparison.Ordinal));
        if (!restore) Assert.Contains(frames, frame => frame.Contains("DISPLAY assistant true 75 RAW", StringComparison.Ordinal));
        else Assert.DoesNotContain(frames, frame => frame.Contains("DISPLAY assistant true", StringComparison.Ordinal));
        Assert.Equal(!hideThinking, frames.Any(frame => frame.Contains("DISPLAY assistant-thinking", StringComparison.Ordinal)));
        Assert.Equal("raw question", Assert.IsType<TextContent>(Assert.Single(Assert.Single(runner.Messages.OfType<UserMessage>()).Content)).Text);
        Assert.Contains(ui.Transcript, entry => entry.Kind == TranscriptEntryKind.Assistant && entry.Text == "raw answer");
        Assert.DoesNotContain("DISPLAY", File.ReadAllText(tree.Store.Path));
        if (!restore) Assert.Contains(provider.Contexts[0].Messages.OfType<UserMessage>(),
            message => message.Content.OfType<TextContent>().Any(text => text.Text == "raw question"));
    }
}
