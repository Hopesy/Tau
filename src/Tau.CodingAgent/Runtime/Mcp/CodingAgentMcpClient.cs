// 作者：xxx
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 传输】收发独立 JSON-RPC 报文，传输层负责消息边界和资源释放。</summary>
public interface ICodingAgentMcpTransport : IAsyncDisposable
{
    event Action<JsonElement>? Message;
    event Action<Exception>? Error;
    event Action? Closed;
    /// <summary>【CodingAgent】【MCP 启动】启动连接并接收消息。</summary><param name="token">取消信号。</param><returns>启动任务。</returns>
    Task StartAsync(CancellationToken token = default);
    /// <summary>【CodingAgent】【MCP 发送】发送一个完整报文。</summary><param name="message">报文。</param><param name="token">取消信号。</param><returns>发送任务。</returns>
    Task SendAsync(JsonElement message, CancellationToken token = default);
    /// <summary>【CodingAgent】【MCP 协商】更新后续 HTTP 请求使用的协议版本。</summary><param name="version">协商版本。</param>
    void SetProtocolVersion(string version) { }
}

/// <summary>【CodingAgent】【MCP 协议错误】保留服务端 JSON-RPC 错误码及可选数据。</summary>
public sealed class CodingAgentMcpException : Exception
{
    public double Code { get; }
    public JsonElement? DataValue { get; }
    /// <summary>【CodingAgent】【MCP 协议错误】创建可检查错误码的异常。</summary>
    /// <param name="code">JSON-RPC 错误码。</param><param name="message">错误说明。</param><param name="data">错误数据。</param>
    public CodingAgentMcpException(double code, string message, JsonElement? data = null) : base(message) { Code = code; DataValue = data?.Clone(); }
}

/// <summary>【CodingAgent】【MCP 客户端】管理初始化、并发请求、进度续期、取消、服务端回调和分页。</summary>
public sealed class CodingAgentMcpClient : IAsyncDisposable
{
    public const string LatestProtocolVersion = "2025-11-25";
    private static readonly string[] Versions = [LatestProtocolVersion, "2025-06-18", "2025-03-26", "2024-11-05"];
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private readonly string _name;
    private readonly string _version;
    private readonly TimeSpan _timeout;
    private ICodingAgentMcpTransport? _transport;
    private readonly object _lifecycleGate = new();
    private Task? _disposeTask;
    private long _nextId;
    private int _state;
    public bool IsConnected => Volatile.Read(ref _state) == 2;
    public JsonElement? ServerInfo { get; private set; }
    public JsonElement? ServerCapabilities { get; private set; }
    public string? Instructions { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public event Action<string, JsonElement?>? Notification;
    public event Action<Exception>? Error;
    public event Action? Closed;

    /// <summary>【CodingAgent】【MCP 客户端】设置客户端身份、请求时限及可选工作目录根列表。</summary>
    /// <param name="name">客户端名称。</param><param name="version">客户端版本。</param><param name="timeout">默认请求时限。</param><param name="roots">可选根目录读取器。</param>
    public CodingAgentMcpClient(string name = "tau", string version = "1.0.0", TimeSpan? timeout = null, Func<Task<JsonElement>>? roots = null)
    {
        _name = name; _version = version; _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _handlers["ping"] = (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new JsonObject()));
        if (roots is not null) _handlers["roots/list"] = async (_, _) => JsonSerializer.SerializeToElement(new { roots = await roots().ConfigureAwait(false) });
    }

    /// <summary>【CodingAgent】【MCP 初始化】协商协议并发布 initialized；失败时释放传输且不可复用旧客户端。</summary>
    /// <param name="transport">未启动传输。</param><param name="token">取消信号。</param><returns>原始初始化结果。</returns>
    public async Task<JsonElement> ConnectAsync(ICodingAgentMcpTransport transport, CancellationToken token = default)
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new InvalidOperationException("MCP client has already been started or closed.");
            _transport = transport;
            transport.Message += Receive; transport.Error += ReportError; transport.Closed += MarkClosed;
        }
        try
        {
            await transport.StartAsync(token).ConfigureAwait(false);
            var capabilities = new JsonObject();
            if (_handlers.ContainsKey("roots/list")) capabilities["roots"] = new JsonObject();
            var result = await RequestCoreAsync("initialize", new JsonObject { ["protocolVersion"] = LatestProtocolVersion,
                ["capabilities"] = capabilities, ["clientInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version } }, null, null, token, true).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                !result.TryGetProperty("capabilities", out var serverCapabilities) || serverCapabilities.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object || !HasString(info, "name") || !HasString(info, "version") ||
                result.TryGetProperty("instructions", out var instructions) && instructions.ValueKind != JsonValueKind.String)
                throw new CodingAgentMcpException(-32600, "Invalid MCP initialize result");
            ProtocolVersion = protocol.GetString();
            if (!Versions.Contains(ProtocolVersion, StringComparer.Ordinal)) throw new InvalidOperationException("MCP server selected unsupported protocol version " + ProtocolVersion);
            ServerInfo = info.Clone(); ServerCapabilities = serverCapabilities.Clone();
            Instructions = result.TryGetProperty("instructions", out instructions) ? instructions.GetString() : null;
            transport.SetProtocolVersion(ProtocolVersion!);
            await NotifyAsync("notifications/initialized", null, token).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _state, 2, 1) != 1) throw new IOException("MCP connection closed.");
            return result;
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>【CodingAgent】【MCP 请求】关联返回值、取消与请求进度，进度通知重置请求时限。</summary>
    /// <param name="method">方法名。</param><param name="parameters">参数对象。</param><param name="onProgress">进度回调。</param>
    /// <param name="timeout">可选时限覆盖。</param><param name="token">取消信号。</param><returns>服务端 result 值。</returns>
    public Task<JsonElement> RequestAsync(string method, JsonObject? parameters = null, Action<JsonElement>? onProgress = null,
        TimeSpan? timeout = null, CancellationToken token = default) => RequestCoreAsync(method, parameters, onProgress, timeout, token, false);

    /// <summary>【CodingAgent】【MCP 通知】发送无返回值通知。</summary>
    /// <param name="method">通知方法。</param><param name="parameters">参数。</param><param name="token">取消信号。</param><returns>发送任务。</returns>
    public Task NotifyAsync(string method, JsonObject? parameters = null, CancellationToken token = default)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters.DeepClone();
        return RequireTransport().SendAsync(JsonSerializer.SerializeToElement(message), token);
    }

    /// <summary>【CodingAgent】【MCP 回调】登记服务端调用的客户端方法。</summary>
    /// <param name="method">方法名。</param><param name="handler">支持取消的处理器。</param>
    public void SetRequestHandler(string method, Func<JsonElement?, CancellationToken, Task<JsonElement>> handler) => _handlers[method] = handler;

    /// <summary>【CodingAgent】【MCP 分页】读取全部列表页并防止重复游标或无限分页。</summary>
    /// <param name="method">列表方法。</param><param name="key">结果数组字段。</param><param name="token">取消信号。</param><returns>独立条目列表。</returns>
    public async Task<IReadOnlyList<JsonElement>> ListAllAsync(string method, string key, CancellationToken token = default)
    {
        var values = new List<JsonElement>(); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var result = await RequestAsync(method, cursor is null ? null : new JsonObject { ["cursor"] = cursor }, token: token).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array ||
                items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)) throw new CodingAgentMcpException(-32600, "Invalid MCP " + method + " result");
            values.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            if (!result.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null || next.ValueKind == JsonValueKind.String && next.GetString() == "") return values;
            if (next.ValueKind != JsonValueKind.String) throw new CodingAgentMcpException(-32600, "Invalid MCP " + method + " cursor");
            cursor = next.GetString()!;
            if (!cursors.Add(cursor)) throw new InvalidOperationException("MCP " + method + " returned duplicate cursor: " + cursor);
        }
        throw new InvalidOperationException("MCP " + method + " exceeded 1000 pages");
    }

    /// <summary>【CodingAgent】【MCP 工具目录】读取全部工具并验证名称和输入 Schema。</summary>
    /// <param name="token">取消信号。</param><returns>工具定义。</returns>
    public async Task<IReadOnlyList<JsonElement>> ListToolsAsync(CancellationToken token = default)
    {
        var tools = await ListAllAsync("tools/list", "tools", token).ConfigureAwait(false);
        if (tools.Any(tool => !HasString(tool, "name") || !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object))
            throw new CodingAgentMcpException(-32600, "Invalid entry in MCP tools/list result");
        return tools;
    }

    /// <summary>【CodingAgent】【MCP 资源目录】读取全部资源或模板；缺少名称时使用 URI 作为名称。</summary>
    /// <param name="templates">是否列出模板。</param><param name="token">取消信号。</param><returns>规范化资源定义。</returns>
    public async Task<IReadOnlyList<JsonElement>> ListResourcesAsync(bool templates = false, CancellationToken token = default)
    {
        var method = templates ? "resources/templates/list" : "resources/list";
        var values = await ListAllAsync(method, templates ? "resourceTemplates" : "resources", token).ConfigureAwait(false);
        var key = templates ? "uriTemplate" : "uri";
        if (values.Any(value => !HasString(value, key) || value.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.String))
            throw new CodingAgentMcpException(-32600, "Invalid entry in MCP " + method + " result");
        return values.Select(value =>
        {
            var result = JsonNode.Parse(value.GetRawText())!.AsObject();
            if (!result.ContainsKey("name")) result["name"] = result[key]!.DeepClone();
            return JsonSerializer.SerializeToElement(result);
        }).ToArray();
    }

    /// <summary>【CodingAgent】【MCP 资源分页】读取指定游标页，校验资源并为缺少名称的资源补 URI 名称。</summary>
    /// <param name="templates">是否列出模板。</param><param name="cursor">上一页游标。</param><param name="token">取消信号。</param><returns>规范化页对象。</returns>
    public async Task<JsonElement> ListResourcesPageAsync(bool templates = false, string? cursor = null, CancellationToken token = default)
    {
        var key = templates ? "resourceTemplates" : "resources";
        var uri = templates ? "uriTemplate" : "uri";
        var result = await RequestAsync(templates ? "resources/templates/list" : "resources/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, token: token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array ||
            result.TryGetProperty("nextCursor", out var next) && next.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new CodingAgentMcpException(-32600, "Invalid MCP resource page");
        var normalized = JsonNode.Parse(result.GetRawText())!.AsObject();
        foreach (var item in normalized[key]!.AsArray())
        {
            if (item is not JsonObject value || value[uri]?.GetValueKind() != JsonValueKind.String ||
                value.ContainsKey("name") && value["name"]?.GetValueKind() != JsonValueKind.String)
                throw new CodingAgentMcpException(-32600, "Invalid MCP resource item");
            if (!value.ContainsKey("name")) value["name"] = value[uri]!.DeepClone();
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    /// <summary>【CodingAgent】【MCP 资源读取】获取文本或二进制资源并校验内容记录。</summary>
    /// <param name="uri">资源 URI。</param><param name="token">取消信号。</param><returns>包含 contents 的原生结果。</returns>
    public async Task<JsonElement> ReadResourceAsync(string uri, CancellationToken token = default)
    {
        var result = await RequestAsync("resources/read", new() { ["uri"] = uri }, token: token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array ||
            contents.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object || !HasString(value, "uri") || !HasString(value, "text") && !HasString(value, "blob")))
            throw new CodingAgentMcpException(-32600, "Invalid MCP resources/read result");
        return result;
    }

    /// <summary>【CodingAgent】【MCP 工具调用】返回原生内容和结构化数据，仅有 structuredContent 时补空内容数组。</summary>
    /// <param name="name">服务器工具名称。</param><param name="arguments">参数对象。</param><param name="onProgress">可选进度。</param>
    /// <param name="token">取消信号。</param><param name="timeout">可选单次超时。</param><returns>规范化 tools/call 结果。</returns>
    public async Task<JsonElement> CallToolAsync(string name, JsonObject? arguments = null, Action<JsonElement>? onProgress = null, CancellationToken token = default, TimeSpan? timeout = null)
    {
        var parameters = new JsonObject { ["name"] = name };
        if (arguments is not null) parameters["arguments"] = arguments.DeepClone();
        var result = await RequestAsync("tools/call", parameters, onProgress, timeout, token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || result.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Array ||
            result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Object)
            throw new CodingAgentMcpException(-32600, "Invalid MCP tools/call result");
        var normalized = JsonNode.Parse(result.GetRawText())!.AsObject();
        if (!normalized.ContainsKey("content")) normalized["content"] = new JsonArray();
        return JsonSerializer.SerializeToElement(normalized);
    }

    /// <summary>【CodingAgent】【MCP 关联】建立待完成请求并在所有结束路径移除定时器和取消监听。</summary>
    /// <param name="method">方法。</param><param name="parameters">参数。</param><param name="progress">进度回调。</param>
    /// <param name="timeout">时限。</param><param name="token">取消信号。</param><param name="connecting">是否为初始化。</param><returns>响应结果。</returns>
    private async Task<JsonElement> RequestCoreAsync(string method, JsonObject? parameters, Action<JsonElement>? progress, TimeSpan? timeout,
        CancellationToken token, bool connecting)
    {
        if (!connecting && Volatile.Read(ref _state) != 2) throw new InvalidOperationException("MCP client is not connected.");
        var transport = RequireTransport(); token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var pending = new Pending(timeout ?? _timeout, progress,
            () => CancelPending(id, new TimeoutException("MCP request timed out."), method != "initialize"));
        _pending[id] = pending;
        using var registration = token.Register(() => CancelPending(id, new OperationCanceledException(token), method != "initialize"));
        try
        {
            pending.ResetTimeout();
            // 1. 【CodingAgent】【MCP 请求快照】复制参数，进度标识只属于本次请求，不改变调用方对象
            var args = parameters?.DeepClone().AsObject();
            if (progress is not null)
            {
                args ??= new();
                var meta = args["_meta"] as JsonObject ?? new();
                meta["progressToken"] = id;
                if (args["_meta"] is not JsonObject) args["_meta"] = meta;
            }
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (args is not null) message["params"] = args;
            if (!pending.Completion.Task.IsCompleted) _ = SendRequestAsync(transport, JsonSerializer.SerializeToElement(message), id, sending.Token);
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            // 2. 【MCP】【请求发送回收】超时、关闭或完成均停止尚未结束的发送，取消通知使用独立生命周期
            try { await sending.CancelAsync().ConfigureAwait(false); } catch (AggregateException error) { ReportError(error); }
        }
    }

    /// <summary>【CodingAgent】【MCP 异步发送】发送等待不阻止请求自身超时或取消，发送失败关联到原待处理请求。</summary>
    /// <param name="transport">当前传输。</param><param name="message">独立报文。</param><param name="id">请求标识。</param>
    /// <param name="token">调用方取消信号。</param><returns>发送观察任务。</returns>
    private async Task SendRequestAsync(ICodingAgentMcpTransport transport, JsonElement message, long id, CancellationToken token)
    {
        try { await transport.SendAsync(message, token).ConfigureAwait(false); }
        catch (Exception error) { CancelPending(id, error, false); }
    }

    /// <summary>【CodingAgent】【MCP 分发】响应按标识匹配，服务端请求异步执行，通知不会阻塞其他响应。</summary>
    /// <param name="message">独立 JSON-RPC 报文。</param>
    private void Receive(JsonElement message)
    {
        try
        {
            CodingAgentMcpJsonRpc.Validate(message);
            var hasId = message.TryGetProperty("id", out var id);
            if (!CodingAgentMcpJsonRpc.IsResponse(message) && message.TryGetProperty("method", out var method))
            {
                var parameters = message.TryGetProperty("params", out var args) ? args.Clone() : (JsonElement?)null;
                if (hasId) { _ = HandleRequestAsync(id.Clone(), method.GetString()!, parameters); return; }
                var name = method.GetString()!;
                if (name == "notifications/progress" && parameters is { ValueKind: JsonValueKind.Object } update &&
                    update.TryGetProperty("progressToken", out var progressId) && CodingAgentMcpJsonRpc.TryRequestId(progressId, out var requestId) &&
                    update.TryGetProperty("progress", out var amount) && amount.ValueKind == JsonValueKind.Number && _pending.TryGetValue(requestId, out var pending) && pending.Progress is not null)
                {
                    pending.ResetTimeout();
                    try { pending.Progress(update); } catch (Exception error) { ReportError(error); }
                }
                if (name == "notifications/cancelled" && parameters is { ValueKind: JsonValueKind.Object } cancellation &&
                    cancellation.TryGetProperty("requestId", out var incomingId) && CodingAgentMcpJsonRpc.IsId(incomingId) &&
                    _incoming.TryGetValue(CodingAgentMcpJsonRpc.IdKey(incomingId), out var source)) source.Cancel();
                try { Notification?.Invoke(name, parameters); } catch (Exception error) { ReportError(error); }
                return;
            }
            if (!CodingAgentMcpJsonRpc.TryRequestId(id, out var number))
            { ReportError(new IOException("Received response for unknown MCP request " + id.ToString())); return; }
            CodingAgentMcpException? responseError = null;
            if (message.TryGetProperty("error", out var failure))
            {
                responseError = new(failure.GetProperty("code").GetDouble(), failure.GetProperty("message").GetString()!, failure.TryGetProperty("data", out var data) ? data : null);
            }
            else if (!message.TryGetProperty("result", out _)) throw new CodingAgentMcpException(-32600, "Invalid JSON-RPC response");
            if (!_pending.TryRemove(number, out var request))
            { ReportError(new IOException("Received response for unknown MCP request " + id.ToString())); return; }
            if (responseError is not null) request.Completion.TrySetException(responseError);
            else request.Completion.TrySetResult(message.GetProperty("result").Clone());
        }
        catch (Exception error) { ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 服务端调用】执行客户端方法并返回成功或标准错误，服务端取消会传入同一处理器。</summary>
    /// <param name="id">原始调用标识。</param><param name="method">调用方法。</param><param name="parameters">参数。</param><returns>处理任务。</returns>
    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        using var source = new CancellationTokenSource();
        var key = CodingAgentMcpJsonRpc.IdKey(id);
        _incoming[key] = source;
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        try
        {
            if (!_handlers.TryGetValue(method, out var handler)) throw new CodingAgentMcpException(-32601, "Method not found: " + method);
            var result = await handler(parameters, source.Token).ConfigureAwait(false);
            response["result"] = result.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(result.GetRawText());
        }
        catch (Exception error)
        {
            response["error"] = new JsonObject { ["code"] = error is CodingAgentMcpException mcp ? mcp.Code : -32603, ["message"] = error.Message };
            if (error is CodingAgentMcpException { DataValue: { } data }) response["error"]!["data"] = JsonNode.Parse(data.GetRawText());
        }
        finally { _incoming.TryRemove(key, out _); }
        try { await RequireTransport().SendAsync(JsonSerializer.SerializeToElement(response)).ConfigureAwait(false); }
        catch (Exception error) { ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 取消】移除请求并通知服务端；初始化请求按协议禁止发取消通知。</summary>
    /// <param name="id">请求标识。</param><param name="error">取消或超时异常。</param><param name="notify">是否发送取消通知。</param>
    private void CancelPending(long id, Exception error, bool notify)
    {
        if (!_pending.TryRemove(id, out var pending)) return;
        if (error is OperationCanceledException cancelled) pending.Completion.TrySetCanceled(cancelled.CancellationToken);
        else pending.Completion.TrySetException(error);
        if (notify) _ = SendCancellationAsync(id, error.Message);
    }

    /// <summary>【CodingAgent】【MCP 取消通知】以独立信号发送取消，发送错误仅作为连接诊断。</summary>
    /// <param name="id">请求标识。</param><param name="reason">取消原因。</param><returns>发送任务。</returns>
    private async Task SendCancellationAsync(long id, string reason)
    {
        try { await NotifyAsync("notifications/cancelled", new() { ["requestId"] = id, ["reason"] = reason }).ConfigureAwait(false); }
        catch (Exception error) { ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 关闭】失败所有待处理请求并取消服务端回调，关闭事件只发布一次。</summary>
    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _state, 3) == 3) return;
        foreach (var pair in _pending) CancelPending(pair.Key, new IOException("MCP connection closed."), false);
        foreach (var source in _incoming.Values)
            try { source.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException error) { ReportError(error); }
        try { Closed?.Invoke(); } catch (Exception error) { ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 释放】先取消请求，再解绑事件并释放传输。</summary><returns>释放任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            var transport = Interlocked.Exchange(ref _transport, null);
            _ = DisposeCoreAsync(transport, completion);
            return new(_disposeTask);
        }
    }

    /// <summary>【MCP】【客户端关闭屏障】所有调用方共同等待传输清理，关闭回调重入也只能获得同一个任务。</summary>
    /// <param name="transport">已摘除的传输。</param><param name="completion">共享完成信号。</param><returns>关闭观察任务。</returns>
    private async Task DisposeCoreAsync(ICodingAgentMcpTransport? transport, TaskCompletionSource completion)
    {
        try
        {
            MarkClosed();
            if (transport is not null)
            {
                transport.Message -= Receive; transport.Error -= ReportError; transport.Closed -= MarkClosed;
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    /// <summary>【CodingAgent】【MCP 连接】读取尚未关闭的传输。</summary><returns>活动传输。</returns>
    private ICodingAgentMcpTransport RequireTransport() => Volatile.Read(ref _state) != 3 && _transport is { } transport ? transport : throw new IOException("MCP connection closed.");
    /// <summary>【CodingAgent】【MCP 诊断】隔离诊断监听器异常，不影响响应分发。</summary><param name="error">原异常。</param>
    private void ReportError(Exception error) { try { Error?.Invoke(error); } catch { } }
    /// <summary>【CodingAgent】【MCP 字段】判断必需字符串字段。</summary><param name="value">对象。</param><param name="name">字段名。</param><returns>是否有效。</returns>
    private static bool HasString(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String;

    /// <summary>【CodingAgent】【MCP 待处理】保存单次响应和可重置的进度时限，长时限分段计时以避开系统定时器上限。</summary>
    /// <param name="timeout">请求时限，非正值表示不自动超时。</param><param name="progress">进度回调。</param><param name="expire">超时动作。</param>
    private sealed class Pending(TimeSpan timeout, Action<JsonElement>? progress, Action expire) : IDisposable
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        private readonly object _timerGate = new();
        private Timer? _timer;
        private long _started;
        private bool _disposed;
        internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<JsonElement>? Progress { get; } = progress;
        /// <summary>【CodingAgent】【MCP 续期】进度通知重新开始时限，已结束请求不再续期。</summary>
        internal void ResetTimeout()
        {
            lock (_timerGate)
            {
                if (_disposed || timeout <= TimeSpan.Zero || Completion.Task.IsCompleted) return;
                _started = Stopwatch.GetTimestamp();
                _timer ??= new Timer(CheckTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                Arm(timeout);
            }
        }

        /// <summary>【MCP】【分段超时】根据单调时钟判断最终截止点，旧定时回调不得覆盖进度续期。</summary>
        /// <param name="state">定时器状态，未使用。</param>
        private void CheckTimeout(object? state)
        {
            lock (_timerGate)
            {
                if (_disposed || Completion.Task.IsCompleted) return;
                var remaining = timeout - Stopwatch.GetElapsedTime(_started);
                if (remaining > TimeSpan.Zero) Arm(remaining);
                else expire();
            }
        }

        /// <summary>【MCP】【定时片段】调用方持锁，最长等待不超过系统上限，亚毫秒片段至少等待一毫秒避免忙循环。</summary>
        /// <param name="remaining">距离最终截止点的剩余时间。</param>
        private void Arm(TimeSpan remaining) =>
            _timer!.Change(remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, Timeout.InfiniteTimeSpan);

        /// <summary>【MCP】【定时器清理】与续期及已排队回调互斥，完成请求不保留定时资源。</summary>
        public void Dispose()
        {
            lock (_timerGate) { _disposed = true; _timer?.Dispose(); _timer = null; }
        }
    }
}
