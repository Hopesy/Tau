// 作者：xxx
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【扩展生命周期】验证扩展闭包在命令和事件之间保持状态</summary>
public sealed class CodingAgentExtensionLifetimeTests
{
    /// <summary>【CodingAgent】【协作取消】命令、工具和事件收到 AbortSignal，清理完成后保留原有进程闭包。</summary>
    /// <param name="kind">调用类型。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("command")]
    [InlineData("tool")]
    [InlineData("event")]
    public async Task HostCancellation_ReachesAbortSignalAndPreservesProcess(string kind)
    {
        using var project = new ExtensionProject();
        var file = project.Write("cancel.js", """
            const fs = require('node:fs');
            module.exports = pi => {
              let cleaned = false;
              const wait = async signal => {
                fs.writeFileSync('started', 'ready');
                if (!(signal instanceof AbortSignal)) throw Error('missing signal');
                await new Promise(resolve => {
                  if (signal.aborted) resolve(); else signal.addEventListener('abort', resolve, {once:true});
                });
                cleaned = true;
              };
              pi.registerCommand('wait', {handler:(_,ctx)=>wait(ctx.signal)});
              pi.registerCommand('state', {handler:()=>process.pid + ':' + cleaned});
              pi.registerTool({name:'wait', parameters:{type:'object'}, execute:async (_,args,signal,update,ctx)=>{
                if (signal !== ctx.signal) throw Error('different signal');
                await wait(signal);
                return {content:[]};
              }});
              pi.on('context', async (e,ctx) => { await wait(ctx.signal); return {messages:e.messages}; });
            };
            """);
        using var runtime = project.CreateRuntime();
        var original = runtime.Invoke(file, "state", "").StatusMessage;
        using var cancellation = new CancellationTokenSource();
        using var args = JsonDocument.Parse("{}");
        using var evt = JsonDocument.Parse("""{"type":"context","messages":[]}""");
        var pending = Task.Run(() =>
        {
            if (kind == "tool") runtime.ExecuteTool(file, "wait", "call", args.RootElement, cancellation.Token);
            else if (kind == "event") runtime.EmitEvent(file, evt.RootElement, cancellation.Token);
            else runtime.Invoke(file, "wait", "", cancellation.Token);
        });
        await WaitForStartedAsync(project);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(original!.Replace(":false", ":true", StringComparison.Ordinal), runtime.Invoke(file, "state", "").StatusMessage);
    }

    /// <summary>【CodingAgent】【取消隔离】取消一个等待命令，不取消同一 Node 中并发的其他命令。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task HostCancellation_DoesNotAffectConcurrentCommand()
    {
        using var project = new ExtensionProject();
        var file = project.Write("parallel.js", """
            const fs = require('node:fs');
            module.exports = pi => {
              pi.registerCommand('wait', {handler:async (_,ctx)=>{
                fs.writeFileSync('started','ready');
                await new Promise(resolve=>ctx.signal.addEventListener('abort',resolve,{once:true}));
              }});
              pi.registerCommand('other', {handler:async ()=>{
                await new Promise(resolve=>setTimeout(resolve,250)); return 'completed';
              }});
            };
            """);
        using var runtime = project.CreateRuntime();
        Assert.True(runtime.Load(file).Success);
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(() => runtime.Invoke(file, "wait", "", cancellation.Token));
        await WaitForStartedAsync(project);
        var other = Task.Run(() => runtime.Invoke(file, "other", ""));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await other.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success, result.Error);
        Assert.Equal("completed", result.StatusMessage);
    }

    /// <summary>【CodingAgent】【取消兜底】忽略信号或阻塞事件循环的扩展被终止，下一请求可重建运行时。</summary>
    /// <param name="busyLoop">是否使用同步死循环。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCancellation_StopsUncooperativeHandler(bool busyLoop)
    {
        using var project = new ExtensionProject();
        var file = project.Write("uncooperative.js", "const fs=require('node:fs'); module.exports=pi=>{ pi.registerCommand('pid',{handler:()=>String(process.pid)});" +
            "pi.registerCommand('wait',{handler:async ()=>{ fs.writeFileSync('started','ready'); " +
            (busyLoop ? "while(true){}" : "await new Promise(()=>{});") + " }}); };");
        using var runtime = project.CreateRuntime(TimeSpan.FromSeconds(10));
        var original = runtime.Invoke(file, "pid", "").StatusMessage;
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(() => runtime.Invoke(file, "wait", "", cancellation.Token));
        await WaitForStartedAsync(project);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var next = runtime.Invoke(file, "pid", "");
        Assert.True(next.Success, next.Error);
        Assert.NotEqual(original, next.StatusMessage);
    }

    /// <summary>【CodingAgent】【交互取消】宿主取消解除没有自行传入 signal 的对话，并保留扩展闭包。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task HostCancellation_ClosesPendingDialog()
    {
        using var project = new ExtensionProject();
        var file = project.Write("dialog-cancel.js", """
            module.exports = pi => {
              let closed = false;
              pi.registerCommand('wait',{handler:async (_,ctx)=>{
                await ctx.ui.input('Waiting'); closed = true;
              }});
              pi.registerCommand('state',{handler:()=>String(closed)});
            };
            """);
        using var runtime = project.CreateRuntime();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Attach((_, _) => { requested.TrySetResult(); return Task.CompletedTask; });
        runtime.SetExtensionUiBridge(bridge, "rpc");
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(() => runtime.Invoke(file, "wait", "", cancellation.Token));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("true", runtime.Invoke(file, "state", "").StatusMessage);
    }

    /// <summary>等待扩展写出的开始标记，保证取消验证发生在处理器已运行后。</summary>
    /// <param name="project">隔离扩展目录。</param>
    /// <returns>标记出现时完成的任务。</returns>
    private static async Task WaitForStartedAsync(ExtensionProject project)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(Path.Combine(project.DirectoryPath, "started"))) await Task.Delay(20, deadline.Token);
    }

    /// <summary>不同扩展和不同会话使用各自的闭包</summary>
    [Fact]
    public void ModulesAndRuntimes_HaveIndependentState()
    {
        using var project = new ExtensionProject();
        const string source = "module.exports = pi => { let n = 0; pi.registerCommand('count', { handler: () => String(++n) }); };";
        var first = project.Write("first.cjs", source);
        var second = project.Write("second.cjs", source);
        using var left = project.CreateRuntime();
        using var right = project.CreateRuntime();
        Assert.Equal("1", left.Invoke(first, "count", "").StatusMessage);
        Assert.Equal("1", left.Invoke(second, "count", "").StatusMessage);
        Assert.Equal("1", right.Invoke(first, "count", "").StatusMessage);
        Assert.Equal("2", left.Invoke(first, "count", "").StatusMessage);
    }

    /// <summary>并发请求共享一次初始化，各自只收到自己的消息和返回值</summary>
    /// <returns>并发测试完成的任务</returns>
    [Fact]
    public async Task ConcurrentCommands_KeepResponsesAndActionsSeparate()
    {
        using var project = new ExtensionProject();
        var file = project.Write("concurrent.cjs", """
            module.exports = async pi => {
              globalThis.initializations = (globalThis.initializations || 0) + 1;
              await new Promise(resolve => setTimeout(resolve, 30));
              let n = 0;
              pi.registerCommand('run', { handler: async args => {
                const current = ++n;
                await new Promise(resolve => setTimeout(resolve, 100 - Number(args) * 10));
                pi.sendUserMessage('消息-' + args);
                return args + ':' + current + ':' + globalThis.initializations;
              }});
            };
            """);
        using var runtime = project.CreateRuntime();
        var tasks = Enumerable.Range(0, 4)
            .Select(i => Task.Run(() => runtime.Invoke(file, "run", i.ToString())))
            .ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].Success, results[i].Error);
            Assert.Equal(["消息-" + i], results[i].RunnerMessages);
            Assert.StartsWith(i + ":", results[i].StatusMessage);
            Assert.EndsWith(":1", results[i].StatusMessage);
        }
        Assert.Equal(4, results.Select(result => result.StatusMessage!.Split(':')[1]).Distinct().Count());
    }

    /// <summary>显式重载重建闭包，并重新读取 CommonJS 和 ESM 依赖</summary>
    /// <param name="esm">是否使用 ESM 模块</param>
    /// <returns>重载命令测试完成的任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadCommand_RefreshesImportedDependencies(bool esm)
    {
        using var project = new ExtensionProject();
        var helperName = esm ? "helper.mjs" : "helper.cjs";
        project.Write(helperName, esm ? "export default 'old';" : "module.exports = 'old';");
        var file = project.Write("extension.js", esm
            ? "import value from './helper.mjs'; export default pi => { let n = 0; pi.registerCommand('read', { handler: () => value + ':' + (++n) }); };"
            : "const value = require('./helper.cjs'); module.exports = pi => { let n = 0; pi.registerCommand('read', { handler: () => value + ':' + (++n) }); };");
        using var store = project.CreateStore(file);
        var command = Assert.Single(store.LoadStatus().Commands).InvocationName;
        Assert.True(store.TryInvoke('/' + command, out var first));
        Assert.Equal("old:1", first!.Message);
        project.Write(helperName, esm ? "export default 'new';" : "module.exports = 'new';");
        Assert.True(store.TryInvoke('/' + command, out var cached));
        Assert.Equal("old:2", cached!.Message);

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var router = new CodingAgentCommandRouter(runner, extensionCommandStore: store);
        var reload = await router.TryHandleAsync("/reload");
        Assert.False(reload.IsError, reload.Message);
        Assert.True(store.TryInvoke('/' + command, out var refreshed));
        Assert.Equal("new:1", refreshed!.Message);
    }

    /// <summary>更新标志参数不会重建扩展，移除覆盖值后恢复默认值</summary>
    [Fact]
    public void FlagUpdates_AreVisibleWithoutReinitialization()
    {
        using var project = new ExtensionProject();
        var file = project.Write("flags.cjs", """
            module.exports = pi => {
              let n = 0;
              pi.registerFlag('name', { type: 'string', default: 'default' });
              pi.registerCommand('flag', { handler: () => pi.getFlag('name') + ':' + (++n) });
            };
            """);
        using var runtime = project.CreateRuntime();
        Assert.Equal("default:1", runtime.Invoke(file, "flag", "").StatusMessage);
        runtime.SetFlagValues(new Dictionary<string, object> { ["name"] = "changed" });
        Assert.Equal("changed:2", runtime.Invoke(file, "flag", "").StatusMessage);
        runtime.SetFlagValues(new Dictionary<string, object>());
        Assert.Equal("default:3", runtime.Invoke(file, "flag", "").StatusMessage);
    }

    /// <summary>工具参数准备与执行共用同一份扩展状态</summary>
    [Fact]
    public void ToolPreparationAndExecution_ShareState()
    {
        using var project = new ExtensionProject();
        var file = project.Write("tool.cjs", """
            module.exports = pi => {
              let prepared = false;
              pi.registerTool({ name: 'state', parameters: { type: 'object' },
                prepareArguments: args => { prepared = true; return args; },
                execute: async () => ({ content: [{ type: 'text', text: String(prepared) }] })
              });
            };
            """);
        using var runtime = project.CreateRuntime();
        using var args = JsonDocument.Parse("{}");
        Assert.True(runtime.PrepareToolArguments(file, "state", args.RootElement).Success);
        var result = runtime.ExecuteTool(file, "state", "call-1", args.RootElement);
        Assert.True(result.Success, result.Error);
        Assert.Equal("true", Assert.IsType<Tau.Ai.TextContent>(Assert.Single(result.Content)).Text);
    }

    /// <summary>重载后会话开始事件可重新建立命令需要的状态</summary>
    /// <returns>会话重载验证完成的任务</returns>
    [Fact]
    public async Task ReloadCommand_PublishesSessionStartWithReloadReason()
    {
        using var project = new ExtensionProject();
        var file = project.Write("session.js", """
            module.exports = pi => {
              let reason = 'unset';
              pi.on('session_start', event => { reason = event.reason; });
              pi.registerCommand('reason', { handler: () => reason });
            };
            """);
        using var store = project.CreateStore(file);
        var command = Assert.Single(store.LoadStatus().Commands).InvocationName;
        Assert.Empty(await store.PublishSessionStartAsync("startup"));
        Assert.True(store.TryInvoke('/' + command, out var initial));
        Assert.Equal("startup", initial!.Message);
        var router = new CodingAgentCommandRouter(
            new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>()), extensionCommandStore: store);
        Assert.False((await router.TryHandleAsync("/reload")).IsError);
        Assert.True(store.TryInvoke('/' + command, out var reloaded));
        Assert.Equal("reload", reloaded!.Message);
    }

    /// <summary>SDK 会话释放时关闭已经加载的扩展进程</summary>
    /// <returns>SDK 资源释放验证完成的任务</returns>
    [Fact]
    public async Task SdkSession_DisposesItsExtensionProcess()
    {
        using var project = new ExtensionProject();
        Directory.CreateDirectory(Path.Combine(project.DirectoryPath, ".tau", "extensions"));
        project.Write(Path.Combine(".tau", "extensions", "pid.js"),
            "module.exports = pi => pi.registerCommand('pid', { handler: () => String(process.pid) });");
        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = project.DirectoryPath,
            AgentDirectory = Path.Combine(project.DirectoryPath, ".agent"),
            NoSession = true
        });
        var command = Assert.Single(session.ExtensionStatus.Commands).InvocationName;
        Assert.True(session.ExtensionCommandStore.TryInvoke('/' + command, out var invocation));
        using var process = Process.GetProcessById(int.Parse(invocation!.Message));
        session.Dispose();
        Assert.True(process.HasExited);
    }

    /// <summary>执行超时终止原进程，后续调用可启动新的干净运行时</summary>
    [Fact]
    public void Timeout_TerminatesProcessAndAllowsRecovery()
    {
        using var project = new ExtensionProject();
        var file = project.Write("timeout.cjs", """
            module.exports = pi => {
              pi.registerCommand('pid', { handler: () => String(process.pid) });
              pi.registerCommand('hang', { handler: () => { while (true) {} } });
            };
            """);
        using var runtime = project.CreateRuntime(TimeSpan.FromSeconds(1));
        var pid = int.Parse(runtime.Invoke(file, "pid", "").StatusMessage!);
        using var process = Process.GetProcessById(pid);
        var failure = runtime.Invoke(file, "hang", "");
        Assert.False(failure.Success);
        Assert.Contains("timed out", failure.Error);
        Assert.True(process.HasExited);
        var recovered = runtime.Invoke(file, "pid", "");
        Assert.True(recovered.Success, recovered.Error);
        Assert.NotEqual(pid.ToString(), recovered.StatusMessage);
    }

    /// <summary>释放运行时会取消等待中的交互和命令，且不会重新启动进程</summary>
    /// <returns>取消测试完成的任务</returns>
    [Fact]
    public async Task Dispose_UnblocksPendingInteractionAndStopsProcess()
    {
        using var project = new ExtensionProject();
        var file = project.Write("dispose.cjs", """
            module.exports = pi => {
              pi.registerCommand('pid', { handler: () => String(process.pid) });
              pi.registerCommand('edit', { handler: async (_, ctx) => await ctx.ui.editor('Title', '') });
            };
            """);
        using var runtime = project.CreateRuntime();
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((_, _) => { request.TrySetResult(); return Task.CompletedTask; });
        runtime.SetExtensionUiBridge(bridge);
        var pid = int.Parse(runtime.Invoke(file, "pid", "").StatusMessage!);
        using var process = Process.GetProcessById(pid);
        var pending = Task.Run(() => runtime.Invoke(file, "edit", ""));
        await request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True(process.HasExited);
        Assert.False(runtime.Load(file).Success);
    }

    /// <summary>编辑器等待超过执行时限仍可返回，前后生命周期事件修改同一扩展状态</summary>
    /// <returns>交互和重入验证完成的任务</returns>
    [Fact]
    public async Task EditorWait_PausesTimeoutAndSupportsReentrantLifecycleEvents()
    {
        using var project = new ExtensionProject();
        var file = project.Write("editor.js", """
            module.exports = pi => {
              let events = [];
              pi.on('ui_prompt_start', () => { events.push('start'); });
              pi.on('ui_prompt_end', () => { events.push('end'); });
              pi.registerCommand('edit', { handler: async (_, ctx) => {
                const value = await ctx.ui.editor('Title', '草稿');
                return events.join(',') + ':' + value;
              }});
            };
            """);
        using var store = project.CreateStore(file, TimeSpan.FromSeconds(1));
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((value, _) =>
        {
            var fields = Assert.IsType<Dictionary<string, object?>>(value);
            request.TrySetResult(Assert.IsType<string>(fields["id"]));
            return Task.CompletedTask;
        });
        store.SetExtensionUiBridge(bridge, "rpc");
        var command = Assert.Single(store.LoadStatus().Commands).InvocationName;
        var pending = Task.Run(() =>
        {
            Assert.True(store.TryInvoke('/' + command, out var invocation));
            return invocation!;
        });
        var editor = await request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(1300));
        Assert.False(pending.IsCompleted);
        using var response = JsonDocument.Parse($$"""{"id":"{{editor}}","value":"已编辑"}""");
        Assert.True(bridge.TryHandleResponse(response.RootElement));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsError, result.Message);
        Assert.Equal("start,end:已编辑", result.Message);
    }

    /// <summary>RPC 输入关闭或宿主取消时，中断正在等待编辑器的扩展并结束提示任务</summary>
    /// <param name="cancel">是否使用取消信号；否则关闭输入流</param>
    /// <returns>宿主退出验证完成的任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcExit_UnblocksEditorInActivePrompt(bool cancel)
    {
        using var project = new ExtensionProject();
        var file = project.Write("rpc.js", """
            module.exports = pi => {
              pi.registerCommand('edit', { handler: async (_, ctx) => await ctx.ui.editor('Title', '') });
            };
            """);
        using var store = project.CreateStore(file);
        var command = Assert.Single(store.LoadStatus().Commands).InvocationName;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, _) => InvokeExtensionAsync(store, command, completed));
        using var input = new TestInputReader();
        using var output = new StringWriter();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: store, extensionUi: bridge);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Attach((_, _) => { requested.TrySetResult(); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var running = host.RunAsync(cancellation.Token);
        input.Send("""{"id":"prompt","type":"prompt","message":"edit"}""");
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            input.Complete();
            Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>在模拟提示执行期间调用扩展，供宿主退出测试观察完成状态</summary>
    /// <param name="store">扩展存储</param>
    /// <param name="command">扩展命令名称</param>
    /// <param name="completed">请求结束通知</param>
    /// <returns>空的代理事件流</returns>
    private static async IAsyncEnumerable<AgentEvent> InvokeExtensionAsync(
        CodingAgentExtensionCommandStore store, string command, TaskCompletionSource completed)
    {
        try
        {
            await Task.Run(() => store.TryInvoke('/' + command, out _));
        }
        finally
        {
            completed.TrySetResult();
        }
        yield break;
    }

    /// <summary>选择、确认和输入对话通过真实 Node 桥接，正常结果与取消结果符合约定。</summary>
    /// <param name="method">交互方法。</param>
    /// <param name="cancelled">是否取消。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("select", false)]
    [InlineData("select", true)]
    [InlineData("confirm", false)]
    [InlineData("confirm", true)]
    [InlineData("input", false)]
    [InlineData("input", true)]
    public async Task Dialogs_ReturnRealResults(string method, bool cancelled)
    {
        using var project = new ExtensionProject();
        var file = project.Write("dialogs.js", """
            module.exports = pi => pi.registerCommand('run', { handler: async (method, ctx) => {
              const value = method === 'select' ? await ctx.ui.select('Title', ['one', 'two'], { timeout: 3000 })
                : method === 'confirm' ? await ctx.ui.confirm('Title', 'Question', { timeout: 3000 })
                : await ctx.ui.input('Title', 'Placeholder', { timeout: 3000 });
              return value === undefined ? 'cancelled' : String(value);
            }});
            """);
        using var runtime = project.CreateRuntime();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((value, _) =>
        {
            var fields = Assert.IsType<Dictionary<string, object?>>(value);
            Assert.Equal(method, fields["method"]);
            Assert.Equal(3000, fields["timeout"]);
            if (method == "select") Assert.Equal(["one", "two"], Assert.IsType<string[]>(fields["options"]));
            using var response = JsonDocument.Parse($$"""{"id":"{{fields["id"]}}","value":"two","confirmed":true,"cancelled":{{(cancelled ? "true" : "false")}}} """);
            Assert.True(bridge.TryHandleResponse(response.RootElement));
            return Task.CompletedTask;
        });
        runtime.SetExtensionUiBridge(bridge, "rpc");
        var result = await Task.Run(() => runtime.Invoke(file, "run", method)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal(method == "confirm" ? cancelled ? "false" : "true" : cancelled ? "cancelled" : "two", result.StatusMessage);
    }

    /// <summary>对话支持预先取消、等待期间取消及超时，取消不终止 Node 进程。</summary>
    /// <param name="method">交互方法。</param>
    /// <param name="mode">取消方式。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("select", "before")]
    [InlineData("select", "during")]
    [InlineData("select", "timeout")]
    [InlineData("confirm", "before")]
    [InlineData("confirm", "during")]
    [InlineData("confirm", "timeout")]
    [InlineData("input", "before")]
    [InlineData("input", "during")]
    [InlineData("input", "timeout")]
    public async Task Dialogs_CancelWithoutTerminatingRuntime(string method, string mode)
    {
        using var project = new ExtensionProject();
        var file = project.Write("cancel-dialog.js", """
            module.exports = pi => {
              let count = 0;
              let controller;
              pi.registerCommand('cancel', { handler: () => controller.abort() });
              pi.registerCommand('run', { handler: async (args, ctx) => {
                const [method, mode] = args.split(':');
                controller = new AbortController();
                if (mode === 'before') controller.abort();
                const options = { signal: controller.signal, timeout: mode === 'timeout' ? 80 : 3000 };
                const result = method === 'select' ? await ctx.ui.select('Title', ['one'], options)
                  : method === 'confirm' ? await ctx.ui.confirm('Title', 'Question', options)
                  : await ctx.ui.input('Title', '', options);
                if (result !== undefined && result !== false) throw Error('not cancelled');
                return String(++count);
              }});
            };
            """);
        using var runtime = project.CreateRuntime();
        var requests = 0;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((_, _) => { Interlocked.Increment(ref requests); opened.TrySetResult(); return Task.CompletedTask; });
        runtime.SetExtensionUiBridge(bridge, "rpc");
        for (var i = 1; i <= 2; i++)
        {
            opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Task.Run(() => runtime.Invoke(file, "run", method + ":" + mode));
            // 1. 【CodingAgent】【交互取消】明确等到对话打开再取消，避免机器负载把 during 情况变成预先取消
            if (mode == "during")
            {
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(runtime.Invoke(file, "cancel", "").Success);
            }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Success, result.Error);
            Assert.Equal(i.ToString(), result.StatusMessage);
        }
        Assert.Equal(mode == "before" ? 0 : 2, requests);
    }

    /// <summary>SDK 创建自动发布一次启动，释放自动发布一次关闭，关闭处理器仍可保存状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sdk_PublishesStartupAndShutdownExactlyOnce()
    {
        using var project = new ExtensionProject();
        Directory.CreateDirectory(Path.Combine(project.DirectoryPath, "extensions"));
        project.Write("extensions/lifecycle.js", """
            module.exports = pi => {
              let starts = 0;
              pi.on('session_start', (_, ctx) => { pi.appendEntry('start', { count: ++starts, mode: ctx.mode }); });
              pi.on('session_shutdown', () => pi.appendEntry('stop', { count: starts }));
            };
            """);
        var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = project.DirectoryPath, AgentDirectory = project.DirectoryPath,
            NoTools = CodingAgentSdkNoToolsMode.All, IncludeContextFiles = false,
            IncludeSkills = false, IncludePromptTemplates = false
        });
        var path = session.TreeSessionController!.Path;
        await session.ExtensionCommandStore.EnsureSessionStartedAsync();
        session.Dispose();
        session.Dispose();
        var entries = File.ReadLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(entry => entry.GetProperty("type").GetString() == "custom").ToArray();
        Assert.Equal(["start", "stop"], entries.Select(entry => entry.GetProperty("customType").GetString()));
        Assert.All(entries, entry => Assert.Equal(1, entry.GetProperty("data").GetProperty("count").GetInt32()));
        Assert.Equal("print", entries[0].GetProperty("data").GetProperty("mode").GetString());
    }

    /// <summary>RPC 启动交互期间继续接收 UI 响应，提示执行必须等待启动完成，EOF 仍发布关闭。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RpcStartupDialog_DoesNotBlockResponseReader()
    {
        using var project = new ExtensionProject();
        var file = project.Write("startup.js", """
            module.exports = pi => {
              pi.on('session_start', async (_, ctx) => {
                if (ctx.mode !== 'rpc' || !ctx.hasUI) throw Error('mode');
                pi.setSessionName(await ctx.ui.select('Choose', ['ready']));
              });
              pi.on('session_shutdown', () => pi.setSessionName('closed'));
            };
            """);
        using var store = project.CreateStore(file);
        using var input = new TestInputReader();
        using var output = new StringWriter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, _) => ObserveStartupAsync(entered));
        var bridge = new CodingAgentRpcExtensionUiBridge();
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: store, extensionUi: bridge);
        bridge.Attach((value, _) =>
        {
            var fields = Assert.IsType<Dictionary<string, object?>>(value);
            input.Send("""{"type":"prompt","message":"after startup"}""");
            input.Send($$"""{"type":"extension_ui_response","id":"{{fields["id"]}}","value":"ready"}""");
            return Task.CompletedTask;
        });
        var running = host.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ready", runner.SessionName);
        input.Complete();
        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("closed", runner.SessionName);
    }

    /// <summary>通知测试提示已开始执行。</summary>
    /// <param name="entered">开始通知。</param>
    /// <returns>空事件流。</returns>
    private static async IAsyncEnumerable<AgentEvent> ObserveStartupAsync(TaskCompletionSource entered)
    {
        entered.TrySetResult();
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>允许测试分别模拟新输入、断开和取消的 RPC 输入流</summary>
    private sealed class TestInputReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        /// <summary>发送一行 RPC 输入</summary>
        /// <param name="line">完整 JSON 命令</param>
        public void Send(string line) => _lines.Writer.TryWrite(line);

        /// <summary>关闭输入并让宿主观察到文件结束</summary>
        public void Complete() => _lines.Writer.TryComplete();

        /// <summary>等待下一行输入，支持宿主取消</summary>
        /// <param name="cancellationToken">取消信号</param>
        /// <returns>输入行，或关闭后的空值</returns>
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.WaitToReadAsync(cancellationToken) ? await _lines.Reader.ReadAsync(cancellationToken) : null;
    }

    /// <summary>测试扩展目录，确保测试结束后资源先释放再删除目录</summary>
    private sealed class ExtensionProject : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "tau-extension-life-" + Guid.NewGuid().ToString("N"));

        /// <summary>创建独立临时项目目录</summary>
        public ExtensionProject() => Directory.CreateDirectory(DirectoryPath);

        /// <summary>写入扩展或依赖源文件</summary>
        /// <param name="name">项目内文件名</param>
        /// <param name="source">源代码文本</param>
        /// <returns>文件完整路径</returns>
        public string Write(string name, string source)
        {
            var file = Path.Combine(DirectoryPath, name);
            File.WriteAllText(file, source);
            return file;
        }

        /// <summary>创建由调用方释放的扩展运行时</summary>
        /// <param name="timeout">可选执行时限</param>
        /// <returns>尚未启动进程的运行时</returns>
        public CodingAgentJavaScriptExtensionRuntime CreateRuntime(TimeSpan? timeout = null) => new(DirectoryPath, timeout: timeout);

        /// <summary>创建仅加载指定扩展、由调用方释放的存储</summary>
        /// <param name="file">扩展文件路径</param>
        /// <param name="timeout">可选执行时限</param>
        /// <returns>拥有运行时的扩展存储</returns>
        public CodingAgentExtensionCommandStore CreateStore(string file, TimeSpan? timeout = null) => new(
            cwd: DirectoryPath, explicitPaths: [file], includeDefaults: false, javaScriptRuntime: CreateRuntime(timeout));

        /// <summary>删除本测试创建的临时目录</summary>
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    /// <summary>重复查询注册信息不会重建扩展，连续命令共用闭包</summary>
    [Fact]
    public void Commands_KeepStateAcrossRegistrationQueries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extension-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "counter.cjs");
            File.WriteAllText(file, """
                module.exports = pi => {
                  let count = 0;
                  pi.registerCommand("count", { handler: () => String(++count) });
                };
                """);
            using var runtime = new CodingAgentJavaScriptExtensionRuntime(directory);
            Assert.True(runtime.Load(file).Success);
            Assert.Equal("1", runtime.Invoke(file, "count", "").StatusMessage);
            Assert.True(runtime.Load(file).Success);
            Assert.Equal("2", runtime.Invoke(file, "count", "").StatusMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>会话开始事件建立的状态在后续命令中可见</summary>
    [Fact]
    public void SessionStart_StateIsVisibleToCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extension-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "session.cjs");
            File.WriteAllText(file, """
                module.exports = pi => {
                  let ready = false;
                  pi.on("session_start", () => { ready = true; });
                  pi.registerCommand("ready", { handler: () => String(ready) });
                };
                """);
            using var runtime = new CodingAgentJavaScriptExtensionRuntime(directory);
            var loaded = runtime.Load(file);
            Assert.True(loaded.Success, loaded.Error);
            using var evt = JsonDocument.Parse("""{"type":"session_start","reason":"startup"}""");
            var emitted = runtime.EmitEvent(file, evt.RootElement);
            Assert.True(emitted.Success, emitted.Error);
            var invoked = runtime.Invoke(file, "ready", "");
            Assert.True(invoked.Success, invoked.Error);
            Assert.Equal("true", invoked.StatusMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
