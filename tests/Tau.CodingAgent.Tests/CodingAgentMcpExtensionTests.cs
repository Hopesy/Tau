// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【MCP 扩展事务】工厂注册按归属隔离，失败回滚不会留下服务器或破坏已有定义。</summary>
    [Fact]
    public void McpExtensionRegistrationValidatesOwnershipAndRollsBackFactoryFailure()
    {
        using var fixture = new Fixture("mcp-registration", "models");
        using var runtime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
        var owner = Path.Combine(fixture.Root, "owner.js");
        File.WriteAllText(owner, """
            export default pi=>{
              const config={command:'node',exposure:'codemode-deferred'};
              pi.registerMcpServer('my-server',config);config.command='changed';
              const snapshot=pi.getMcpServers();snapshot[0].config.command='mutated';
              if(pi.getMcpServers()[0].config.command!=='node'||pi.getMcpServers()[0].config.exposure!=='codemode')throw Error('mutable registry');
            };
            """);
        Assert.True(runtime.Load(owner).Success);
        var failed = Path.Combine(fixture.Root, "failed.js");
        File.WriteAllText(failed, """
            export default pi=>{
              pi.registerMcpServer('temporary',{command:'node'});
              pi.unregisterMcpServer('my-server');
              let ownership=false,collision=false,invalid=false;
              try{pi.registerMcpServer('my-server',{command:'other'});}catch(e){ownership=e.message.includes('already registered');}
              try{pi.registerMcpServer('my_server',{command:'other'});}catch(e){collision=e.message.includes('conflicts');}
              try{pi.registerMcpServer('invalid',{url:'http://example.test',auth:{provider:'test'}});}catch(e){invalid=e.message.includes('auth requires');}
              if(!ownership||!collision||!invalid)throw Error('validation missing');
              throw Error('factory failed after registrations');
            };
            """);
        var failure = runtime.Load(failed);
        Assert.False(failure.Success);
        Assert.Contains("factory failed after registrations", failure.Error);
        var observer = Path.Combine(fixture.Root, "observer.js");
        File.WriteAllText(observer, """
            export default pi=>{const all=pi.getMcpServers();if(all.length!==1||all[0].name!=='my-server'||all[0].config.command!=='node')throw Error('rollback failed');};
            """);
        Assert.True(runtime.Load(observer).Success);
        var registered = Assert.Single(runtime.McpServers.List());
        Assert.Equal("my-server", registered.Name);
        Assert.Equal(owner, registered.Source);
        runtime.Reset();
        Assert.Empty(runtime.McpServers.List());
    }

    /// <summary>【CodingAgent】【MCP 动态变更】实时注册、替换和注销依次通知扩展，事件服务器列表保持当时的独立快照。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpExtensionChangesReachSessionHandlersWithoutBlockingCommands()
    {
        using var fixture = new Fixture("mcp-events", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mcp.js"), """
            export default pi=>{
              const changes=[];
              pi.registerMcpServer('first',{command:'node'});
              pi.on('mcp_servers_change',event=>{
                changes.push(event.servers.map(server=>server.name+':'+(server.config.args?.[0]??'initial')).join(','));
                event.servers[0].config.command='must not leak';
              });
              pi.registerCommand('mcp-change',{handler:()=>{
                pi.registerMcpServer('second',{url:'http://127.0.0.1:1234/mcp',exposure:'direct'});
                pi.registerMcpServer('first',{command:'node',args:['updated']});
                pi.unregisterMcpServer('second');
                if(pi.getMcpServers().length!==1)throw Error('local removal missing');
              }});
              pi.registerCommand('mcp-check',{handler:()=>{
                if(JSON.stringify(changes)!==JSON.stringify(['first:initial,second:initial','first:updated,second:initial','first:updated']))throw Error('wrong changes '+JSON.stringify(changes));
                if(pi.getMcpServers()[0].config.command!=='node')throw Error('mutable event');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }, deadline.Token);
        await DrainAsync(session.RunAsync("/mcp-change", deadline.Token));
        await ((RuntimeCodingAgentRunner)session.Runner).WaitForStateNotificationsAsync();
        await DrainAsync(session.RunAsync("/mcp-check", deadline.Token));
    }
}
