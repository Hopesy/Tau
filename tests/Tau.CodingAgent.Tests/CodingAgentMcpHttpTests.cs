// 作者：xxx
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpHttpTests
{
    /// <summary>【CodingAgent】【MCP HTTP 实测】真实回环服务验证会话头、SSE 事件恢复、进度和 DELETE 关闭。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpHttpResumesResponseStreamAndMaintainsSessionHeaders()
    {
        await using var server = await Server.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        await using (var transport = new CodingAgentMcpHttpTransport(server.Address, http, initialDelay: TimeSpan.Zero))
        await using (var client = new CodingAgentMcpClient(timeout: TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(transport);
            Assert.Equal("session-fixture", transport.SessionId);
            var progress = new List<int>();
            var reply = await client.RequestAsync("stream", onProgress: value => progress.Add(value.GetProperty("progress").GetInt32()));
            Assert.Equal("resumed", reply.GetProperty("value").GetString());
            Assert.Equal([1], progress);
            var status = await client.RequestAsync("status");
            Assert.Equal(CodingAgentMcpClient.LatestProtocolVersion, status.GetProperty("protocol").GetString());
            Assert.Equal("session-fixture", status.GetProperty("session").GetString());
            Assert.Equal("checkpoint", status.GetProperty("lastId").GetString());
            var failure = await Assert.ThrowsAsync<CodingAgentMcpException>(() => client.RequestAsync("unfinished"));
            Assert.Contains("stream ended without a response", failure.Message);
            Assert.True((await client.RequestAsync("alive")).GetProperty("ok").GetBoolean());
        }
        using var inspection = await http.GetAsync(new Uri(server.Address, "status"));
        using var state = JsonDocument.Parse(await inspection.Content.ReadAsStringAsync());
        Assert.Equal(1, state.RootElement.GetProperty("deleted").GetInt32());
    }

    /// <summary>【CodingAgent】【MCP HTTP 认证】一次质询后重新取令牌并重发，认证头和会话清理都沿用新令牌。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpHttpRetriesAuthenticationOnceWithFreshToken()
    {
        await using var server = await Server.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var auth = new Authentication();
        await using var transport = new CodingAgentMcpHttpTransport(new(server.Address, "auth"), http, auth: auth, openGetStream: false);
        await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport);
        Assert.Equal(1, auth.Challenges);
        Assert.True((await client.RequestAsync("alive")).GetProperty("ok").GetBoolean());
        var status = await client.RequestAsync("status");
        Assert.Equal("Bearer fresh", status.GetProperty("authorization").GetString());
    }

    /// <summary>【CodingAgent】【MCP HTTP 错误】保留会话失效及 HTTP 状态，空请求响应不能误判为成功。</summary>
    /// <param name="method">测试方法。</param><param name="status">预期 HTTP 状态。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("expired", 404)]
    [InlineData("accepted", 202)]
    [InlineData("unauthorized", 401)]
    [InlineData("wrong-content", 200)]
    public async Task McpHttpPreservesProtocolFailures(string method, int status)
    {
        await using var server = await Server.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        await using var transport = new CodingAgentMcpHttpTransport(server.Address, http, openGetStream: false);
        await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport);
        var failure = await Assert.ThrowsAsync<CodingAgentMcpHttpException>(() => client.RequestAsync(method));
        Assert.Equal(status, (int)failure.Status);
        Assert.Equal(method == "expired", failure.SessionExpired);
        if (method == "unauthorized") Assert.Contains("Bearer", failure.Authenticate);
    }

    /// <summary>【CodingAgent】【MCP SSE 边界】支持控制事件、多行数据和 UTF-8 BOM，累计短行也受事件大小限制。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpSseParserPreservesControlEventsAndBoundsAccumulatedData()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFFid: first\r\nretry: 12\r\n\r\n: comment\ndata: 中文\ndata: second\nevent: message\n\nid: \nretry: no\nid: bad\0id\ndata: tail"));
        var events = new List<(string?, string)>(); var ids = new List<string>(); var retries = new List<long>();
        await CodingAgentMcpSseParser.ConsumeAsync(source, (type, data) => events.Add((type, data)), ids.Add, retries.Add);
        Assert.Equal(["first", ""], ids); Assert.Equal([12L], retries);
        Assert.Equal(new[] { ((string?)"message", "中文\nsecond"), ((string?)null, "tail") }, events);
        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data: small\n", 10))));
        await Assert.ThrowsAsync<IOException>(() => CodingAgentMcpSseParser.ConsumeAsync(oversized, (_, _) => { }, maxEventBytes: 32));
    }

    /// <summary>【CodingAgent】【MCP 测试认证】记录质询并切换令牌。</summary>
    private sealed class Authentication : ICodingAgentMcpHttpAuth
    {
        public int Challenges { get; private set; }
        /// <summary>【CodingAgent】【MCP 测试令牌】返回质询前后的令牌。</summary><param name="token">取消信号。</param><returns>当前令牌。</returns>
        public Task<string?> GetTokenAsync(CancellationToken token) => Task.FromResult<string?>(Challenges == 0 ? "stale" : "fresh");
        /// <summary>【CodingAgent】【MCP 测试质询】验证服务端拒绝的旧令牌。</summary><param name="response">响应。</param><param name="usedToken">旧令牌。</param><param name="token">取消信号。</param><returns>已完成任务。</returns>
        public Task OnUnauthorizedAsync(HttpResponseMessage response, string? usedToken, CancellationToken token)
        { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal("stale", usedToken); Challenges++; return Task.CompletedTask; }
    }

    /// <summary>【CodingAgent】【MCP HTTP 测试服务】在随机回环端口运行独立 Node 服务。</summary>
    internal sealed class Server : IAsyncDisposable
    {
        private readonly string _root;
        private readonly Process _process;
        public Uri Address { get; }
        /// <summary>【CodingAgent】【MCP HTTP 测试实例】保存本次独立进程和目录。</summary>
        /// <param name="root">临时目录。</param><param name="process">已启动进程。</param><param name="address">监听地址。</param>
        private Server(string root, Process process, Uri address) { _root = root; _process = process; Address = address; }
        /// <summary>【CodingAgent】【MCP HTTP 测试启动】生成服务器脚本并等待随机端口就绪。</summary><returns>运行中的服务。</returns>
        internal static async Task<Server> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tau-mcp-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var script = Path.Combine(root, "server.cjs");
            File.WriteAllText(script, """
                const http=require('node:http');let deleted=0,lastId='',pending;const providerTokens=[];
                const server=http.createServer(async(req,res)=>{
                  if(req.url==='/status'){res.setHeader('content-type','application/json');res.end(JSON.stringify({deleted,providerTokens}));return;}
                  if(req.url.startsWith('/provider')){
                    providerTokens.push(req.headers.authorization??null);
                    if(req.url==='/provider-denied'||!req.headers.authorization?.startsWith('Bearer provider-')){res.writeHead(401);res.end('authenticate');return;}
                  }
                  if(req.method==='DELETE'){deleted++;res.writeHead(204);res.end();return;}
                  if(req.url==='/auth'&&req.headers.authorization!=='Bearer fresh'){res.writeHead(401,{'www-authenticate':'Bearer realm="fixture"'});res.end('authenticate');return;}
                  if(req.method==='GET'){
                    if(req.headers['last-event-id']==='checkpoint'&&pending){lastId=req.headers['last-event-id'];res.writeHead(200,{'content-type':'text/event-stream'});res.end('data: '+JSON.stringify({jsonrpc:'2.0',id:pending,result:{value:'resumed'}})+'\n\n');pending=undefined;return;}
                    res.writeHead(405);res.end();return;
                  }
                  let text='';for await(const chunk of req)text+=chunk;const m=JSON.parse(text);
                  const result=value=>{res.writeHead(200,{'content-type':'application/json','mcp-session-id':'session-fixture'});res.end(JSON.stringify({jsonrpc:'2.0',id:m.id,result:value}));};
                  if(!('id' in m)){res.writeHead(202);res.end();return;}
                  switch(m.method){
                    case 'initialize':result({protocolVersion:m.params.protocolVersion,serverInfo:{name:'http-fixture',version:'1'},capabilities:req.url.startsWith('/provider')?{tools:{}}:{}});break;
                    case 'tools/list':result({tools:[{name:'echo',inputSchema:{type:'object'}}]});break;
                    case 'tools/call':result({content:[{type:'text',text:JSON.stringify({authorization:req.headers.authorization,providerHeader:req.headers['x-provider']??null})}]});break;
                    case 'stream':pending=m.id;res.writeHead(200,{'content-type':'text/event-stream'});res.end('id: checkpoint\nretry: 0\n\nevent: message\ndata: '+JSON.stringify({jsonrpc:'2.0',method:'notifications/progress',params:{progressToken:m.params._meta.progressToken,progress:1}})+'\n\n');break;
                    case 'unfinished':res.writeHead(200,{'content-type':'text/event-stream'});res.end(': heartbeat\n\n');break;
                    case 'status':result({protocol:req.headers['mcp-protocol-version'],session:req.headers['mcp-session-id'],authorization:req.headers.authorization,lastId});break;
                    case 'expired':res.writeHead(404);res.end('expired');break;
                    case 'accepted':res.writeHead(202);res.end();break;
                    case 'unauthorized':res.writeHead(401,{'www-authenticate':'Bearer scope="read"'});res.end('login');break;
                    case 'wrong-content':res.writeHead(200,{'content-type':'text/plain'});res.end('not JSON');break;
                    default:result({ok:true});break;
                  }
                });
                server.listen(0,'127.0.0.1',()=>process.stdout.write(server.address().port+'\n'));
                """);
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(script);
            var process = Process.Start(start)!;
            try
            {
                var port = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                return new(root, process, new Uri("http://127.0.0.1:" + int.Parse(port!) + "/mcp"));
            }
            catch { if (!process.HasExited) process.Kill(true); process.Dispose(); Directory.Delete(root, true); throw; }
        }
        /// <summary>【CodingAgent】【MCP HTTP 测试清理】只终止本测试创建的进程并删除其临时目录。</summary><returns>清理任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(); _process.Dispose(); Directory.Delete(_root, true);
        }
    }
}
