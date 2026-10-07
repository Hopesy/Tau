// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpClientTests
{
    /// <summary>【CodingAgent】【MCP 工具代理测试】真实管道保留参数、异步进度及逐调用超时，并允许后续调用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpToolProxyCallsActualServerWithProgressAndTimeout()
    {
        using var fixture = new Fixture();
        await using var transport = fixture.Transport();
        await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport);
        var tool = new CodingAgentMcpTool("fixture", Parse("""{"name":"updates","inputSchema":{}}"""), "mcp__fixture__updates", "direct", Parse("{}"), _ => Task.FromResult(client));
        var progress = new List<string>();
        var result = await tool.ExecuteAsync("call", Parse("""{"value":42}"""), onUpdate: async update => { await Task.Delay(10); progress.Add(update.Text); });
        Assert.Equal(["Progress 1/2", "完成"], progress);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("structuredContent").GetProperty("value").GetInt32());
        var slow = new CodingAgentMcpTool("fixture", Parse("""{"name":"hold","inputSchema":{}}"""), "mcp__fixture__hold", "direct", Parse("{}"), _ => Task.FromResult(client), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => slow.ExecuteAsync("slow", Parse("{}")));
        Assert.False((await tool.ExecuteAsync("next", Parse("{}"))).IsError);
    }

    /// <summary>【CodingAgent】【MCP 发送取消】发送本身挂起时请求仍按时取消，初始化不得发送 cancelled 通知。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpInitializationCancellationDoesNotWaitForBlockedSend()
    {
        await using var transport = new BlockedTransport();
        await using var client = new CodingAgentMcpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(transport, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(transport.Disposed);
        Assert.Equal(["initialize"], transport.Methods);
    }

    /// <summary>【CodingAgent】【MCP 真实管道】验证初始化、乱序响应、分页、资源、结构化结果及进度续期。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpStdioSupportsConcurrentRequestsProgressAndPaginatedResources()
    {
        using var fixture = new Fixture();
        await using var transport = fixture.Transport();
        await using var client = new CodingAgentMcpClient(timeout: TimeSpan.FromSeconds(5), roots: () => Task.FromResult(Parse("""[{"uri":"file:///workspace","name":"workspace"}]""")));
        var diagnostics = new ConcurrentQueue<Exception>();
        client.Error += diagnostics.Enqueue;
        await client.ConnectAsync(transport);
        Assert.Equal(CodingAgentMcpClient.LatestProtocolVersion, client.ProtocolVersion);
        Assert.Equal("fixture", client.ServerInfo!.Value.GetProperty("name").GetString());
        Assert.Equal("fixture instructions", client.Instructions);
        var calls = Enumerable.Range(0, 6).Select(index => client.RequestAsync("echo", new() { ["value"] = index, ["delay"] = (6 - index) * 10 })).ToArray();
        var replies = await Task.WhenAll(calls);
        Assert.Equal(Enumerable.Range(0, 6), replies.Select(reply => reply.GetProperty("value").GetInt32()));
        var tools = await client.ListToolsAsync();
        Assert.Equal(["first", "second"], tools.Select(tool => tool.GetProperty("name").GetString()));
        var resources = await client.ListResourcesAsync();
        Assert.Equal("test://resource", Assert.Single(resources).GetProperty("name").GetString());
        Assert.Equal("test://{id}", Assert.Single(await client.ListResourcesAsync(true)).GetProperty("name").GetString());
        Assert.Equal("资源正文", (await client.ReadResourceAsync("test://resource")).GetProperty("contents")[0].GetProperty("text").GetString());
        var toolResult = await client.CallToolAsync("structured", new() { ["value"] = 42 });
        Assert.Empty(toolResult.GetProperty("content").EnumerateArray());
        Assert.Equal(42, toolResult.GetProperty("structuredContent").GetProperty("value").GetInt32());
        var progress = new List<int>();
        var original = new JsonObject { ["_meta"] = new JsonObject { ["kept"] = true } };
        await client.RequestAsync("progress", original, value => progress.Add(value.GetProperty("progress").GetInt32()), TimeSpan.FromMilliseconds(400));
        Assert.Equal([1, 2, 3, 4], progress);
        Assert.False(original["_meta"]!.AsObject().ContainsKey("progressToken"));
        var status = await client.RequestAsync("status");
        Assert.Equal("file:///workspace", status.GetProperty("roots")[0].GetProperty("uri").GetString());
        Assert.True(status.GetProperty("ping").GetBoolean());
        Assert.Empty(diagnostics);
    }

    /// <summary>【CodingAgent】【MCP 故障恢复】取消和超时通知服务端，坏报文不会丢掉待处理请求，后续仍可请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpStdioCancellationTimeoutAndMalformedResponseKeepClientUsable()
    {
        using var fixture = new Fixture();
        await using var transport = fixture.Transport();
        await using var client = new CodingAgentMcpClient();
        var diagnostics = new ConcurrentQueue<Exception>();
        client.Error += diagnostics.Enqueue;
        await client.ConnectAsync(transport);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("hold", token: cancel.Token));
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("hold", timeout: TimeSpan.FromMilliseconds(100)));
        var result = await client.RequestAsync("malformed", timeout: TimeSpan.FromSeconds(3));
        Assert.True(result.GetProperty("recovered").GetBoolean());
        Assert.NotEmpty(diagnostics);
        var status = await client.RequestAsync("status");
        Assert.Equal(2, status.GetProperty("cancelled").GetInt32());
        var error = await Assert.ThrowsAsync<CodingAgentMcpException>(() => client.RequestAsync("failure"));
        Assert.Equal(-32000, error.Code);
        Assert.Equal("retry", error.DataValue!.Value.GetProperty("hint").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAllAsync("repeat-pages", "tools"));
        Assert.Equal("中文", (await client.RequestAsync("split")).GetProperty("value").GetString());
    }

    /// <summary>【CodingAgent】【MCP 协议拒绝】不支持的协议和无效初始化释放服务端进程。</summary>
    /// <param name="mode">测试服务器返回的初始化变体。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("unsupported")]
    [InlineData("invalid")]
    public async Task McpStdioRejectsInvalidInitializeAndCloses(string mode)
    {
        using var fixture = new Fixture();
        await using var transport = fixture.Transport(mode);
        await using var client = new CodingAgentMcpClient();
        var closed = 0;
        client.Closed += () => closed++;
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(transport));
        Assert.Equal(1, closed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("echo"));
    }

    /// <summary>【CodingAgent】【MCP 边界】超大报文和 stderr 有界，服务端退出失败所有挂起调用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpStdioBoundsOutputAndFailsRequestsWhenServerExits()
    {
        using var fixture = new Fixture();
        await using var transport = new CodingAgentMcpStdioTransport("node", [fixture.Script], maxMessageBytes: 1024, maxStderrBytes: 32);
        await using var client = new CodingAgentMcpClient();
        var diagnostics = new ConcurrentQueue<Exception>();
        client.Error += diagnostics.Enqueue;
        await client.ConnectAsync(transport);
        Assert.True((await client.RequestAsync("oversized")).GetProperty("recovered").GetBoolean());
        Assert.Contains(diagnostics, error => error.Message.Contains("exceeds 1024"));
        Assert.True(transport.StandardError.Length <= 32);
        var held = client.RequestAsync("hold");
        await Assert.ThrowsAsync<IOException>(() => client.RequestAsync("exit"));
        await Assert.ThrowsAsync<IOException>(() => held);
    }

    /// <summary>【CodingAgent】【MCP 测试数据】解析不依赖反射序列化的独立 JSON。</summary>
    /// <param name="json">JSON 文本。</param><returns>独立元素。</returns>
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    /// <summary>【CodingAgent】【MCP 阻塞传输】模拟服务端迟迟不接收请求的连接。</summary>
    private sealed class BlockedTransport : ICodingAgentMcpTransport
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Methods { get; } = [];
        public bool Disposed { get; private set; }
        public event Action<JsonElement>? Message { add { } remove { } }
        public event Action<Exception>? Error { add { } remove { } }
        public event Action? Closed;
        /// <summary>【CodingAgent】【MCP 测试启动】保持连接可用。</summary><param name="token">取消信号。</param><returns>已完成任务。</returns>
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        /// <summary>【CodingAgent】【MCP 测试发送】记录方法并挂起，模拟底层发送尚未完成。</summary><param name="message">报文。</param><param name="token">取消信号。</param><returns>阻塞任务。</returns>
        public Task SendAsync(JsonElement message, CancellationToken token = default) { Methods.Add(message.GetProperty("method").GetString()!); return _released.Task; }
        /// <summary>【CodingAgent】【MCP 测试释放】解除挂起发送并通知关闭。</summary><returns>已完成任务。</returns>
        public ValueTask DisposeAsync() { if (!Disposed) { Disposed = true; _released.TrySetResult(); Closed?.Invoke(); } return ValueTask.CompletedTask; }
    }

    /// <summary>【CodingAgent】【MCP 测试服务】创建真实 Node JSON-RPC 子进程脚本。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-mcp-client-" + Guid.NewGuid().ToString("N"));
        internal string Script => Path.Combine(_root, "server.cjs");
        /// <summary>【CodingAgent】【MCP 测试服务】写入只通过标准管道通信的测试服务器。</summary>
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Script, """
                const readline=require('node:readline');let cancelled=0,roots=[],ping=false;
                /** 【CodingAgent】【测试报文】@param {object} value 报文 @returns {void} 写入 JSON 行 */
                const send=value=>process.stdout.write(JSON.stringify(value)+'\n');
                readline.createInterface({input:process.stdin}).on('line',line=>{
                  const m=JSON.parse(line),p=m.params??{};
                  if(m.id==='server-roots'){roots=m.result?.roots??[];return;}
                  if(m.id==='server-ping'){ping=!!m.result;return;}
                  const result=value=>send({jsonrpc:'2.0',id:m.id,result:value});
                  switch(m.method){
                    case 'initialize':
                      if(process.argv[2]==='invalid'){result({protocolVersion:'2025-11-25'});break;}
                      result({protocolVersion:process.argv[2]==='unsupported'?'1900-01-01':p.protocolVersion,serverInfo:{name:'fixture',version:'1'},capabilities:{tools:{},resources:{}},instructions:'fixture instructions'});break;
                    case 'notifications/initialized':
                      send({jsonrpc:'2.0',id:'server-roots',method:'roots/list'});send({jsonrpc:'2.0',id:'server-ping',method:'ping'});break;
                    case 'notifications/cancelled':cancelled++;break;
                    case 'echo':setTimeout(()=>result({value:p.value}),p.delay??0);break;
                    case 'tools/list':result(p.cursor?{tools:[{name:'second',inputSchema:{type:'object'}}],nextCursor:''}:{tools:[{name:'first',inputSchema:{type:'object'}}],nextCursor:'next'});break;
                    case 'resources/list':result({resources:[{uri:'test://resource'}]});break;
                    case 'resources/templates/list':result({resourceTemplates:[{uriTemplate:'test://{id}'}]});break;
                    case 'resources/read':result({contents:[{uri:p.uri,text:'资源正文'}]});break;
                    case 'tools/call':
                      if(p.name==='hold') break;
                      if(p.name==='updates'){
                        send({jsonrpc:'2.0',method:'notifications/progress',params:{progressToken:p._meta.progressToken,progress:1,total:2}});
                        send({jsonrpc:'2.0',method:'notifications/progress',params:{progressToken:p._meta.progressToken,progress:2,message:'完成'}});
                      }
                      result({structuredContent:p.arguments});break;
                    case 'progress':{let count=0;const timer=setInterval(()=>{send({jsonrpc:'2.0',method:'notifications/progress',params:{progressToken:p._meta.progressToken,progress:++count}});if(count===4){clearInterval(timer);result({done:true});}},150);break;}
                    case 'status':result({cancelled,roots,ping});break;
                    case 'hold':break;
                    case 'malformed':send({jsonrpc:'2.0',id:m.id,error:'broken'});setTimeout(()=>result({recovered:true}),20);break;
                    case 'failure':send({jsonrpc:'2.0',id:m.id,error:{code:-32000,message:'fixture failure',data:{hint:'retry'}}});break;
                    case 'repeat-pages':result({tools:[],nextCursor:'same'});break;
                    case 'split':{const bytes=Buffer.from(JSON.stringify({jsonrpc:'2.0',id:m.id,result:{value:'中文'}})+'\n'),cut=bytes.indexOf(Buffer.from('中'))+1;process.stdout.write(bytes.subarray(0,cut));setTimeout(()=>process.stdout.write(bytes.subarray(cut)),10);break;}
                    case 'oversized':process.stderr.write('e'.repeat(5000));process.stdout.write('x'.repeat(3000)+'\n');result({recovered:true});break;
                    case 'exit':process.exit(0);break;
                  }
                });
                """);
        }
        /// <summary>【CodingAgent】【MCP 测试传输】创建指定初始化行为的实际子进程。</summary>
        /// <param name="mode">可选初始化变体。</param><returns>未启动传输。</returns>
        internal CodingAgentMcpStdioTransport Transport(string? mode = null) => new("node", mode is null ? [Script] : [Script, mode]);
        /// <summary>【CodingAgent】【MCP 测试清理】子进程已释放后移除测试专用目录。</summary>
        public void Dispose() => Directory.Delete(_root, true);
    }
}
