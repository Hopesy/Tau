// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【扩展控制回归】验证 Node 控制 API 对真实运行器和请求的影响。</summary>
public sealed partial class CodingAgentExtensionRuntimeControlTests
{
    /// <summary>模型切换及目录读取使用宿主目录，缺少认证时不切换，成功后立即看到新状态。</summary>
    [Fact]
    public void Models_UseActualCatalogAndAuthentication()
    {
        using var fixture = new Fixture();
        var file = fixture.Write("models.js", """
            module.exports = pi => pi.registerCommand('run', { handler: async (_, ctx) => {
              const registry = ctx.modelRegistry;
              const next = registry.find('control-test', 'next');
              if (!registry.getAll().some(m => m.id === 'next') || !registry.getAvailable().some(m => m.id === 'next')) throw Error('catalog');
              if (!next.input.includes('text') || next.maxTokens !== 200) throw Error('model fields');
              if (await pi.setModel(registry.find('unconfigured-test', 'missing-auth'))) throw Error('missing auth accepted');
              if (ctx.model.id !== 'first' || !await pi.setModel(next) || ctx.model.id !== 'next') throw Error('switch');
              if (await registry.getApiKey(next) !== 'synthetic-key') throw Error('key');
              const copy = ctx.model; copy.id = 'changed';
              if (ctx.model.id !== 'next' || ctx.getSystemPrompt() !== 'control system') throw Error('snapshot');
              await ctx.waitForIdle();
              return ctx.model.id;
            }});
            """);
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("next", result.StatusMessage);
        Assert.Equal("next", fixture.Runner.Model.Id);
        Assert.Equal("next", fixture.Tree.LoadSnapshot().Model);
    }

    /// <summary>活动工具去重并忽略未知名称，读取返回副本，下一次真实请求使用修改后的集合。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Tools_ChangeActualRequestAndCanBeReenabled()
    {
        using var fixture = new Fixture();
        var file = fixture.Write("tools.js", """
            module.exports = pi => pi.registerCommand('run', { handler: () => {
              if (pi.getAllTools().length !== 2) throw Error('registry');
              pi.setActiveTools(['second', 'missing', 'second']);
              const names = pi.getActiveTools(); names.push('changed');
              return pi.getActiveTools().join(',');
            }});
            """);
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("second", result.StatusMessage);
        await DrainAsync(fixture.Runner.RunAsync("first request"));
        Assert.Equal(["second"], fixture.Provider.Calls.Single().Tools);
        fixture.Runner.SetActiveTools(["first", "second"]);
        await DrainAsync(fixture.Runner.RunAsync("second request"));
        Assert.Equal(["first", "second"], fixture.Provider.Calls.Last().Tools.Order(StringComparer.Ordinal));
        Assert.Equal(["first", "second"], fixture.Runner.GetActiveToolNames());
    }

    /// <summary>扩展在 agent_start 中改模型、工具和思考时，本轮第一条请求就使用新配置。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AgentStart_UpdatesInFlightRequestConfiguration()
    {
        using var fixture = new Fixture(withEvents: true);
        fixture.Write("events.js", """
            module.exports = pi => pi.on('agent_start', async (_, ctx) => {
              pi.setActiveTools(['second']);
              pi.setThinkingLevel('high');
              if (!await pi.setModel(ctx.modelRegistry.find('control-test', 'next'))) throw Error('switch');
            });
            """);
        await DrainAsync(fixture.Runner.RunAsync("run"));
        var call = Assert.Single(fixture.Provider.Calls);
        Assert.Equal("next", call.Model.Id);
        Assert.Equal(["second"], call.Tools);
        Assert.Equal(ThinkingLevel.High, call.Reasoning);
    }

    /// <summary>思考等级能即时读取并持久化，关闭不会回退到请求默认值。</summary>
    /// <param name="level">扩展请求的等级。</param>
    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Thinking_IsSavedAndReadable(string level)
    {
        using var fixture = new Fixture();
        var file = fixture.Write("thinking.js", "module.exports = pi => pi.registerCommand('run', { handler: level => { pi.setThinkingLevel(level); return pi.getThinkingLevel(); } });");
        var result = fixture.Runtime.Invoke(file, "run", level);
        Assert.True(result.Success, result.Error);
        Assert.Equal(level, result.StatusMessage);
        Assert.Equal(level, fixture.Tree.LoadSnapshot().ThinkingLevel);
    }

    /// <summary>非法控制参数返回错误，模型及工具状态不变。</summary>
    /// <param name="operation">非法 JavaScript 调用。</param>
    [Theory]
    [InlineData("pi.setThinkingLevel('invalid')")]
    [InlineData("pi.setActiveTools([42])")]
    [InlineData("await pi.setModel({})")]
    public void InvalidControl_DoesNotChangeState(string operation)
    {
        using var fixture = new Fixture();
        var file = fixture.Write("invalid.js", "module.exports = pi => pi.registerCommand('run', { handler: async () => { " + operation + "; } });");
        Assert.False(fixture.Runtime.Invoke(file, "run", "").Success);
        Assert.Equal("first", fixture.Runner.Model.Id);
        Assert.Equal(["first", "second"], fixture.Runner.GetActiveToolNames());
    }

    /// <summary>事件总线跨扩展分发，取消订阅生效且处理器异常不阻断其他订阅者。</summary>
    [Fact]
    public void EventBus_ConnectsExtensionsAndUnsubscribes()
    {
        using var fixture = new Fixture();
        var listener = fixture.Write("listener.js", """
            module.exports = pi => {
              let total = 0;
              const off = pi.events.on('shared', n => { total += n; });
              pi.events.on('shared', () => { throw Error('expected handler failure'); });
              pi.registerCommand('read', { handler: () => String(total) });
              pi.registerCommand('off', { handler: () => off() });
            };
            """);
        var sender = fixture.Write("sender.js", "module.exports = pi => pi.registerCommand('send', { handler: () => pi.events.emit('shared', 2) });");
        Assert.True(fixture.Runtime.Load(listener).Success);
        Assert.True(fixture.Runtime.Invoke(sender, "send", "").Success);
        Assert.Equal("2", fixture.Runtime.Invoke(listener, "read", "").StatusMessage);
        Assert.True(fixture.Runtime.Invoke(listener, "off", "").Success);
        Assert.True(fixture.Runtime.Invoke(sender, "send", "").Success);
        Assert.Equal("2", fixture.Runtime.Invoke(listener, "read", "").StatusMessage);
    }

    /// <summary>扩展命令执行保留参数边界、工作目录和 UTF-8 标准输出及错误。</summary>
    [Fact]
    public void Exec_PreservesArgumentsCwdAndExitCode()
    {
        using var fixture = new Fixture();
        var file = fixture.Write("exec.js", """
            module.exports = pi => pi.registerCommand('run', { handler: async () => JSON.stringify(await pi.exec(process.execPath,
              ['-e', "process.stdout.write(process.cwd() + '|' + process.argv[1]); process.stderr.write('中文错误'); process.exitCode = 7;", 'a & b']) ) });
            """);
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.True(result.Success, result.Error);
        using var value = JsonDocument.Parse(result.StatusMessage!);
        Assert.Equal(fixture.Root + "|a & b", value.RootElement.GetProperty("stdout").GetString());
        Assert.Equal("中文错误", value.RootElement.GetProperty("stderr").GetString());
        Assert.Equal(7, value.RootElement.GetProperty("code").GetInt32());
        Assert.False(value.RootElement.GetProperty("killed").GetBoolean());
    }

    /// <summary>扩展命令支持超时及预先取消，找不到程序返回失败码。</summary>
    /// <param name="mode">取消或启动失败模式。</param>
    [Theory]
    [InlineData("timeout")]
    [InlineData("abort")]
    [InlineData("missing")]
    public void Exec_HandlesCancellationAndMissingProgram(string mode)
    {
        using var fixture = new Fixture();
        var file = fixture.Write("cancel.js", """
            module.exports = pi => pi.registerCommand('run', { handler: async mode => {
              if (mode === 'missing') return JSON.stringify(await pi.exec('tau-nonexistent-test-program', []));
              const controller = new AbortController();
              if (mode === 'abort') controller.abort();
              return JSON.stringify(await pi.exec(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { signal: controller.signal, timeout: 80 }));
            }});
            """);
        var result = fixture.Runtime.Invoke(file, "run", mode);
        Assert.True(result.Success, result.Error);
        using var value = JsonDocument.Parse(result.StatusMessage!);
        Assert.Equal(mode != "missing", value.RootElement.GetProperty("killed").GetBoolean());
        if (mode == "missing") Assert.Equal(1, value.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>外部进程等待不消耗 JavaScript 计算时限，进程结束后正常恢复计时。</summary>
    [Fact]
    public void Exec_WaitDoesNotConsumeHandlerBudget()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(500));
        var file = fixture.Write("wait.js", """
            module.exports = pi => pi.registerCommand('run', { handler: async () => {
              const result = await pi.exec(process.execPath, ['-e', "setTimeout(() => process.stdout.write('done'), 1200)"], { timeout: 3000 });
              return result.stdout;
            }});
            """);
        Assert.True(fixture.Runtime.Load(file).Success);
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("done", result.StatusMessage);
    }

    /// <summary>完整消费运行器事件。</summary>
    /// <param name="events">事件流。</param>
    /// <returns>消费任务。</returns>
    private static async Task DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        await foreach (var _ in events) { }
    }

    /// <summary>【CodingAgent】【控制夹具】使用真实运行器、Node 和独立本地认证文件。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-extension-control-" + Guid.NewGuid().ToString("N"));
        public CodingAgentJavaScriptExtensionRuntime Runtime { get; }
        public RuntimeCodingAgentRunner Runner { get; }
        public CodingAgentTreeSessionController Tree { get; }
        public CaptureProvider Provider { get; } = new();

        /// <summary>创建隔离的控制测试会话。</summary>
        /// <param name="withEvents">是否加载 agent_start 扩展。</param>
        /// <param name="timeout">可选的 JavaScript 计算时限。</param>
        /// <param name="eventTypes">可选事件订阅集合。</param>
        public Fixture(bool withEvents = false, TimeSpan? timeout = null, IReadOnlyList<string>? eventTypes = null)
        {
            Directory.CreateDirectory(Root);
            var configStore = new ModelConfigurationStore([Path.Combine(Root, "models.json")]);
            var auth = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([Path.Combine(Root, "auth.json")]), configurationStore: configStore);
            var catalog = new ModelCatalog(auth, configStore);
            var first = new Model { Provider = "control-test", Id = "first", Name = "First", Api = Provider.Api, Reasoning = true, ContextWindow = 10000, MaxOutputTokens = 200 };
            catalog.RegisterModel(first);
            catalog.RegisterModel(first with { Id = "next" });
            catalog.RegisterModel(first with { Provider = "unconfigured-test", Id = "missing-auth" });
            var registry = new ProviderRegistry();
            registry.Register(Provider.Api, () => Provider);
            Runtime = new CodingAgentJavaScriptExtensionRuntime(Root, timeout: timeout);
            var sink = withEvents || eventTypes is not null ? new CodingAgentExtensionLifecycleEventSink(
                [new(Path.Combine(Root, "events.js"), "test", "javascript", eventTypes ?? ["agent_start"])], Runtime) : null;
            Runner = new RuntimeCodingAgentRunner(new AgentRuntime(), new AgentLoopConfig
            {
                Model = first, ProviderRegistry = registry, Tools = [new TestTool("first"), new TestTool("second")],
                SystemPrompt = "control system", StreamOptions = new() { ApiKey = "synthetic-key" }
            }, catalog, extensionLifecycleEventSink: sink, workingDirectory: Root);
            Tree = new(new CodingAgentTreeSessionStore(Path.Combine(Root, "session.jsonl"), Root));
            Runtime.BindSession(Runner, Tree);
        }

        /// <summary>写入合成扩展。</summary>
        /// <param name="name">文件名。</param>
        /// <param name="source">源码。</param>
        /// <returns>完整路径。</returns>
        public string Write(string name, string source)
        {
            var file = Path.Combine(Root, name);
            File.WriteAllText(file, source);
            return file;
        }

        /// <summary>释放 Node 并清理经过路径验证的夹具目录。</summary>
        public void Dispose()
        {
            Runtime.Dispose();
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("tau-extension-control-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => "Synthetic tool";
        public JsonElement ParameterSchema
        {
            get
            {
                using var document = JsonDocument.Parse("{\"type\":\"object\"}");
                return document.RootElement.Clone();
            }
        }
        /// <summary>返回合成工具结果。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">参数。</param>
        /// <param name="ct">取消令牌。</param>
        /// <param name="onUpdate">进度回调。</param>
        /// <returns>工具结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) =>
            Task.FromResult(new ToolResult([new TextContent("ok")]));
    }

    private sealed class CaptureProvider : IStreamProvider
    {
        public string Api => "extension-control";
        public List<(Model Model, string[] Tools, ThinkingLevel? Reasoning)> Calls { get; } = [];
        /// <summary>记录真实请求配置并返回合成结果。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">请求上下文。</param>
        /// <param name="options">请求选项。</param>
        /// <returns>已结束的流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Calls.Add((model, context.Tools?.Select(tool => tool.Name).ToArray() ?? [], (options as SimpleStreamOptions)?.Reasoning));
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("ok")])));
            return stream;
        }
        /// <summary>使用相同记录器处理简化选项。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>完成流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
