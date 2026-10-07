// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【结算顺序】结算边界与 agent_end 队列属于同一次活动，结算处理器的新提示开始新活动</summary>
    /// <param name="source">后续请求的产生位置</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData("agent_end")]
    [InlineData("agent_before_settle")]
    [InlineData("agent_settled")]
    public async Task Settlement_FollowsContinuationAndDefersNewSettledPrompts(string source)
    {
        using var fixture = new Fixture("settlement-order", "models");
        WriteBoundaryExtension(fixture, "settlement.js", """
            import fs from 'node:fs';
            export default pi=>{
              const source='__SOURCE__';
              let starts=0,ends=0,befores=0,settled=0;
              const events=[];
              pi.on('before_agent_start',()=>{starts++;return {systemPrompt:'retained boundary prompt'};});
              pi.on('agent_end',()=>{
                events.push('end');ends++;
                if(source==='agent_end'&&ends===1)pi.sendUserMessage('queued after end',{deliverAs:'followUp'});
              });
              pi.on('agent_before_settle',(event,ctx)=>{
                events.push('before');befores++;
                if(ctx.isIdle()||event.outcome!=='completed')throw Error('before-settle state mismatch');
                if(source==='agent_before_settle'&&befores===1)return {entries:[
                  {type:'custom_message',customType:'settlement-request',content:'continue before settling',display:true}],continue:true};
              });
              pi.on('agent_settled',(_,ctx)=>{
                events.push('settled');settled++;
                if(!ctx.isIdle())throw Error('settled still busy');
                fs.writeFileSync('settlement.json',JSON.stringify({starts,ends,befores,settled,events}));
                if(source==='agent_settled'&&settled===1)pi.sendUserMessage('new settled prompt');
              });
            };
            """.Replace("__SOURCE__", source));
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, true, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests, request => Assert.Contains("retained boundary prompt", request.Body.GetRawText()));
        Assert.Equal(2, events.OfType<AgentEndEvent>().Count());
        Assert.Equal(source == "agent_settled" ? 2 : 1, events.OfType<CodingAgentSettledEvent>().Count());
        Assert.IsType<CodingAgentSettledEvent>(events.Last());
        Assert.False(session.Runner.IsStreaming);
        await session.Runner.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "settlement.json")));
        Assert.Equal(source == "agent_settled" ? 2 : 1, state.RootElement.GetProperty("starts").GetInt32());
        Assert.Equal(source == "agent_end" ? 1 : 2, state.RootElement.GetProperty("befores").GetInt32());
        var expected = source switch
        {
            "agent_end" => new[] { "end", "end", "before", "settled" },
            "agent_before_settle" => ["end", "before", "end", "before", "settled"],
            _ => ["end", "before", "settled", "end", "before", "settled"]
        };
        Assert.Equal(expected, state.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>【CodingAgent】【结算等待】外部等待和扩展命令等待都包含慢结算处理器，处理器内已显示空闲</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task Settlement_IdleWaitIncludesAwaitedHandlersAndCommandWaiters()
    {
        using var fixture = new Fixture("settlement-wait", "models");
        WriteBoundaryExtension(fixture, "wait.js", """
            import fs from 'node:fs';
            export default pi=>{
              pi.on('agent_settled',async(_,ctx)=>{
                if(!ctx.isIdle())throw Error('settled context must be idle');
                fs.writeFileSync('settling','');
                while(!fs.existsSync('release'))await new Promise(resolve=>setTimeout(resolve,5));
                fs.writeFileSync('settled-complete','');
              });
              pi.registerCommand('after-idle',{handler:async(_,ctx)=>{
                fs.writeFileSync('command-started','');
                await ctx.waitForIdle();
                if(!ctx.isIdle()||!fs.existsSync('settled-complete'))throw Error('wait completed before settlement');
                fs.writeFileSync('command-complete','');
              }});
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, false, log);
        var run = Task.Run(() => CollectBoundaryEventsAsync(session.RunAsync("initial")));
        await WaitForSettlementFileAsync(fixture, "settling");
        var idle = session.Runner.WaitForIdleAsync(default);
        var command = Task.Run(() => CollectBoundaryEventsAsync(session.RunAsync("/after-idle")));
        try
        {
            await WaitForSettlementFileAsync(fixture, "command-started");
            Assert.False(idle.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "command-complete")));
        }
        finally { File.WriteAllText(Path.Combine(fixture.Root, "release"), ""); }
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await command.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(events.OfType<CodingAgentSettledEvent>());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "command-complete")));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
    }

    /// <summary>【CodingAgent】【结算重试】失败尝试不提前结算，最终边界看到恢复成功的结果</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task Settlement_RetryProducesOneFinalNotification()
    {
        using var fixture = new Fixture("settlement-retry", "models");
        WriteBoundaryExtension(fixture, "retry.js", """
            import fs from 'node:fs';
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let calls=0,ends=0,befores=0;
              pi.registerProvider('settlement-retry',{api:'settlement-retry',baseUrl:'https://fixture.test',apiKey:'fixture',
                models:[{id:'test',name:'Test',contextWindow:100000,maxTokens:1000}],streamSimple:model=>{
                  const output=createAssistantMessageEventStream();
                  const failure=++calls===1;
                  if(calls>2)throw Error('unexpected extra request');
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,
                    content:[{type:'text',text:failure?'':'recovered'}],stopReason:failure?'error':'stop',errorMessage:failure?'overloaded_error':undefined,
                    timestamp:Date.now(),usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                  output.push(failure?{type:'error',reason:'error',error:message}:{type:'done',reason:'stop',message});return output;
                }});
              pi.on('agent_end',()=>ends++);
              pi.on('agent_before_settle',event=>{
                befores++;
                if(event.outcome!=='completed'||calls!==2)throw Error('settled before retry recovery');
              });
              pi.on('agent_settled',(_,ctx)=>{
                if(!ctx.isIdle()||ends!==2||befores!==1)throw Error('retry settlement mismatch');
                fs.writeFileSync('retry-settled','');
              });
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true,
            ProviderId = "settlement-retry", ModelId = "test", NoTools = CodingAgentSdkNoToolsMode.All, LogSink = log
        });
        session.Runner.RetryOptions = new(1, 1);
        session.Runner.SetAutoCompactionEnabled(false);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Equal([true, false], events.OfType<AgentEndEvent>().Select(item => item.WillRetry));
        Assert.Single(events.OfType<CodingAgentSettledEvent>());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "retry-settled")));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
    }

    /// <summary>【CodingAgent】【结算取消】处理器取消当前运行后仍提交其草稿并通知结算，但不执行继续请求</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task Settlement_AbortInsideBoundaryCommitsAndStopsContinuation()
    {
        using var fixture = new Fixture("settlement-abort", "models");
        WriteBoundaryExtension(fixture, "abort.js", """
            import fs from 'node:fs';
            export default pi=>{
              pi.on('agent_before_settle',(_,ctx)=>{
                ctx.abort();
                return {entries:[{type:'custom_message',customType:'cancelled-boundary',content:'retained draft',display:false}],continue:true};
              });
              pi.on('agent_settled',(_,ctx)=>{if(!ctx.isIdle())throw Error('not idle after cancel');fs.writeFileSync('cancel-settled','');});
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, true, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>());
        Assert.Single(events.OfType<CodingAgentSettledEvent>());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "cancel-settled")));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
        await session.Runner.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>【CodingAgent】【结算空上下文】移除全部非系统上下文后，队列本身不足以自动继续结算边界</summary>
    /// <param name="requestContinue">是否显式请求继续并预期诊断</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settlement_EmptyContextRetainsQueueWithoutStartingInvalidRequest(bool requestContinue)
    {
        using var fixture = new Fixture("settlement-empty", "models");
        WriteBoundaryExtension(fixture, "empty.js", """
            export default pi=>{
              pi.on('agent_before_settle',event=>{
                pi.sendUserMessage('keep queued',{deliverAs:'followUp'});
                return {entries:event.context.contextEntries.filter(e=>e.sourceEntry.type==='message'&&['user','assistant'].includes(e.sourceEntry.message.role))
                  .map(e=>({type:'context_edit',targetId:e.sourceEntry.id,replacement:null})),continue:__CONTINUE__};
              });
              pi.on('agent_before_settle',event=>{
                if(event.context.canContinue||event.context.pendingMessages.length!==1)throw Error('invalid empty-context preview');
              });
            };
            """.Replace("__CONTINUE__", requestContinue.ToString().ToLowerInvariant()));
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, false, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(events.OfType<CodingAgentSettledEvent>());
        Assert.Equal(1, session.Runner.PendingMessageCount);
        Assert.DoesNotContain(session.Messages, message => message is UserMessage or AssistantMessage);
        var errors = log.Events.Where(item => item.Event == "event.error").ToArray();
        if (requestContinue) Assert.Contains("without runnable model context", string.Join(" ", Assert.Single(errors).Fields.Values));
        else Assert.Empty(errors);
    }

    /// <summary>【CodingAgent】【结算夹具】等待真实扩展进程发出的文件屏障，使用有界取消防止测试挂起</summary>
    /// <param name="fixture">隔离目录</param><param name="name">屏障文件名</param><returns>屏障出现后完成的任务</returns>
    private static async Task WaitForSettlementFileAsync(Fixture fixture, string name)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(fixture.Root, name))) await Task.Delay(10, deadline.Token);
    }
}
