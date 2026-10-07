// 作者：xxx
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【MCP】【标准错误诊断】真实 Node 服务器启动失败及运行期退出都在状态中保留有界 stderr 尾部。</summary>
    /// <param name="duringInitialization">是否在初始化前退出。</param><param name="lateError">是否先结束 stdout 再写最后错误。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task McpStderrTailIsIncludedInFailedAndDisconnectedStatus(bool duringInitialization, bool lateError)
    {
        using var temp = TempDirectory.Create();
        var script = Path.Combine(temp.Path, "stderr-server.cjs");
        File.WriteAllText(script, """
            // 作者：xxx
            const readline=require('node:readline');
            // 1. 【MCP】【测试退出】模拟 stdout 和 stderr 独立结束，诊断仍必须包含最后写出的尾部
            const fail=()=>{
              const write=()=>process.stderr.write('START-MARKER'+ 'x'.repeat(5000)+'\nTAIL-END\n',()=>process.exit(3));
              if(process.argv[3]==='late')process.stdout.end(()=>setTimeout(write,25));else write();
            };
            if(process.argv[2]==='initialize')fail();
            else readline.createInterface({input:process.stdin}).on('line',line=>{
              const request=JSON.parse(line);
              if(request.method==='tools/call'){fail();return;}
              if(request.id===undefined)return;
              const result=request.method==='initialize'
                ?{protocolVersion:'2025-11-25',capabilities:{tools:{}},serverInfo:{name:'fixture',version:'1'}}
                :{tools:[{name:'exit',inputSchema:{type:'object'}}]};
              console.log(JSON.stringify({jsonrpc:'2.0',id:request.id,result}));
            });
            """);
        var registry = new CodingAgentMcpServerRegistry();
        registry.Register("fixture", new() { ["command"] = "node", ["args"] = new JsonArray(script, duringInitialization ? "initialize" : "call", lateError ? "late" : "normal") }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.ReloadAsync(deadline.Token); await service.WaitForServersAsync(deadline.Token);
        if (!duringInitialization)
        {
            var version = service.Version;
            await Assert.ThrowsAnyAsync<IOException>(() => Assert.Single(service.GetTools()).ExecuteAsync("exit", ParseMcpSessionJson("{}"), deadline.Token));
            while (Assert.Single(service.GetStatus()).State == "connected") await Task.Delay(10, deadline.Token);
            Assert.True(service.Version > version);
        }
        var status = Assert.Single(service.GetStatus());
        Assert.Equal(duringInitialization ? "failed" : "disconnected", status.State);
        Assert.NotNull(status.Error); Assert.EndsWith("\nTAIL-END", status.Error);
        Assert.DoesNotContain("START-MARKER", status.Error);
        Assert.Equal(2000, status.Error[(status.Error.IndexOf('\n') + 1)..].Length);
        var reports = await service.GetReportsAsync(deadline.Token);
        Assert.Equal(status.Error, reports[0]!["error"]!.GetValue<string>());
    }
}
