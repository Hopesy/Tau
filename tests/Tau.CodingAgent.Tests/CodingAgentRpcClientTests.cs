using System.Text.Json;
using System.Threading.Channels;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentRpcClientTests
{
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

    [Fact]
    public async Task PromptAndWaitAsync_CollectsEventsUntilAgentEnd()
    {
        var transport = new FakeRpcTransport();
        await using var client = CreateClient(transport);
        await client.StartAsync();

        var task = client.PromptAndWaitAsync("run", timeout: TimeSpan.FromSeconds(1));
        await transport.ReadSentLineAsync();
        transport.EmitStdout("""{"type":"response","id":"req_1","command":"prompt","success":true}""");
        transport.EmitStdout("""{"type":"agent_start"}""");
        transport.EmitStdout("""{"type":"agent_end"}""");

        var events = await task;
        Assert.Equal(
            ["agent_start", "agent_end"],
            events.Select(evt => evt.GetProperty("type").GetString() ?? string.Empty).ToArray());
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
