using System.Text.Json;
using System.Threading.Channels;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentRpcClientTests
{
    /// <summary>【CodingAgent】【RPC 接收结果】三个输入方法返回服务端的明确处理方式。</summary>
    /// <param name="command">输入命令。</param><param name="disposition">原生处理方式。</param><returns>异步协议测试。</returns>
    [Theory]
    [InlineData("prompt", "handled")] [InlineData("prompt", "queued")] [InlineData("prompt", "started")]
    [InlineData("steer", "handled")] [InlineData("steer", "queued")]
    [InlineData("follow_up", "handled")] [InlineData("follow_up", "queued")]
    public async Task InputMethods_ReturnDisposition(string command, string disposition)
    {
        var transport = new FakeRpcTransport(); await using var client = CreateClient(transport); await client.StartAsync();
        var result = command switch
        {
            "prompt" => ReadPromptAsync(),
            "steer" => ReadQueuedAsync(true),
            _ => ReadQueuedAsync(false)
        };
        await transport.ReadSentLineAsync();
        transport.EmitStdout(new System.Text.Json.Nodes.JsonObject { ["type"] = "response", ["id"] = "req_1", ["command"] = command,
            ["success"] = true, ["data"] = new System.Text.Json.Nodes.JsonObject { ["disposition"] = disposition } }.ToJsonString());
        Assert.Equal(disposition, await result);
        /// <summary>【CodingAgent】【测试提示】统一返回文本便于断言。</summary><returns>处理方式文本。</returns>
        async Task<string> ReadPromptAsync() => (await client.PromptAsync("input")).ToString().ToLowerInvariant();
        /// <summary>【CodingAgent】【测试队列】统一两个队列方法的返回类型。</summary><param name="steer">是否引导。</param><returns>处理方式文本。</returns>
        async Task<string> ReadQueuedAsync(bool steer) => (steer ? await client.SteerAsync("input") : await client.FollowUpAsync("input")).ToString().ToLowerInvariant();
    }

    /// <summary>【CodingAgent】【已处理提示】扩展接管不产生结算事件时，组合等待立即完成且后续事件不修改返回快照。</summary>
    /// <returns>异步协议测试。</returns>
    [Fact]
    public async Task PromptAndWait_HandledDoesNotWaitForSettled()
    {
        var transport = new FakeRpcTransport(); await using var client = CreateClient(transport); await client.StartAsync();
        var pending = client.PromptAndWaitAsync("handled", timeout: TimeSpan.FromSeconds(10));
        await transport.ReadSentLineAsync();
        transport.EmitStdout("""{"type":"entry_appended","entry":{"type":"custom"}}""");
        transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":true,"data":{"disposition":"handled"}}""");
        var events = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Single(events);
        transport.EmitStdout("""{"type":"agent_settled"}""");
        Assert.Single(events); Assert.Equal(0, client.PendingRequestCount);
    }

    /// <summary>【CodingAgent】【等待失败】失败、无效处理结果或调用方取消都及时结束整个组合等待。</summary>
    /// <param name="failure">故障种类。</param><returns>异步协议测试。</returns>
    [Theory] [InlineData("error")] [InlineData("invalid")] [InlineData("cancel")]
    public async Task PromptAndWait_PreflightFailureDoesNotLeavePendingRequest(string failure)
    {
        var transport = new FakeRpcTransport(); await using var client = CreateClient(transport); await client.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = client.PromptAndWaitAsync("input", cancellationToken: cancellation.Token);
        await transport.ReadSentLineAsync();
        if (failure == "cancel") { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else if (failure == "error")
        {
            transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":false,"error":"fixture failure"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }
        else
        {
            transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":true,"data":{"disposition":"unexpected"}}""");
            await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        }
        Assert.Equal(0, client.PendingRequestCount);
    }
    [Fact]
    public async Task PromptAsync_WritesJsonlAndDispatchesNonResponseEvents()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        var events = new List<JsonElement>();
        using var subscription = client.OnEvent(evt => events.Add(evt));
        await client.StartAsync();

        var promptTask = client.PromptAsync(
            "hello",
            [new ImageContent("aGVsbG8=", "image/png")],
            streamingBehavior: "steer");
        var sent = await transport.ReadSentLineAsync();

        using var request = JsonDocument.Parse(sent);
        Assert.Equal("req_1", request.RootElement.GetProperty("id").GetString());
        Assert.Equal("prompt", request.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", request.RootElement.GetProperty("message").GetString());
        Assert.Equal("steer", request.RootElement.GetProperty("streamingBehavior").GetString());
        var image = Assert.Single(request.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("aGVsbG8=", image.GetProperty("data").GetString());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());

        transport.EmitStdout("""{"type":"message_update","value":1}""");
        transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":true}""");

        await promptTask;
        var evt = Assert.Single(events);
        Assert.Equal("message_update", evt.GetProperty("type").GetString());
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task SendAsync_FailsTypedHelpersOnErrorResponse()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();

        var task = client.GetStateAsync();
        await transport.ReadSentLineAsync();
        transport.EmitStdout("""{"type":"response","id":"req_1","command":"get_state","success":false,"error":"bad state"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("bad state", error.Message);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task BashAsync_DeserializesResultData()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();

        var task = client.BashAsync("pwd");
        var sent = await transport.ReadSentLineAsync();
        using (var request = JsonDocument.Parse(sent))
        {
            Assert.Equal("bash", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("pwd", request.RootElement.GetProperty("command").GetString());
        }

        transport.EmitStdout(
            """
            {"type":"response","id":"req_1","command":"bash","success":true,"data":{"output":"ok\n","exitCode":0,"cancelled":false,"truncated":true,"fullOutputPath":"out.log"}}
            """);

        var result = await task;
        Assert.Equal("ok\n", result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.True(result.Truncated);
        Assert.Equal("out.log", result.FullOutputPath);
    }

    /// <summary>【CodingAgent】【RPC 结算】提示事件收集跨过多次 agent_end，直到最终结算才完成</summary>
    /// <returns>异步协议验证任务</returns>
    [Fact]
    public async Task PromptAndWaitAsync_CollectsEventsUntilAgentSettled()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();

        var task = client.PromptAndWaitAsync("run", timeout: TimeSpan.FromSeconds(1));
        await transport.ReadSentLineAsync();
        transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":true}""");
        transport.EmitStdout("""{"type":"agent_start"}""");
        transport.EmitStdout("""{"type":"agent_end"}""");
        Assert.False(task.IsCompleted);
        transport.EmitStdout("""{"type":"agent_start"}""");
        transport.EmitStdout("""{"type":"agent_end"}""");
        transport.EmitStdout("""{"type":"entry_appended","entry":{"type":"custom"}}""");
        transport.EmitStdout("""{"type":"agent_settled"}""");

        var events = await task;
        Assert.Equal(
            ["agent_start", "agent_end", "agent_start", "agent_end", "entry_appended", "agent_settled"],
            events.Select(evt => evt.GetProperty("type").GetString() ?? string.Empty).ToArray());
    }

    /// <summary>【CodingAgent】【RPC 结算】独立空闲等待忽略尝试结束和重试事件</summary>
    /// <returns>异步协议验证任务</returns>
    [Fact]
    public async Task WaitForIdleAsync_RequiresAgentSettled()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();
        var task = client.WaitForIdleAsync(TimeSpan.FromSeconds(1));
        transport.EmitStdout("""{"type":"agent_end","willRetry":true}""");
        transport.EmitStdout("""{"type":"auto_retry_end","success":true}""");
        transport.EmitStdout("""{"type":"agent_end"}""");
        Assert.False(task.IsCompleted);
        transport.EmitStdout("""{"type":"agent_settled"}""");
        await task;
    }

    [Fact]
    public async Task WaitForIdleAsync_TimesOutWithCollectedStderr()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();
        transport.EmitStderr("stderr boom");

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));

        Assert.Contains("stderr boom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_RejectsPendingRequests()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();

        var task = client.GetStateAsync();
        await transport.ReadSentLineAsync();
        await client.StopAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("RPC client stopped.", error.Message);
        Assert.True(transport.StopCalled);
        Assert.True(transport.DisposeCalled);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public void CreateStartInfo_AddsRpcModeProviderModelArgumentsAndEnvironment()
    {
        var options = new CodingAgentRpcClientOptions
        {
            FileName = "dotnet",
            WorkingDirectory = @"C:\work",
            ProcessArguments = ["run", "--project", "Tau.CodingAgent.csproj"],
            Provider = "openai",
            Model = "gpt-5.4",
            AgentArguments = ["--no-context-files"],
            Environment = new Dictionary<string, string?>
            {
                ["TAU_RPC_CLIENT_TEST_KEEP"] = "value",
                ["TAU_RPC_CLIENT_TEST_REMOVE"] = null
            }
        };

        var startInfo = CodingAgentRpcProcessTransport.CreateStartInfo(options);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(@"C:\work", startInfo.WorkingDirectory);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            [
                "run",
                "--project",
                "Tau.CodingAgent.csproj",
                "--mode",
                "rpc",
                "--provider",
                "openai",
                "--model",
                "gpt-5.4",
                "--no-context-files"
            ],
            startInfo.ArgumentList.ToArray());
        Assert.Equal("value", startInfo.Environment["TAU_RPC_CLIENT_TEST_KEEP"]);
        Assert.False(startInfo.Environment.ContainsKey("TAU_RPC_CLIENT_TEST_REMOVE"));
    }

    private static CodingAgentRpcClient CreateClient(FakeRpcTransport transport) =>
        new(
            transport,
            new CodingAgentRpcClientOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(1),
                StartupDelay = TimeSpan.Zero
            });

    private sealed class FakeRpcTransport : ICodingAgentRpcTransport
    {
        private readonly Channel<string> _sentLines = Channel.CreateUnbounded<string>();

        public event Action<string>? StdoutLineReceived;

        public event Action<string>? StderrReceived;

        public bool HasExited { get; private set; }

        public int? ExitCode => HasExited ? 0 : null;

        public bool StartCalled { get; private set; }

        public bool StopCalled { get; private set; }

        public bool DisposeCalled { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task SendLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _sentLines.Writer.TryWrite(line);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            HasExited = true;
            _sentLines.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }

        public async Task<string> ReadSentLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            return await _sentLines.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }

        public void EmitStdout(string line) => StdoutLineReceived?.Invoke(line);

        public void EmitStderr(string text) => StderrReceived?.Invoke(text);
    }
}
