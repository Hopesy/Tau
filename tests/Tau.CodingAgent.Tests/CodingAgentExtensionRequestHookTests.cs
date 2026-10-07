// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【请求钩子回归】验证实际 Node 与请求转换的顺序、隔离及协议元数据。</summary>
public sealed class CodingAgentExtensionRequestHookTests
{
    /// <summary>未裁剪或仅原位修改消息时，中途系统消息保持原有位置。</summary>
    /// <param name="operation">普通上下文处理器操作。</param>
    [Theory]
    [InlineData("return { messages: [...e.messages] };")]
    [InlineData("e.messages[0].content[0].text = 'mutated';")]
    public void Context_UnchangedShapePreservesSystemPositions(string operation)
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("module.exports = pi => pi.on('context', e => { if (e.messages.some(m => m.role === 'system')) throw Error('system visible'); " + operation + " });");
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var original = History();
        var result = sink.TransformRequestContext(original, errors.Add, default);
        Assert.Empty(errors);
        Assert.Equal(original.Select(message => message.Role), result.Select(message => message.Role));
        Assert.Equal("base", Assert.IsType<SystemMessage>(result[0]).Content);
        Assert.Equal("update", Assert.IsType<SystemMessage>(result[2]).Content);
        Assert.Equal("first", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(original[1]).Content)).Text);
    }

    /// <summary>裁剪会话后重放系统段落及工具增删，下一处理器只能看到保留的对话。</summary>
    [Fact]
    public void Context_PrunesConversationAndRestoresCurrentDeclarations()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("""
            module.exports = pi => {
              pi.on('context', e => ({ messages: e.messages.slice(-1) }));
              pi.on('context', e => { if (e.messages.length !== 1 || e.messages[0].role !== 'assistant') throw Error('sequence'); });
            };
            """);
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = sink.TransformRequestContext(History(), errors.Add, default);
        Assert.Empty(errors);
        Assert.Equal(["system", "assistant"], result.Select(message => message.Role));
        var head = Assert.IsType<SystemMessage>(result[0]);
        Assert.Equal("base\n\nupdate", head.Content);
        Assert.Equal("new section", head.Sections!["section"]);
        Assert.False(head.Sections.ContainsKey("removed"));
        Assert.Equal(["new"], head.ToolsAdded!.Select(tool => tool.Name));
    }

    /// <summary>全部普通上下文处理器先于完整上下文处理器，跨模块注册顺序仍保持。</summary>
    [Fact]
    public void Context_PhasesRunAcrossAllModules()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("""
            module.exports = pi => {
              pi.on('context_with_system', e => { e.messages[0].content = 'full phase'; });
              pi.on('context', e => { e.messages.push({role:'user',content:'module one'}); });
            };
            """, """
            module.exports = pi => pi.on('context', e => {
              const content = e.messages.at(-1).content;
              if (e.messages.some(m => m.role === 'system') || (typeof content === 'string' ? content : content[0].text) !== 'module one') throw Error('phase order');
              e.messages.push({role:'user',content:'module two'});
            });
            """);
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = sink.TransformRequestContext(History(), errors.Add, default);
        Assert.Empty(errors);
        Assert.Equal("full phase", Assert.IsType<SystemMessage>(result[0]).Content);
        Assert.Equal("module two", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(result[^1]).Content)).Text);
    }

    /// <summary>完整上下文钩子移除系统声明时报告问题，同时尊重其返回结果。</summary>
    [Fact]
    public void ContextWithSystem_CanRemovePromptAndTools()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("module.exports = pi => pi.on('context_with_system', e => ({messages:e.messages.filter(m => m.role !== 'system')}));");
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = sink.TransformRequestContext(History(), errors.Add, default);
        Assert.DoesNotContain(result, message => message is SystemMessage);
        Assert.Contains("removed the leading system", Assert.Single(errors).Error);
    }

    /// <summary>处理器异常与非法返回值不会阻止后续处理器正常修改消息。</summary>
    /// <param name="operation">第一个处理器的错误行为。</param>
    [Theory]
    [InlineData("throw Error('synthetic failure');")]
    [InlineData("return {messages:42};")]
    public void Context_HandlerFailuresAreIsolated(string operation)
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("module.exports = pi => { pi.on('context', e => {" + operation + "}); pi.on('context', e => ({messages:e.messages.slice(-1)})); };");
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = sink.TransformRequestContext(History(), errors.Add, default);
        Assert.Single(errors);
        Assert.Equal(["system", "assistant"], result.Select(message => message.Role));
    }

    /// <summary>取消在调用 Node 之前生效。</summary>
    [Fact]
    public void Context_CancellationSkipsHandlers()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("module.exports = pi => pi.on('context', () => { throw Error('must not run'); });");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => sink.TransformRequestContext(History(), _ => Assert.Fail(), cancellation.Token));
    }

    /// <summary>消息结束回调返回展开原消息时，响应信息、推理签名及工具详情不会丢失。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task MessageEnd_RoundTripPreservesMetadata()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("module.exports = pi => pi.on('message_end', e => ({message:{...e.message}}));");
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1791072000000);
        var assistant = new AssistantMessage([new TextContent("answer") { TextSignature = "text-sig" },
            new ThinkingContent("thought") { ThinkingSignature = "thinking-sig", Redacted = true }])
        {
            Provider = "test", Model = "model", Api = "api", Usage = new(10, 2, 3, 4) { TotalTokens = 19 },
            ResponseId = "response", ResponseModel = "resolved", RawStopReason = "raw", StopReason = StopReason.Deferred, Timestamp = timestamp
        };
        var result = await sink.TransformMessageEndAsync(assistant);
        Assert.Empty(result.Errors);
        var message = Assert.IsType<AssistantMessage>(result.Message);
        Assert.Equal(assistant.Usage, message.Usage);
        Assert.Equal("response", message.ResponseId);
        Assert.Equal("resolved", message.ResponseModel);
        Assert.Equal(StopReason.Deferred, message.StopReason);
        Assert.Equal("raw", message.RawStopReason);
        Assert.Equal(timestamp, message.Timestamp);
        Assert.Equal("text-sig", Assert.IsType<TextContent>(message.Content[0]).TextSignature);
        Assert.Equal("thinking-sig", Assert.IsType<ThinkingContent>(message.Content[1]).ThinkingSignature);
        var tool = new ToolResultMessage("call", [new TextContent("result")]) { ToolName = "read", Details = new { count = 2 }, Timestamp = timestamp };
        var toolResult = Assert.IsType<ToolResultMessage>((await sink.TransformMessageEndAsync(tool)).Message);
        Assert.Equal("read", toolResult.ToolName);
        Assert.Equal(2, Assert.IsType<JsonElement>(toolResult.Details).GetProperty("count").GetInt32());
        Assert.Equal(timestamp, toolResult.Timestamp);
    }

    /// <summary>真实 Provider 请求串联已有回调与扩展请求体、响应事件，连续运行不叠加回调。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ProviderHooks_ReachActualHttpRequestWithoutChangingHistory()
    {
        using var fixture = new Fixture();
        var sink = fixture.Sink("""
            module.exports = pi => {
              let responses = 0, streamEvents = 0;
              pi.on('context', e => {
                if (e.messages.some(m => m.role === 'system')) throw Error('system exposed');
                e.messages.at(-1).content = 'request-only';
              });
              pi.on('context_with_system', e => { e.messages[0].content = 'request system'; });
              pi.on('before_provider_request', e => {
                if (e.payload.original !== 1) throw Error('previous callback missing');
                e.payload.chain = ['one'];
              });
              pi.on('before_provider_request', e => ({...e.payload, chain:[...e.payload.chain, 'two']}));
              pi.on('after_provider_response', e => {
                if (e.status !== 200 || e.headers['x-synthetic'] !== 'yes') throw Error('response');
                responses++;
              });
              pi.on('before_provider_headers', e => {
                e.headers['X-Synthetic'] = 'extension';
                e.headers['X-Remove'] = null;
                return { ignored: 'return value' };
              });
              pi.on('provider_stream_event', e => {
                if (responses < 1 || e.provider !== 'test' || e.model !== 'synthetic' || e.api !== 'openai-responses') throw Error('stream event');
                streamEvents++;
                e.data.type = 'ignored mutation';
              });
              pi.registerCommand('count', {handler:() => String(responses)});
              pi.registerCommand('stream-count', {handler:() => String(streamEvents)});
            };
            """, """
            module.exports = pi => pi.on('before_provider_request', e => {
              if (e.payload.chain.join(',') !== 'one,two') throw Error('module order');
              e.payload.chain.push('three');
            });
            """);
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var registry = new ProviderRegistry();
        registry.Register("openai-responses", new OpenAiResponsesProvider(client));
        var configuration = new ModelConfigurationStore([Path.Combine(fixture.Root, "models.json")]);
        var auth = new ProviderAuthResolver(configurationStore: configuration, credentialStore: new OAuthCredentialStore([Path.Combine(fixture.Root, "auth.json")]));
        var model = new Model { Id = "synthetic", Name = "Synthetic", Provider = "test", Api = "openai-responses", BaseUrl = "https://synthetic.invalid" };
        var catalog = new ModelCatalog(auth, configuration);
        catalog.RegisterModel(model);
        var payloadCount = 0;
        var responseCount = 0;
        var runner = new RuntimeCodingAgentRunner(new AgentRuntime(), new AgentLoopConfig
        {
            Model = model, ProviderRegistry = registry, Tools = [], SystemPrompt = "saved system",
            StreamOptions = new()
            {
                ApiKey = "synthetic-key",
                Headers = new Dictionary<string, string> { ["X-Remove"] = "original" },
                OnPayload = (payload, _) =>
                {
                    payloadCount++;
                    Assert.IsType<Dictionary<string, object>>(payload)["original"] = 1;
                    return ValueTask.FromResult<object?>(payload);
                },
                OnResponse = (_, _) => { responseCount++; return ValueTask.CompletedTask; }
            }
        }, catalog, extensionLifecycleEventSink: sink, workingDirectory: fixture.Root);
        fixture.Runtime.BindSession(runner);
        foreach (var input in new[] { "saved one", "saved two" })
            await foreach (var item in runner.RunAsync(input))
                if (item is AgentEndEvent end) Assert.Null(end.ErrorMessage);
        Assert.Equal(2, payloadCount);
        Assert.Equal(2, responseCount);
        Assert.Equal("2", fixture.Runtime.Invoke(fixture.Files[0], "count", "").StatusMessage);
        Assert.Equal("4", fixture.Runtime.Invoke(fixture.Files[0], "stream-count", "").StatusMessage);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal(["one", "two", "three"], request.GetProperty("chain").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("request system", request.GetRawText());
            Assert.Contains("request-only", request.GetRawText());
        }
        Assert.Equal(["saved one", "saved two"], runner.Messages.OfType<UserMessage>().Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
        Assert.Equal("saved system", Assert.Single(runner.Messages.OfType<SystemMessage>()).Content);
    }

    /// <summary>生成含中途系统更新和工具增删的原始会话。</summary>
    /// <returns>系统状态与普通消息交错的测试历史。</returns>
    private static IReadOnlyList<ChatMessage> History()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        return [
            new SystemMessage("base") { Sections = new Dictionary<string,string?> { ["section"] = "old", ["removed"] = "old" },
                ToolsAdded = [new Tool("old", "Old", schema.RootElement.Clone())] },
            new UserMessage("first"),
            new SystemMessage("update") { Sections = new Dictionary<string,string?> { ["section"] = "new section", ["removed"] = null },
                ToolsAdded = [new Tool("new", "New", schema.RootElement.Clone())], ToolsRemoved = [new ToolReference("old")] },
            new AssistantMessage([new TextContent("answer")])
        ];
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-request-hooks-" + Guid.NewGuid().ToString("N"));
        public CodingAgentJavaScriptExtensionRuntime Runtime { get; }
        public List<string> Files { get; } = [];
        /// <summary>创建隔离目录和持久 Node。</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Runtime = new(Root);
        }
        /// <summary>按顺序写入、加载扩展，使用实际注册信息构造事件接收器。</summary>
        /// <param name="sources">扩展代码。</param>
        /// <returns>事件接收器。</returns>
        public CodingAgentExtensionLifecycleEventSink Sink(params string[] sources)
        {
            var modules = new List<CodingAgentExtensionLifecycleEventModule>();
            foreach (var source in sources)
            {
                var path = Path.Combine(Root, Files.Count + ".js");
                Files.Add(path);
                File.WriteAllText(path, source);
                var loaded = Runtime.Load(path);
                Assert.True(loaded.Success, loaded.Error);
                Assert.Equal(0, loaded.Unsupported.Handlers);
                modules.Add(new(path, "test", "javascript", loaded.EventHandlerTypes));
            }
            return new(modules, Runtime);
        }
        /// <summary>释放 Node 并清理经过范围核验的专用临时目录。</summary>
        public void Dispose()
        {
            Runtime.Dispose();
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("tau-request-hooks-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(path, true);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        /// <summary>记录完整 HTTP 报文并返回带响应头的 Responses SSE。</summary>
        /// <param name="request">实际请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟服务响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("extension", request.Headers.GetValues("X-Synthetic").Single());
            Assert.False(request.Headers.Contains("X-Remove"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg","content":[{"type":"output_text","text":"answer"}]}}

                    data: {"type":"response.completed","response":{"status":"completed"}}


                    """, Encoding.UTF8, "text/event-stream")
            };
            response.Headers.Add("x-synthetic", "yes");
            return response;
        }
    }
}
