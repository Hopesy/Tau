// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【渲染注册】同模块取最后一次注册，跨模块取首个有效注册，消息与条目注册表相互独立。</summary>
    [Fact]
    public void EntryRenderers_ResolvePriorityAndPassNativeEntryOptions()
    {
        using var fixture = new Fixture("""
            let factories=0;
            export default pi=>{
              factories++;
              pi.registerMessageRenderer('state',()=>({render:()=>['message renderer']}));
              pi.registerEntryRenderer('state',()=>({render:()=>['superseded']}));
              pi.registerEntryRenderer('state',(entry,options,theme)=>({render:width=>[
                theme.bold([entry.type,entry.id,entry.parentId,entry.customType,entry.data.value,options.expanded,width,factories].join('|'))
              ]}));
            };
            """, """
            export default pi=>pi.registerEntryRenderer('state',()=>({render:()=>['later module']}));
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var status = commands.LoadStatus();
        Assert.Equal(fixture.Files[0], Assert.Single(status.EntryRenderers).FilePath);
        Assert.Single(status.MessageRenderers);
        using var document = JsonDocument.Parse("""{"type":"custom","id":"entry-id","parentId":"parent-id","customType":"state","data":{"value":"payload"},"timestamp":"2026-01-01T00:00:00Z"}""");
        Assert.True(commands.TryRenderCustomEntry(document.RootElement, out var collapsed, width: 43));
        Assert.Equal("custom|entry-id|parent-id|state|payload|false|43|1", collapsed);
        Assert.True(commands.TryRenderCustomEntry(document.RootElement, out var expanded, expanded: true, width: 67));
        Assert.Equal("custom|entry-id|parent-id|state|payload|true|67|1", expanded);
    }

    /// <summary>【CodingAgent】【渲染隔离】空组件和无注册隐藏条目，异常转换为可读错误且后续渲染仍可用。</summary>
    /// <param name="type">渲染器类型。</param><param name="visible">是否有可见内容。</param>
    [Theory]
    [InlineData("missing", false)]
    [InlineData("empty", false)]
    [InlineData("failing", true)]
    public void EntryRenderers_HideEmptyAndIsolateFailures(string type, bool visible)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerEntryRenderer('empty',()=>undefined);
              pi.registerEntryRenderer('failing',()=>{throw Error('expected renderer failure')});
              pi.registerEntryRenderer('good',()=>({render:()=>['still available']}));
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        using var document = JsonDocument.Parse("{\"type\":\"custom\",\"id\":\"entry\",\"customType\":\"" + type + "\"}");
        Assert.Equal(visible, commands.TryRenderCustomEntry(document.RootElement, out var text));
        if (visible) Assert.Contains("[failing] renderer failed: expected renderer failure", text);
        using var good = JsonDocument.Parse("""{"type":"custom","id":"good","customType":"good"}""");
        Assert.True(commands.TryRenderCustomEntry(good.RootElement, out var next));
        Assert.Equal("still available", next);
    }

    /// <summary>【CodingAgent】【渲染重载】无效注册提供诊断，文件重载移除旧注册并启用新渲染器。</summary>
    [Fact]
    public void EntryRenderers_ReloadClearsPreviousRegistrationAndInvalidDefinitions()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerEntryRenderer('',()=>undefined);
              pi.registerEntryRenderer('bad',123);
              pi.registerEntryRenderer('old',()=>({render:()=>['old text']}));
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var status = commands.LoadStatus();
        Assert.Equal(2, status.Diagnostics.Count(item => item.Message.Contains("entry renderer requires", StringComparison.Ordinal)));
        Assert.Equal("old", Assert.Single(status.EntryRenderers).CustomType);
        File.WriteAllText(fixture.Files[0], "export default pi=>pi.registerEntryRenderer('new',()=>({render:()=>['new text']}));");
        var reloaded = commands.Reload();
        Assert.Equal("new", Assert.Single(reloaded.EntryRenderers).CustomType);
        Assert.DoesNotContain(reloaded.Diagnostics, item => item.Message.Contains("entry renderer requires", StringComparison.Ordinal));
    }

    /// <summary>【CodingAgent】【条目显示】真实 appendEntry 通知和历史恢复都调用渲染器，元数据不进入模型消息。</summary>
    /// <param name="restore">是否先保存条目再启动终端。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryRenderers_DisplayLiveAndRestoredEntriesWithoutAddingMessages(bool restore)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerEntryRenderer('state',(entry,{expanded})=>({render:width=>['entry display '+entry.data.value+' '+expanded+' '+width]}));
              pi.registerCommand('append-state',{handler:()=>pi.appendEntry('state',{value:'persisted'})});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        if (restore)
        {
            var result = fixture.Runtime.Invoke(fixture.Files[0], "append-state", "");
            Assert.True(result.Success, result.Error);
            await runner.WaitForStateNotificationsAsync();
        }
        var terminal = new FakeTerminal();
        if (!restore) terminal.QueueInput("/append-state");
        terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        await new CodingAgentHost(ui, runner, extensionCommandStore: commands).RunAsync();
        var displayed = Assert.Single(ui.Transcript, item => item.Key?.StartsWith("entry:", StringComparison.Ordinal) == true);
        Assert.Equal("entry display persisted false 80", displayed.Text);
        Assert.DoesNotContain(runner.Messages, message => message is not Tau.Ai.SystemMessage);
        Assert.Empty(provider.Contexts);
    }
}
