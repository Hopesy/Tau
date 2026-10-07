// 作者：xxx
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【打印会话回归】覆盖多轮提示、提交时机和中断后的历史恢复。</summary>
public sealed class CodingAgentPrintSessionTests
{
    /// <summary>多轮共享运行器，文本只输出末轮，JSON 保留所有轮次。</summary>
    /// <param name="json">是否输出 JSON。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sequence_PreservesOrderAndOutputContract(bool json)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input));
        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(["first", "second", "third"], [], "stdin:");
        var mode = new CodingAgentPrintMode(runner, output, error, json);
        Assert.Equal(0, await mode.RunAsync(prompt, ["second", "third"]));
        Assert.Equal(["stdin:first", "second", "third"], runner.Inputs);
        Assert.Empty(error.ToString());
        if (!json) Assert.Equal("third" + Environment.NewLine, output.ToString());
        else
        {
            var events = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).ToArray();
            Assert.Equal(3, events.Count(item => item.GetProperty("type").GetString() == "agent_end"));
            Assert.Equal(3, events.Count(item => item.GetProperty("type").GetString() == "message_end"));
        }
    }

    /// <summary>图片只随初始提示提交，后续文本不会重新附带图片。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sequence_AttachesImagesOnlyToInitialPrompt()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input));
        var picture = new ImageContent("aW1hZ2U=", "image/png");
        var mode = new CodingAgentPrintMode(runner, output, error);
        Assert.Equal(0, await mode.RunAsync(new CodingAgentInitialPrompt("", [picture]), ["describe again"]));
        Assert.Equal(picture, Assert.Single(Assert.Single(runner.ContentInputs).OfType<ImageContent>()));
        Assert.Equal("describe again", Assert.Single(runner.Inputs));
    }

    /// <summary>首个空位置参数未生成初始提示时仍执行后续参数。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sequence_AllowsRemainingMessagesWithoutInitialPrompt()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input));
        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(["", "second"], []);
        Assert.Null(prompt);
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, error).RunAsync(prompt, ["second"]));
        Assert.Equal("second", Assert.Single(runner.Inputs));
    }

    /// <summary>与上游逐条提示一致，中间请求错误不阻止后续显式提示，最终状态决定文本输出。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sequence_LastPromptCanRecoverFromEarlierProviderError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input, input == "first" ? StopReason.Error : StopReason.EndTurn));
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, error)
            .RunAsync(new CodingAgentInitialPrompt("first", []), ["retry"]));
        Assert.Equal("retry" + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
    }

    /// <summary>每轮完成后可读回历史，反复同步和第二个控制器恢复都不重复追加。</summary>
    /// <param name="tree">是否使用 JSONL 树会话。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_CheckpointsAndResumesWithoutDuplicateMessages(bool tree)
    {
        using var fixture = new SessionFixture(tree);
        using var output = new StringWriter();
        using var error = new StringWriter();
        FakeCodingAgentRunner runner = null!;
        runner = new FakeCodingAgentRunner((input, _) => PersistedReply(input)) { SessionName = "test session" };
        Assert.Equal(0, await fixture.Mode(runner, output, error)
            .RunAsync(new CodingAgentInitialPrompt("first", []), ["second"]));
        Assert.Equal(4, fixture.Load().Messages.Count);
        Assert.Equal("test session", fixture.Load().Name);
        Assert.Equal(runner.Model.Id, fixture.Load().Model);
        Assert.Equal(runner.Model.Provider, fixture.Load().Provider);

        // 1. 【CodingAgent】【会话恢复】重新创建控制器和运行器，模拟下一次进程启动
        fixture.Reopen();
        var restored = fixture.Load();
        runner = new FakeCodingAgentRunner((input, _) => PersistedReply(input));
        runner.RestoreSession(restored);
        Assert.Equal(0, await fixture.Mode(runner, output, error).RunAsync("third"));
        var messages = fixture.Load().Messages;
        Assert.Equal(6, messages.Count);
        Assert.Equal(["first", "second", "third"], messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));

        /// <summary>在消息提交、下一轮启动两个边界核对磁盘快照。</summary>
        /// <param name="input">当前提示。</param>
        /// <returns>已提交消息的事件。</returns>
        async IAsyncEnumerable<AgentEvent> PersistedReply(string input)
        {
            Assert.Equal(runner.Messages.Count, fixture.Load().Messages.Count);
            var user = new UserMessage(input);
            runner.MutableMessages.Add(user);
            yield return new MessageEndEvent(user);
            Assert.Equal(runner.Messages.Count, fixture.Load().Messages.Count);
            var assistant = new AssistantMessage([new TextContent("answer:" + input)]);
            runner.MutableMessages.Add(assistant);
            yield return new MessageEndEvent(assistant);
            Assert.Equal(runner.Messages.Count, fixture.Load().Messages.Count);
            yield return new AgentEndEvent(messages: [user, assistant]);
            await Task.CompletedTask;
        }
    }

    /// <summary>助手错误、取消及枚举异常都保留已提交内容，失败不输出半成品。</summary>
    /// <param name="tree">是否使用 JSONL 树会话。</param>
    /// <param name="failure">失败类型。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "exception")]
    [InlineData(true, "exception")]
    public async Task Persistence_PreservesCommittedHistoryOnFailure(bool tree, string failure)
    {
        using var fixture = new SessionFixture(tree);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancel = new CancellationTokenSource();
        FakeCodingAgentRunner runner = null!;
        runner = new FakeCodingAgentRunner((_, ct) => Fail(ct));
        var result = await fixture.Mode(runner, output, error)
            .RunAsync(new CodingAgentInitialPrompt("first", []), ["second"], cancel.Token);
        Assert.Equal(1, result);
        Assert.Empty(output.ToString());
        if (failure != "error") Assert.Single(runner.Inputs);
        var messages = fixture.Load().Messages;
        Assert.NotEmpty(messages);
        Assert.Equal(runner.Messages.Count, messages.Count);
        Assert.Equal("partial", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(messages[^1]).Content[0]).Text);

        /// <summary>提交部分回复后模拟终态错误，异常时在释放阶段提交最后消息。</summary>
        /// <param name="ct">测试取消信号。</param>
        /// <returns>失败事件。</returns>
        async IAsyncEnumerable<AgentEvent> Fail([EnumeratorCancellation] CancellationToken ct)
        {
            var user = new UserMessage("first");
            runner.MutableMessages.Add(user);
            yield return new MessageEndEvent(user);
            var assistant = new AssistantMessage([new TextContent("partial")])
            {
                StopReason = failure == "cancel" ? StopReason.Aborted : StopReason.Error,
                ErrorMessage = "provider failed"
            };
            try
            {
                if (failure == "exception") throw new IOException("stream failed");
                runner.MutableMessages.Add(assistant);
                yield return new MessageEndEvent(assistant);
                if (failure == "cancel") await cancel.CancelAsync();
                yield return new AgentEndEvent("provider failed", [user, assistant]);
            }
            finally
            {
                if (failure == "exception") runner.MutableMessages.Add(assistant);
            }
        }
    }

    /// <summary>落盘失败返回非零且不打印成功正文，也不执行后续提示。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Persistence_WriteFailureIsNotReportedAsSuccess()
    {
        using var fixture = new SessionFixture(false);
        File.WriteAllText(fixture.Path + ".tmp", "occupied");
        using var locked = new FileStream(fixture.Path + ".tmp", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input));
        Assert.Equal(1, await fixture.Mode(runner, output, error)
            .RunAsync(new CodingAgentInitialPrompt("first", []), ["second"]));
        Assert.Single(runner.Inputs);
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    /// <summary>启动前取消不创建会话文件、不调用运行器。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Persistence_PreCancelledRunDoesNotSave()
    {
        using var fixture = new SessionFixture(false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((input, _) => Reply(input));
        Assert.Equal(1, await fixture.Mode(runner, output, error).RunAsync("first", new CancellationToken(true)));
        Assert.Empty(runner.Inputs);
        Assert.False(File.Exists(fixture.Path));
    }

    /// <summary>提供只含最终回复的异步事件。</summary>
    /// <param name="text">正文。</param>
    /// <param name="reason">助手终态。</param>
    /// <returns>消息完成及运行完成事件。</returns>
    private static async IAsyncEnumerable<AgentEvent> Reply(string text, StopReason reason = StopReason.EndTurn)
    {
        var assistant = new AssistantMessage([new TextContent(text)]) { StopReason = reason };
        yield return new MessageEndEvent(assistant);
        yield return new AgentEndEvent(messages: [assistant]);
        await Task.CompletedTask;
    }

    /// <summary>【CodingAgent】【会话回归】管理单个测试的平面或树会话文件。</summary>
    private sealed class SessionFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-print-session-" + Guid.NewGuid().ToString("N"));
        private readonly bool _tree;
        private CodingAgentSessionStore? _flat;
        private CodingAgentTreeSessionController? _controller;
        public string Path => System.IO.Path.Combine(_root, _tree ? "session.jsonl" : "session.json");

        /// <summary>创建独立临时目录并打开对应存储。</summary>
        /// <param name="tree">是否使用树会话。</param>
        public SessionFixture(bool tree)
        {
            _tree = tree;
            Directory.CreateDirectory(_root);
            Reopen();
        }

        /// <summary>重新打开存储以验证磁盘恢复。</summary>
        public void Reopen()
        {
            if (_tree) _controller = CodingAgentTreeSessionController.OpenOrCreate(Path);
            else _flat = new CodingAgentSessionStore(Path);
        }

        /// <summary>读取磁盘快照。</summary>
        /// <returns>包含消息和模型的会话快照。</returns>
        public CodingAgentSessionSnapshot Load() => _flat?.Load() ?? _controller!.Store.LoadCurrentBranchSnapshot().ToFlatSnapshot();

        /// <summary>创建绑定存储的打印入口。</summary>
        /// <param name="runner">测试运行器。</param>
        /// <param name="output">标准输出。</param>
        /// <param name="error">错误输出。</param>
        /// <returns>打印入口。</returns>
        public CodingAgentPrintMode Mode(ICodingAgentRunner runner, TextWriter output, TextWriter error) =>
            new(runner, output, error, false, _flat, _controller);

        /// <summary>删除当前测试创建的文件和空目录。</summary>
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_root)) File.Delete(file);
            Directory.Delete(_root);
        }
    }
}
