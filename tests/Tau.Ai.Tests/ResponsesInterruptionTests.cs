// 作者：xxx
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 中断回归】取消和超时必须保留已有内容，且不得把未完成工具视为成功。</summary>
public sealed class ResponsesInterruptionTests
{
    private static readonly string[] Prefix =
    [
        """{"type":"response.created","response":{"id":"resp_partial","model":"actual-model","usage":{"input_tokens":10,"output_tokens":2}}}""",
        """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","content":[{"type":"reasoning_text","text":"kept reasoning"}],"encrypted_content":"signature"}}""",
        """{"type":"response.output_item.added","output_index":1,"item":{"id":"msg_1","type":"message"}}""",
        """{"type":"response.output_text.delta","output_index":1,"delta":"kept text"}""",
        """{"type":"response.output_item.added","output_index":2,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"lookup","arguments":""}}""",
        """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"value\":\"kept\"}"}"""
    ];

    /// <summary>覆盖三个 SSE Provider 与 Codex WebSocket 的取消和超时。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <param name="timeout">是否用请求超时触发中断。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("responses", false)]
    [InlineData("azure", false)]
    [InlineData("codex", false)]
    [InlineData("codex-ws", false)]
    [InlineData("responses", true)]
    [InlineData("azure", true)]
    [InlineData("codex", true)]
    [InlineData("codex-ws", true)]
    public async Task Interruption_RetainsContentAndMetadata(string protocol, bool timeout)
    {
        using var cancel = new CancellationTokenSource();
        using var input = new HangingStream(string.Concat(Prefix.Select(item => "data: " + item + "\n\n")));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(input) });
        using var client = new HttpClient(handler);
        var socket = new HangingSocket();
        IStreamProvider provider = protocol switch
        {
            "azure" => new AzureOpenAiResponsesProvider(client),
            "codex" or "codex-ws" => new OpenAiCodexResponsesProvider(client, socket),
            _ => new OpenAiResponsesProvider(client)
        };
        using var lifetime = provider as IDisposable;
        var model = new Model { Id = "requested-model", Name = "Interruption", Provider = "test", Api = provider.Api, BaseUrl = "https://example.invalid/v1" };
        StreamOptions options = protocol == "azure" ? new AzureOpenAiResponsesOptions { ApiKey = "key" } : new StreamOptions
        {
            ApiKey = protocol.StartsWith("codex", StringComparison.Ordinal) ? OpenAiResponsesSharedTests.BuildFakeJwt("interrupt") : "key"
        };
        options = options with { Signal = cancel.Token, MaxRetries = 0, Timeout = timeout ? TimeSpan.FromSeconds(1) : null,
            Transport = protocol == "codex-ws" ? StreamTransport.WebSocket : StreamTransport.Sse };
        var stream = provider.Stream(model, new(null, [new UserMessage("synthetic")], null), options);
        var collected = new List<StreamEvent>();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in stream.WithCancellation(watchdog.Token))
        {
            collected.Add(item);
            if (!timeout && item is ToolCallDeltaEvent) cancel.Cancel();
        }

        var result = await stream.ResultAsync;
        Assert.Equal(timeout ? StopReason.Error : StopReason.Aborted, result.StopReason);
        Assert.Equal(model.Api, result.Api);
        Assert.Equal(model.Provider, result.Provider);
        Assert.Equal(model.Id, result.Model);
        Assert.Equal("actual-model", result.ResponseModel);
        Assert.Equal("resp_partial", result.ResponseId);
        Assert.Equal(new Usage(10, 2), result.Usage);
        Assert.Equal("kept text", Assert.Single(result.Content.OfType<TextContent>()).Text);
        var thinking = Assert.Single(result.Content.OfType<ThinkingContent>());
        Assert.Equal("kept reasoning", thinking.Thinking);
        Assert.Contains("signature", thinking.ThinkingSignature!);
        var call = Assert.Single(result.Content.OfType<ToolCallContent>());
        Assert.Equal("lookup", call.Name);
        using var arguments = JsonDocument.Parse(call.Arguments);
        Assert.Equal("kept", arguments.RootElement.GetProperty("value").GetString());
        Assert.Empty(collected.OfType<ToolCallEndEvent>());
        Assert.Empty(collected.OfType<DoneEvent>());
        var error = Assert.Single(collected.OfType<ErrorEvent>());
        Assert.Same(result, error.Message);
        if (timeout) Assert.Contains(protocol == "codex-ws" ? "idle timeout" : "timed out", error.Error);
        else Assert.Equal("Request was aborted", error.Error);
        if (protocol == "codex-ws") await socket.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>验证枚举器因取消直接结束时仍报告取消，并阻止成功连接回收回调。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task JsonCancellation_WithSilentEnd_IsAborted()
    {
        using var cancel = new CancellationTokenSource();
        var stream = new AssistantMessageStream();
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAiResponsesShared.ProcessResponsesJsonEventsAsync(
            CancelAndEnd(cancel), new AssistantMessage(), stream, beforeDone: () => callbacks++, cancellationToken: cancel.Token));
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Aborted, result.StopReason);
        Assert.Equal("kept text", Assert.Single(result.Content.OfType<TextContent>()).Text);
        Assert.Equal(0, callbacks);
    }

    /// <summary>验证 SSE 在事件边界遇到取消后直接结束时不会被误判为断流错误。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task SseCancellation_AtBufferedBoundary_IsAborted()
    {
        using var cancel = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Prefix.Select(item => "data: " + item + "\n\n"))));
        var stream = new AssistantMessageStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAiResponsesShared.ProcessResponsesStreamAsync(
            input, new AssistantMessage(), stream, eventMapper: json =>
            {
                if (json == Prefix[^1]) cancel.Cancel();
                return json;
            }, cancellationToken: cancel.Token));
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Aborted, result.StopReason);
        Assert.Equal("kept text", Assert.Single(result.Content.OfType<TextContent>()).Text);
    }

    /// <summary>在产出前缀后取消并直接返回，模拟不抛异常的传输枚举器。</summary>
    /// <param name="cancel">主动取消源。</param>
    /// <returns>供应商 JSON 事件。</returns>
    private static async IAsyncEnumerable<string> CancelAndEnd(CancellationTokenSource cancel)
    {
        foreach (var item in Prefix) yield return item;
        await cancel.CancelAsync();
    }

    /// <summary>【AI】【Responses 中断回归】提供部分 SSE 后等待取消，模拟响应正文卡住。</summary>
    /// <param name="prefix">中断前的 SSE 数据。</param>
    private sealed class HangingStream(string prefix) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        /// <summary>先读取已知内容，随后等待取消信号。</summary>
        /// <param name="buffer">接收缓冲区。</param>
        /// <param name="cancellationToken">真实请求的取消信号。</param>
        /// <returns>读取字节数。</returns>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>【AI】【Responses 中断回归】发送部分事件后阻塞的 WebSocket 连接。</summary>
    private sealed class HangingSocket : ICodexWebSocketTransport, ICodexWebSocketConnection
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>返回模拟连接。</summary>
        /// <param name="url">连接地址。</param>
        /// <param name="headers">请求头。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已建立连接。</returns>
        public Task<ICodexWebSocketConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) => Task.FromResult<ICodexWebSocketConnection>(this);

        /// <summary>接受测试请求，不访问外网。</summary>
        /// <param name="text">请求 JSON。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>逐条发送已知事件，然后等待取消或空闲超时。</summary>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>供应商事件序列。</returns>
        public async IAsyncEnumerable<string> ReadTextMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in Prefix) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        /// <summary>记录异常连接关闭，避免取消后的连接被继续复用。</summary>
        /// <param name="statusCode">关闭状态。</param>
        /// <param name="reason">关闭原因。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public ValueTask CloseAsync(int statusCode, string reason, CancellationToken cancellationToken = default)
        {
            State = WebSocketState.Closed;
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        /// <summary>释放内存连接。</summary>
        /// <returns>已完成任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
