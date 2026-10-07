// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【回合草稿】跨模块逐步预览并提交状态、上下文编辑及自定义消息，继续请求使用最终投影</summary>
    /// <param name="persistent">是否验证 JSONL 持久化</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnBoundary_PreviewsAcrossHandlersAndCommitsBeforeContinuation(bool persistent)
    {
        using var fixture = new Fixture("boundary-drafts", "models");
        WriteBoundaryExtension(fixture, "a.js", """
            export default pi=>{
              pi.on('turn_end',(event,ctx)=>{
                if(event.turnIndex!==0)return;
                if(event.outcome!=='completed'||!event.messageEntryId||event.toolResultEntryIds.length)throw Error('missing turn source');
                if(!ctx.sessionManager.getEntry(event.messageEntryId))throw Error('assistant was not persisted');
                if(event.entries.length||event.continue||event.context.canContinue)throw Error('invalid initial preview');
                return {entries:[{type:'custom',customType:'boundary-state',data:{count:1}}]};
              });
              pi.on('turn_end',(event,ctx)=>{
                if(event.turnIndex!==0)return;
                if(event.entries.length!==1||!event.context.contextEntries.some(e=>e.sourceEntry.customType==='boundary-state'))throw Error('missing staged state');
                if(ctx.sessionManager.getBranch().some(e=>e.customType==='boundary-state'))throw Error('preview was persisted');
                event.entries.push({type:'context_edit',targetId:event.messageEntryId,replacement:null});
                event.entries.push({type:'custom_message',customType:'boundary-request',content:'continue from draft',display:true,details:{from:'boundary'}});
                return {continue:true};
              });
            };
            """);
        WriteBoundaryExtension(fixture, "b.js", """
            export default pi=>pi.on('turn_end',(event,ctx)=>{
              if(event.turnIndex!==0)return;
              if(event.entries.length!==3||!event.continue||!event.context.canContinue)throw Error('cross-module drafts lost');
              const assistant=event.context.contextEntries.find(e=>e.sourceEntry.id===event.messageEntryId);
              if(!assistant||assistant.messages.length!==0||assistant.sourceEntry.message.role!=='assistant')throw Error('edit lost original source');
              if(event.context.contextMessages.at(-1).role!=='custom'||event.context.llmMessages.at(-1).role!=='user')throw Error('incorrect LLM projection');
            });
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, persistent, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal([0, 1], events.OfType<TurnEndEvent>().Select(item => item.TurnIndex));
        var entries = events.OfType<CodingAgentEntryAppendedEvent>().Select(item => item.Entry).ToArray();
        Assert.Equal(["custom", "context_edit", "custom_message"], entries.Select(item => item.GetProperty("type").GetString()));
        Assert.Single(session.Messages.OfType<AssistantMessage>());
        Assert.Equal("continue from draft", Assert.IsType<TextContent>(Assert.Single(session.Messages.OfType<AgentCustomMessage>()).Content.Single()).Text);
        Assert.Contains("continue from draft", fixture.Handler.Requests[1].Body.GetRawText());
        if (persistent)
        {
            var restored = session.TreeSessionController!.LoadSnapshot();
            Assert.Single(restored.Messages.OfType<AssistantMessage>());
            Assert.Single(restored.Messages.OfType<AgentCustomMessage>());
            Assert.Contains("\"replacement\":null", File.ReadAllText(session.TreeSessionController.Store.Path));
        }
    }

    /// <summary>【CodingAgent】【草稿修复】最终无效时整组不写入，后续模块可以替换前一个模块的无效草稿</summary>
    /// <param name="repair">后续模块是否修复草稿</param><param name="nullDrafts">是否返回非数组草稿</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TurnBoundary_RejectsWholeInvalidBatchAndAllowsLaterRepair(bool repair, bool nullDrafts)
    {
        using var fixture = new Fixture("boundary-validation", "models");
        WriteBoundaryExtension(fixture, "a.js", """
            export default pi=>pi.on('turn_end',()=>({entries:__NULL__?null:[
              {type:'custom',customType:'must-not-partially-save',data:1},
              {type:'context_edit',targetId:'missing',replacement:null}],continue:true}));
            """.Replace("__NULL__", nullDrafts.ToString().ToLowerInvariant()));
        if (repair) WriteBoundaryExtension(fixture, "b.js", """
            export default pi=>pi.on('turn_end',event=>{
              if((event.entries!==null&&event.entries.length!==2)||!event.continue)throw Error('invalid draft not forwarded');
              return {entries:[{type:'custom',customType:'repaired',data:2}],continue:false};
            });
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, true, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Single(fixture.Handler.Requests);
        var entries = events.OfType<CodingAgentEntryAppendedEvent>().ToArray();
        if (repair) Assert.Equal("repaired", Assert.Single(entries).Entry.GetProperty("customType").GetString());
        else Assert.Empty(entries);
        Assert.DoesNotContain("must-not-partially-save", File.ReadAllText(session.TreeSessionController!.Store.Path));
        Assert.Contains(log.Events, item => item.Event == "event.error" && item.Fields.Values.Any(value => value?.Contains("Invalid boundary entries") == true));
    }

    /// <summary>【CodingAgent】【边界压缩】压缩草稿预览与提交使用同一投影，并保留来源及扩展用量</summary>
    /// <param name="persistent">是否持久保存</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnBoundary_CompactionDraftReplacesContextAndContinues(bool persistent)
    {
        using var fixture = new Fixture("boundary-compaction", "models");
        WriteBoundaryExtension(fixture, "compact.js", """
            export default pi=>{
              pi.on('turn_end',event=>{
                if(event.turnIndex===0)return {entries:[{type:'compaction',summary:'boundary compacted',firstKeptEntryId:null,
                  details:{test:true},usage:{input:7,output:3,totalTokens:10}}],continue:true};
              });
              pi.on('turn_end',event=>{
                if(event.turnIndex!==0)return;
                const summary=event.context.contextMessages.find(m=>m.role==='compactionSummary');
                if(!summary||summary.summary!=='boundary compacted'||!event.context.canContinue)throw Error('missing compaction preview');
                if(event.context.contextEntries.filter(e=>e.sourceEntry.type==='compaction').length!==1)throw Error('missing compaction source');
              });
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, persistent, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("discarded initial"));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
        Assert.Equal(2, fixture.Handler.Requests.Count);
        var entry = Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>()).Entry;
        Assert.Equal("compaction", entry.GetProperty("type").GetString());
        Assert.True(entry.GetProperty("fromHook").GetBoolean());
        Assert.Equal(7, entry.GetProperty("usage").GetProperty("input").GetInt32());
        Assert.Contains("boundary compacted", fixture.Handler.Requests[1].Body.GetRawText());
        Assert.DoesNotContain("discarded initial", fixture.Handler.Requests[1].Body.GetRawText());
        Assert.Single(session.Messages.OfType<AssistantMessage>());
    }

    /// <summary>【CodingAgent】【边界排队】同一模块后续处理器看见即时入队的选中消息，所有消息只发送一次</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task TurnBoundary_PreviewIncludesHandlerDeliveriesWithoutConsumingThem()
    {
        using var fixture = new Fixture("boundary-queue", "models");
        WriteBoundaryExtension(fixture, "queue.js", """
            export default pi=>{
              pi.on('turn_end',event=>{
                if(event.turnIndex!==0)return;
                pi.sendUserMessage('follow-one',{deliverAs:'followUp'});
                pi.sendUserMessage('steer-one',{deliverAs:'steer'});
                pi.sendUserMessage('follow-two',{deliverAs:'followUp'});
                pi.sendUserMessage('steer-two',{deliverAs:'steer'});
              });
              pi.on('turn_end',event=>{
                if(event.turnIndex!==0)return;
                if(!event.context.canContinue||event.context.pendingMessages.length!==1||
                   !JSON.stringify(event.context.pendingMessages[0].content).includes('steer-one'))throw Error('pending selection mismatch');
              });
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, false, log);
        session.Runner.FollowUpMode = AgentQueueMode.All;
        await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
        Assert.Equal(4, fixture.Handler.Requests.Count);
        Assert.Equal(["initial", "steer-one", "steer-two", "follow-one", "follow-two"], session.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
    }

    /// <summary>【CodingAgent】【边界失败】失败和中止回合仍提交草稿并报告实际结果，但不因继续标志发起额外请求</summary>
    /// <param name="reason">错误或中止原因</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData("error")]
    [InlineData("aborted")]
    public async Task TurnBoundary_FailedTurnsStillPersistDraftsWithoutContinuing(string reason)
    {
        using var fixture = new Fixture("boundary-failure", "models");
        WriteBoundaryExtension(fixture, "failure.js", """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let calls=0;
              pi.registerProvider('boundary-outcome',{api:'boundary-outcome',baseUrl:'https://fixture.test',apiKey:'fixture',models:[{id:'test',name:'Test',contextWindow:100000,maxTokens:1000}],
                streamSimple:model=>{
                  if(++calls>1)throw Error('unexpected continuation');
                  const output=createAssistantMessageEventStream();
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,content:[],stopReason:'__REASON__',
                    errorMessage:'fixture failure',timestamp:Date.now(),usage:{input:1,output:0,cacheRead:0,cacheWrite:0,totalTokens:1,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                  output.push({type:'error',reason:'__REASON__',error:message});return output;
                }});
              pi.on('turn_end',(event,ctx)=>{
                if(event.outcome!=='__REASON__'||!ctx.sessionManager.getEntry(event.messageEntryId))throw Error('failure source missing');
                return {entries:[{type:'custom_message',customType:'failure-draft',content:'possible continuation',display:false}],continue:true};
              });
            };
            """.Replace("__REASON__", reason));
        var log = new BoundaryLogSink();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true,
            ProviderId = "boundary-outcome", ModelId = "test", NoTools = CodingAgentSdkNoToolsMode.All, LogSink = log
        });
        session.Runner.RetryOptions = CodingAgentRetryOptions.Disabled;
        session.Runner.SetAutoCompactionEnabled(false);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Single(events.OfType<TurnEndEvent>());
        Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>());
        Assert.Single(session.Messages.OfType<AgentCustomMessage>());
        Assert.DoesNotContain(log.Events, item => item.Event == "event.error");
    }

    /// <summary>【CodingAgent】【边界异常】处理器原地修改后抛错仍保留草稿，后续处理器执行；无可执行上下文时拒绝继续</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task TurnBoundary_PreservesMutationOnFailureAndRejectsEmptyContinuation()
    {
        using var fixture = new Fixture("boundary-handler-error", "models");
        WriteBoundaryExtension(fixture, "throw.js", """
            export default pi=>{
              pi.on('turn_end',event=>{event.entries.push({type:'custom',customType:'kept-mutation',data:1});throw Error('fixture handler failure');});
              pi.on('turn_end',event=>{
                if(event.entries.length!==1||!event.context.contextEntries.some(e=>e.sourceEntry.customType==='kept-mutation'))throw Error('mutation lost');
                return {continue:true};
              });
            };
            """);
        var log = new BoundaryLogSink();
        await using var session = await CreateBoundarySessionAsync(fixture, false, log);
        var events = await CollectBoundaryEventsAsync(session.RunAsync("initial"));
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal("kept-mutation", Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>()).Entry.GetProperty("customType").GetString());
        var errors = log.Events.Where(item => item.Event == "event.error").ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, item => item.Fields.Values.Any(value => value?.Contains("fixture handler failure") == true));
        Assert.Contains(errors, item => item.Fields.Values.Any(value => value?.Contains("without runnable model context") == true));
    }

    /// <summary>【CodingAgent】【边界夹具】保存独立模块，按文件名确定处理器顺序</summary>
    /// <param name="fixture">隔离目录</param><param name="name">模块文件名</param><param name="source">模块源码</param>
    private static void WriteBoundaryExtension(Fixture fixture, string name, string source)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), source);
    }

    /// <summary>【CodingAgent】【边界夹具】创建使用模拟 HTTP、真实扩展进程和可选 JSONL 的 SDK 会话</summary>
    /// <param name="fixture">隔离目录及 HTTP 记录器</param><param name="persistent">是否保存 JSONL</param>
    /// <param name="log">错误记录器</param><returns>测试会话</returns>
    private static async Task<CodingAgentSdkSession> CreateBoundarySessionAsync(Fixture fixture, bool persistent, BoundaryLogSink log)
    {
        var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = !persistent,
            SessionPath = persistent ? Path.Combine(fixture.Root, "boundary.jsonl") : null,
            ProviderId = "session-provider", ModelId = "test-model", ProviderRegistry = fixture.Options().ProviderRegistry,
            ThinkingLevel = ThinkingLevel.Off, NoTools = CodingAgentSdkNoToolsMode.All, LogSink = log
        });
        session.Runner.SetAutoCompactionEnabled(false);
        return session;
    }

    /// <summary>【CodingAgent】【边界夹具】收集完整事件流并限制意外无限继续</summary>
    /// <param name="source">运行事件</param><returns>按发生顺序排列的事件列表</returns>
    private static async Task<List<AgentEvent>> CollectBoundaryEventsAsync(IAsyncEnumerable<AgentEvent> source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = new List<AgentEvent>();
        await foreach (var item in source.WithCancellation(deadline.Token))
        {
            events.Add(item);
            Assert.True(events.OfType<TurnEndEvent>().Count() < 8, "Unexpected continuation loop");
        }
        return events;
    }

    /// <summary>【CodingAgent】【边界诊断】记录处理器错误，防止异常隔离造成测试假成功</summary>
    private sealed class BoundaryLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = [];
        /// <summary>【CodingAgent】【边界诊断】保存原始日志</summary>
        /// <param name="logEvent">运行日志</param>
        public void Log(TauLogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }
}
