// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【RPC 生成中命令】命令响应可以先完成，但记录在模型中止回答之后写入会话。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RpcBash_DefersHistoryUntilActiveModelEnds()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>({result:{output:'during model',exitCode:0,cancelled:false,truncated:false}}));");
        var (runner, provider) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            started.TrySetResult(); return stream;
        };
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"prompt","type":"prompt","message":"hold"}""");
        await started.Task.WaitAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"b1","type":"bash","command":"remote"}""");
        var response = await output.WaitAsync(line => line.GetProperty("type").GetString() == "response" && line.GetProperty("id").GetString() == "b1", deadline.Token);
        Assert.True(response.GetProperty("success").GetBoolean()); Assert.True(runner.HasPendingBashMessages);
        Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.True(runner.IsStreaming);
        input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort"}""");
        await output.WaitAsync(line => line.GetProperty("type").GetString() == "agent_settled", deadline.Token);
        var messages = runner.Messages.ToList();
        Assert.True(messages.FindIndex(message => message is AssistantMessage) < messages.FindIndex(message => message is AgentBashExecutionMessage));
        Assert.Equal("during model", Assert.Single(messages.OfType<AgentBashExecutionMessage>()).Output);
        input.Lines.Writer.TryComplete(); await running.WaitAsync(deadline.Token);
    }

    /// <summary>【CodingAgent】【RPC 命令结果】直接结果和远程执行都记录会话，!! 标记保留，输出事件采用原生字段。</summary>
    /// <param name="remote">是否使用自定义后端。</param><param name="excluded">是否排除模型上下文。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task RpcBash_RecordsHookResultAndStreamsNativeEvents(bool remote, bool excluded)
    {
        using var fixture = new Fixture(remote
            ? "export default pi=>pi.on('user_bash',()=>({operations:{exec:async(command,cwd,{onData})=>{onData(Buffer.from('中文😀'));onData(Buffer.from('done'));return {exitCode:3};}}}));"
            : "export default pi=>pi.on('user_bash',()=>({result:{output:'result',exitCode:7,cancelled:false,truncated:true,fullOutputPath:'saved.txt'}}));");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite(new JsonObject { ["id"] = "b1", ["type"] = "bash", ["command"] = "remote", ["excludeFromContext"] = excluded }.ToJsonString());
        var lines = new List<JsonElement>();
        var response = await output.WaitAsync(line => { lines.Add(line); return line.GetProperty("type").GetString() == "response" && line.GetProperty("id").GetString() == "b1"; }, deadline.Token);
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data"); Assert.Equal(remote ? "中文😀done" : "result", data.GetProperty("output").GetString());
        var updates = lines.Where(line => line.GetProperty("type").GetString() == "bash_execution_update").ToArray();
        Assert.Equal(remote ? 2 : 0, updates.Length); Assert.All(updates, update => Assert.Equal("b1", update.GetProperty("id").GetString()));
        if (remote) Assert.Equal("中文😀done", string.Concat(updates.Select(update => update.GetProperty("delta").GetString())));
        Assert.DoesNotContain(lines, line => line.GetProperty("type").GetString() is "bash_event" or "bash_output");
        var message = Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>());
        Assert.Equal(excluded, message.ExcludeFromContext); Assert.Equal(remote ? 3 : 7, message.ExitCode);
        Assert.Equal(excluded ? 0 : 1, AgentHarnessMessages.ConvertToLlm([message]).Count);
        input.Lines.Writer.TryComplete(); Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }

    /// <summary>【CodingAgent】【RPC 拒绝执行】扩展错误返回失败，不能执行命令或写入成功记录。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RpcBash_RejectingHookDoesNotExecuteOrRecord()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>{throw Error('policy rejection');});");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"b1","type":"bash","command":"must-not-execute"}""");
        var response = await output.WaitAsync(line => line.GetProperty("type").GetString() == "response" && line.GetProperty("id").GetString() == "b1", deadline.Token);
        Assert.False(response.GetProperty("success").GetBoolean()); Assert.Contains("policy rejection", response.GetProperty("error").GetString());
        Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.False(runner.IsBashRunning);
        input.Lines.Writer.TryComplete(); await running.WaitAsync(deadline.Token);
    }

    /// <summary>【CodingAgent】【RPC 并行取消】两个请求同时产生增量，abort_bash 取消全部命令并分别返回保留输出的结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RpcBash_ConcurrentCommandsHaveIndependentIdsAndSharedAbort()
    {
        using var fixture = new Fixture("""
            export default pi=>pi.on('user_bash',()=>({operations:{exec:async(command,cwd,{onData,signal})=>{
              onData(Buffer.from(command));await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));return {exitCode:0};
            }}}));
            """);
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"first","type":"bash","command":"first"}""");
        input.Lines.Writer.TryWrite("""{"id":"second","type":"bash","command":"second"}""");
        var started = new HashSet<string>();
        await output.WaitAsync(line =>
        {
            if (line.GetProperty("type").GetString() == "bash_execution_update")
            { Assert.Equal(line.GetProperty("id").GetString(), line.GetProperty("delta").GetString()); started.Add(line.GetProperty("id").GetString()!); }
            return started.Count == 2;
        }, deadline.Token);
        Assert.True(runner.IsBashRunning);
        input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort_bash"}""");
        var responses = new Dictionary<string, JsonElement>();
        await output.WaitAsync(line =>
        {
            if (line.GetProperty("type").GetString() == "response") responses[line.GetProperty("id").GetString()!] = line;
            return responses.Count == 3;
        }, deadline.Token);
        foreach (var id in started)
        {
            var response = responses[id]; Assert.True(response.GetProperty("success").GetBoolean());
            Assert.True(response.GetProperty("data").GetProperty("cancelled").GetBoolean());
            Assert.Equal(id, response.GetProperty("data").GetProperty("output").GetString());
        }
        Assert.False(runner.IsBashRunning); Assert.Equal(2, runner.Messages.OfType<AgentBashExecutionMessage>().Count());
        input.Lines.Writer.TryComplete(); await running.WaitAsync(deadline.Token);
    }

    /// <summary>【CodingAgent】【RPC 输入结束】有限命令在 stdin 结束后仍可完成，Node 运行时在结果返回之后才重置。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RpcBash_EndOfInputWaitsForExtensionExecution()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>({operations:{exec:async(_,__,{onData})=>{await new Promise(resolve=>setTimeout(resolve,30));onData(Buffer.from('finished'));return {exitCode:0};}}}));");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        using var input = new StringReader("""{"id":"b1","type":"bash","command":"finite"}"""); using var output = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands).RunAsync(deadline.Token));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(text => { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }).ToArray();
        var response = Assert.Single(lines, line => line.GetProperty("type").GetString() == "response"); Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal("finished", Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Output);
    }
}
