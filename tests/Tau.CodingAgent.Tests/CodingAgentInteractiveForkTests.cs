// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【双中断设置】空输入双 Esc 按设置打开树或用户消息选择，none 不打开任何选择器。</summary>
    /// <param name="action">设置的动作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("tree")]
    [InlineData("fork")]
    [InlineData("none")]
    public async Task NamedHostKeys_DoubleEscapeUsesConfiguredSelector(string action)
    {
        using var fixture = new Fixture("double-escape", "models");
        var path = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(path, "{\"doubleEscapeAction\":\"" + action + "\"}");
        var settings = new CodingAgentSettingsStore(path);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("request"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        var tree = CodingAgentTreeSessionController.OpenOrCreate(Path.Combine(fixture.Root, "session.jsonl"));
        tree.SyncFromRunner(runner);
        var reader = new DoubleEscapeReader();
        var editor = new InteractiveInputEditor(reader, new DoubleEscapeRenderer(), bindings: CodingAgentKeybindings.CreateEditorBindings(new(platform: "win32")));
        var calls = 0;
        var host = new CodingAgentHost(new InteractiveConsoleSession(new FakeTerminal(), editor), runner,
            treeSessionController: tree, settingsStore: settings, treeNavigator: (items, _, _) =>
            {
                calls++;
                if (action == "fork") Assert.All(items, item => Assert.Equal("user", item.MessageRole));
                else Assert.Contains(items, item => item.MessageRole == "assistant");
                return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1));
            }) { EditorKeyTimeProvider = new DoubleEscapeClock() };
        await host.RunAsync();
        Assert.Equal(action == "none" ? 0 : 1, calls);
        Assert.Empty(runner.Inputs);
    }

    /// <summary>【CodingAgent】【交互分支】候选只包含用户消息，选择后排除原输入并恢复草稿，取消或伪造 ID 不改变会话。</summary>
    /// <param name="mode">成功、取消或无效选择。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("select")]
    [InlineData("cancel")]
    [InlineData("invalid")]
    public async Task InteractiveFork_SelectsBeforeUserAndRestoresDraft(string mode)
    {
        using var fixture = new Fixture("interactive-fork", "models");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new UserMessage("first"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        runner.MutableMessages.Add(new UserMessage("second"));
        var tree = CodingAgentTreeSessionController.OpenOrCreate(Path.Combine(fixture.Root, "session.jsonl"));
        tree.SyncFromRunner(runner);
        var oldPath = tree.Path;
        string? draft = "unchanged";
        var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, inputDraftSetter: value => draft = value,
            treeNavigator: (items, preferred, _) =>
            {
                Assert.Equal(["first", "second"], items.Select(item => item.DisplayLine));
                Assert.All(items, item => Assert.Equal("user", item.MessageRole));
                Assert.Equal(items[1].EntryId, preferred);
                return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(mode == "cancel" ? null : mode == "invalid" ? "forged" : items[1].EntryId, 1, 1));
            });
        var result = await router.TryHandleAsync("/fork");
        Assert.Equal(mode == "invalid", result.IsError);
        if (mode == "select")
        {
            Assert.Equal("second", draft);
            Assert.Equal(2, runner.Messages.Count);
            Assert.NotEqual(oldPath, tree.Path);
        }
        else
        {
            Assert.Equal("unchanged", draft);
            Assert.Equal(3, runner.Messages.Count);
            Assert.Equal(oldPath, tree.Path);
        }
    }

    /// <summary>【CodingAgent】【双中断输入】固定提供两个 Escape 和空输入退出键。</summary>
    private sealed class DoubleEscapeReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new([new('\u001b', ConsoleKey.Escape, false, false, false),
            new('\u001b', ConsoleKey.Escape, false, false, false), new('\u0004', ConsoleKey.D, false, false, true)]);
        /// <summary>【CodingAgent】【固定输入】取下一按键。</summary><param name="cancellationToken">取消信号。</param><returns>固定按键。</returns>
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_keys.Dequeue()); }
    }

    /// <summary>【CodingAgent】【固定时间】两次 Escape 发生在同一个测试时间点。</summary>
    private sealed class DoubleEscapeClock : TimeProvider
    {
        /// <summary>【CodingAgent】【固定时间】读取确定的 UTC 时间。</summary><returns>测试时间。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1);
    }

    /// <summary>【CodingAgent】【输入渲染夹具】隔离控制台输出。</summary>
    private sealed class DoubleEscapeRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        /// <summary>【CodingAgent】【测试提示】忽略提示输出。</summary><param name="prompt">提示。</param><param name="color">颜色。</param>
        public void WritePrompt(string prompt, ConsoleColor? color = null) { }
        /// <summary>【CodingAgent】【测试文本】忽略草稿绘制。</summary><param name="text">草稿。</param><param name="cursor">光标。</param>
        public void Render(string text, int cursor) { }
        /// <summary>【CodingAgent】【测试搜索】忽略搜索绘制。</summary><param name="query">查询。</param><param name="match">匹配文本。</param><param name="cursor">光标。</param>
        public void RenderSearch(string query, string? match, int cursor) { }
        /// <summary>【CodingAgent】【测试提交】忽略提交绘制。</summary>
        public void Commit() { }
        /// <summary>【CodingAgent】【测试取消】忽略取消绘制。</summary>
        public void Cancel() { }
    }
}
