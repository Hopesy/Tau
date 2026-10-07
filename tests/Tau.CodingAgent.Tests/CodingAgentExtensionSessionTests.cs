// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【扩展会话回归】通过真实 Node 进程验证会话读写、持久化及错误边界。</summary>
public sealed class CodingAgentExtensionSessionTests
{
    /// <summary>元数据写入后同一处理器可读取，重载后可恢复，返回对象不能修改真实状态。</summary>
    /// <param name="persistent">是否使用 JSONL 会话。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionApi_ReadsWritesAndSurvivesRuntimeReload(bool persistent)
    {
        using var fixture = new Fixture(persistent);
        var file = fixture.Write("state.cjs", """
            module.exports = pi => {
              pi.registerCommand('write', { handler: (_, ctx) => {
                const sm = ctx.sessionManager;
                pi.setSessionName('  extension session  ');
                pi.appendEntry('counter', { count: 1, nested: [true, null, 'value'] });
                const entry = sm.getLeafEntry();
                pi.setLabel(entry.id, 'checkpoint');
                if (pi.getSessionName() !== 'extension session' || sm.getSessionName() !== pi.getSessionName()) throw Error('name mismatch');
                if (sm.getLabel(entry.id) !== 'checkpoint' || sm.getEntry(entry.id).data.count !== 1) throw Error('read after write mismatch');
                const copy = sm.getEntries();
                copy.find(e => e.id === entry.id).data.count = 100;
                const header = sm.getHeader(); header.id = 'changed';
                if (sm.getEntry(entry.id).data.count !== 1 || sm.getSessionId() === 'changed') throw Error('mutable snapshot');
                if (sm.getBranch().at(-1).id !== sm.getLeafId() || !sm.getTree().length || !sm.getCwd()) throw Error('invalid tree');
                pi.setLabel(entry.id, undefined);
                if (sm.getLabel(entry.id) !== undefined) throw Error('label not cleared');
                return JSON.stringify({ id: sm.getSessionId(), file: sm.getSessionFile(), dir: sm.getSessionDir() });
              }});
              pi.registerCommand('read', { handler: (_, ctx) => pi.getSessionName() + ':' + ctx.sessionManager.getEntries().find(e => e.type === 'custom').data.count });
            };
            """);
        var result = fixture.Runtime.Invoke(file, "write", "");
        Assert.True(result.Success, result.Error);
        using var info = JsonDocument.Parse(result.StatusMessage!);
        Assert.Equal(persistent, info.RootElement.TryGetProperty("file", out _));
        if (!persistent) Assert.Equal("", info.RootElement.GetProperty("dir").GetString());
        Assert.Equal("extension session", fixture.Runner.SessionName);
        Assert.Single(fixture.Runner.Messages);
        fixture.Runtime.Reset();
        Assert.Equal("extension session:1", fixture.Runtime.Invoke(file, "read", "").StatusMessage);
        if (persistent)
        {
            Assert.Equal("extension session", fixture.Tree!.LoadSnapshot().Name);
            Assert.Single(fixture.Tree.LoadSnapshot().Messages);
            using var freshRuntime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
            var reopened = CodingAgentTreeSessionController.OpenOrCreate(fixture.Tree.Path);
            freshRuntime.BindSession(fixture.Runner, reopened);
            Assert.Equal("extension session:1", freshRuntime.Invoke(file, "read", "").StatusMessage);
        }
        else Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".tau")));
    }

    /// <summary>命令、快捷键、工具和事件使用同一条元数据持久化链路。</summary>
    /// <param name="entryPoint">扩展调用入口。</param>
    [Theory]
    [InlineData("command")]
    [InlineData("shortcut")]
    [InlineData("tool")]
    [InlineData("event")]
    public void AllEntryPoints_CommitSessionOperations(string entryPoint)
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("entries.cjs", """
            module.exports = pi => {
              const save = ctx => { pi.appendEntry('state', { source: 'extension' }); pi.setSessionName(ctx.sessionManager.getLeafEntry().customType); };
              pi.registerCommand('save', { handler: (_, ctx) => save(ctx) });
              pi.registerShortcut('ctrl+k', { handler: ctx => save(ctx) });
              pi.registerTool({ name: 'save', parameters: { type: 'object' }, execute: async (_, args, signal, update, ctx) => {
                save(ctx); return { content: [{ type: 'text', text: 'done' }] };
              }});
              pi.on('agent_start', (_, ctx) => save(ctx));
            };
            """);
        using var json = JsonDocument.Parse("{\"type\":\"agent_start\"}");
        var success = entryPoint switch
        {
            "command" => fixture.Runtime.Invoke(file, "save", "").Success,
            "shortcut" => fixture.Runtime.InvokeShortcut(file, "ctrl+k").Success,
            "tool" => fixture.Runtime.ExecuteTool(file, "save", "call", json.RootElement).Success,
            _ => fixture.Runtime.EmitEvent(file, json.RootElement).Success
        };
        Assert.True(success);
        Assert.Equal("state", fixture.Runner.SessionName);
        Assert.Single(fixture.Entries("custom"));
        Assert.Single(fixture.Tree!.LoadSnapshot().Messages);
    }

    /// <summary>切换分支只读取目标父链的状态，导出及再次打开也保留自定义数据。</summary>
    [Fact]
    public void BranchAndExport_PreserveCustomEntriesWithoutLeakingOtherBranch()
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("branches.cjs", """
            module.exports = pi => {
              pi.registerCommand('write', { handler: value => pi.appendEntry('state', { value }) });
              pi.registerCommand('read', { handler: (_, ctx) => JSON.stringify(ctx.sessionManager.getBranch().filter(e => e.type === 'custom').map(e => e.data.value)) });
            };
            """);
        Assert.True(fixture.Runtime.Invoke(file, "write", "first").Success);
        var branchPoint = fixture.Tree!.LoadSnapshot().LeafId;
        Assert.True(fixture.Runtime.Invoke(file, "write", "other").Success);
        fixture.Runner.RestoreSession(fixture.Tree.BranchTo(branchPoint).ToFlatSnapshot());
        Assert.Equal("[\"first\"]", fixture.Runtime.Invoke(file, "read", "").StatusMessage);
        var export = Path.Combine(fixture.Root, "export.jsonl");
        fixture.Tree.ExportCurrentBranch(export);
        using var runtime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
        runtime.BindSession(fixture.Runner, CodingAgentTreeSessionController.OpenOrCreate(export));
        Assert.Equal("[\"first\"]", runtime.Invoke(file, "read", "").StatusMessage);
        Assert.Single(fixture.Runner.Messages);
    }

    /// <summary>会话名称去除换行并跨分支共享，空白名称清除后也不能从旧分支恢复。</summary>
    [Fact]
    public void SessionName_IsSanitizedAndSharedAcrossBranches()
    {
        using var fixture = new Fixture(true);
        fixture.Tree!.SyncFromRunner(fixture.Runner);
        var branchPoint = fixture.Tree.LoadSnapshot().LeafId;
        var file = fixture.Write("names.cjs", """
            module.exports = pi => {
              pi.registerCommand('rename', { handler: name => pi.setSessionName(name) });
              pi.registerCommand('read', { handler: () => pi.getSessionName() ?? 'cleared' });
            };
            """);
        Assert.True(fixture.Runtime.Invoke(file, "rename", "  global\r\nname  ").Success);
        fixture.Runner.RestoreSession(fixture.Tree.BranchTo(branchPoint).ToFlatSnapshot());
        Assert.Equal("global name", fixture.Runner.SessionName);
        Assert.Equal("global name", fixture.Runtime.Invoke(file, "read", "").StatusMessage);
        Assert.True(fixture.Runtime.Invoke(file, "rename", " \r\n ").Success);
        fixture.Runner.RestoreSession(fixture.Tree.BranchTo(branchPoint).ToFlatSnapshot());
        Assert.Null(fixture.Runner.SessionName);
        Assert.Equal("cleared", fixture.Runtime.Invoke(file, "read", "").StatusMessage);
    }

    /// <summary>处理器随后抛错时，已执行元数据操作仍然保留，与上游同步副作用一致。</summary>
    [Fact]
    public void HandlerFailure_DoesNotDiscardEarlierSessionWrite()
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("throw.cjs", "module.exports = pi => pi.registerCommand('run', { handler: () => { pi.appendEntry('saved', { ok: true }); throw Error('after save'); } });");
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.False(result.Success);
        Assert.Contains("after save", result.Error);
        Assert.Single(fixture.Entries("custom"));
    }

    /// <summary>未绑定会话或初始化阶段调用运行时 API 应明确报错。</summary>
    /// <param name="duringLoad">是否在扩展工厂中调用。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninitializedSession_ReportsError(bool duringLoad)
    {
        using var fixture = new Fixture(false);
        var file = fixture.Write("unbound.cjs", duringLoad
            ? "module.exports = pi => pi.setSessionName('bad');"
            : "module.exports = pi => pi.registerCommand('read', { handler: () => pi.getSessionName() });");
        using var runtime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
        if (duringLoad)
        {
            runtime.BindSession(fixture.Runner);
            var result = runtime.Load(file);
            Assert.False(result.Success);
            Assert.Contains("not initialized", result.Error);
        }
        else
        {
            var result = runtime.Invoke(file, "read", "");
            Assert.False(result.Success);
            Assert.Contains("not initialized", result.Error);
        }
    }

    /// <summary>无效标签、自定义类型和不可序列化数据不会产生幽灵条目。</summary>
    /// <param name="operation">无效 JavaScript 操作。</param>
    [Theory]
    [InlineData("pi.setLabel('missing', 'bad')")]
    [InlineData("pi.appendEntry('', {})")]
    [InlineData("pi.appendEntry('state', 1n)")]
    public void InvalidOperation_DoesNotWriteEntry(string operation)
    {
        using var fixture = new Fixture(true);
        fixture.Tree!.SyncFromRunner(fixture.Runner);
        var before = File.ReadAllText(fixture.Tree.Path);
        var file = fixture.Write("invalid.cjs", "module.exports = pi => pi.registerCommand('run', { handler: () => { " + operation + "; } });");
        Assert.False(fixture.Runtime.Invoke(file, "run", "").Success);
        Assert.Equal(before, File.ReadAllText(fixture.Tree.Path));
    }

    /// <summary>真实存储写入失败必须覆盖扩展的成功返回，不能假报已经改名。</summary>
    [Fact]
    public void StorageFailure_IsReportedAndDoesNotChangeRunnerName()
    {
        using var fixture = new Fixture(true);
        fixture.Tree!.SyncFromRunner(fixture.Runner);
        var file = fixture.Write("rename.cjs", "module.exports = pi => pi.registerCommand('run', { handler: () => { pi.setSessionName('not saved'); return 'success'; } });");
        using var locked = new FileStream(fixture.Tree.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = fixture.Runtime.Invoke(file, "run", "");
        Assert.False(result.Success);
        Assert.Contains("session operation failed", result.Error);
        Assert.Null(fixture.Runner.SessionName);
    }

    /// <summary>并发扩展调用的写入串行落盘，父链完整且各项数据不丢失。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConcurrentOperations_AreAllCommittedInOneBranch()
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("parallel.cjs", """
            module.exports = pi => pi.registerCommand('save', { handler: async value => {
              await new Promise(resolve => setTimeout(resolve, Number(value) % 3 * 10));
              pi.appendEntry('concurrent', { value }); return value;
            }});
            """);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => fixture.Runtime.Invoke(file, "save", i.ToString()))));
        Assert.All(results, result => Assert.True(result.Success, result.Error));
        var entries = fixture.Entries("custom");
        Assert.Equal(10, entries.Count);
        Assert.Equal(10, entries.Select(entry => entry.GetProperty("data").GetProperty("value").GetString()).Distinct().Count());
        var snapshot = fixture.Tree!.Store.ReadExtensionSnapshot();
        var byId = snapshot.Entries.ToDictionary(entry => entry.Id);
        var leaf = snapshot.LeafId;
        var traversed = new HashSet<string>();
        while (leaf is not null) { Assert.True(traversed.Add(leaf)); leaf = byId[leaf].ParentId; }
        Assert.Equal(snapshot.Entries.Count, traversed.Count);
    }

    /// <summary>SDK 创建会话时自动接线，无需调用方再手动绑定扩展。</summary>
    /// <param name="noSession">是否禁止持久化。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sdk_BindsSessionAutomatically(bool noSession)
    {
        using var fixture = new Fixture(false);
        var extensions = Path.Combine(fixture.Root, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "session.js"), "module.exports = pi => pi.registerCommand('state', { handler: (_, ctx) => { pi.setSessionName('sdk'); pi.appendEntry('sdk', { count: 1 }); return ctx.sessionManager.getSessionId(); } });");
        using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = Path.Combine(fixture.Root, "agent"), NoSession = noSession,
            IncludeSkills = false, IncludeContextFiles = false, IncludePromptTemplates = false
        });
        var command = Assert.Single(session.ExtensionStatus.Commands).InvocationName;
        // 1. 【CodingAgent】【扩展绑定】命令路由器的默认参数不能把 SDK 已有持久化绑定降级成内存会话
        _ = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore);
        Assert.True(session.ExtensionCommandStore.TryInvoke('/' + command, out var invocation));
        Assert.False(invocation!.IsError, invocation.Message);
        Assert.Equal("sdk", session.Runner.SessionName);
        if (!noSession) Assert.Equal("sdk", session.TreeSessionController!.LoadSnapshot().Name);
        else Assert.Null(session.TreeSessionController);
    }

    /// <summary>进入编辑器前的会话变更必须先提交，重入扩展回调能读到已提交名称。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task UiReentrancy_ObservesEarlierSessionMutation()
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("editor.cjs", """
            module.exports = pi => {
              pi.registerCommand('edit', { handler: async (_, ctx) => {
                pi.setSessionName('before editor');
                return await ctx.ui.editor('Title', '');
              }});
              pi.registerCommand('read', { handler: () => pi.getSessionName() });
            };
            """);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((value, _) =>
        {
            var fields = Assert.IsType<Dictionary<string, object?>>(value);
            Assert.Equal("before editor", fixture.Tree!.LoadSnapshot().Name);
            var read = fixture.Runtime.Invoke(file, "read", "");
            Assert.True(read.Success, read.Error);
            using var response = JsonDocument.Parse($$"""{"id":"{{fields["id"]}}","value":"{{read.StatusMessage}}"}""");
            Assert.True(bridge.TryHandleResponse(response.RootElement));
            return Task.CompletedTask;
        });
        fixture.Runtime.SetExtensionUiBridge(bridge, "rpc");
        var result = await Task.Run(() => fixture.Runtime.Invoke(file, "edit", "")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal("before editor", result.StatusMessage);
    }

    /// <summary>切换会话后，旧处理器的延迟写入不能污染新会话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Rebinding_RejectsDelayedWritesFromPreviousSession()
    {
        using var fixture = new Fixture(true);
        var file = fixture.Write("delayed.cjs", """
            module.exports = pi => pi.registerCommand('wait', { handler: async (_, ctx) => {
              pi.setSessionName('old session'); await ctx.ui.editor('pause', ''); pi.setSessionName('late write');
            }});
            """);
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach((value, _) =>
        {
            waiting.TrySetResult((string)Assert.IsType<Dictionary<string, object?>>(value)["id"]!);
            return Task.CompletedTask;
        });
        fixture.Runtime.SetExtensionUiBridge(bridge);
        var pending = Task.Run(() => fixture.Runtime.Invoke(file, "wait", ""));
        var requestId = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        fixture.Runtime.BindSession(next);
        using var response = JsonDocument.Parse($$"""{"id":"{{requestId}}","value":"continue"}""");
        Assert.True(bridge.TryHandleResponse(response.RootElement));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Contains("session changed", result.Error);
        Assert.Null(next.SessionName);
        Assert.Equal("old session", fixture.Tree!.LoadSnapshot().Name);
    }

    /// <summary>旧式平面格式支持名称保存，自定义状态需要 JSONL，不能静默丢弃。</summary>
    [Fact]
    public void FlatSession_PersistsNameAndRejectsUnsupportedCustomStorage()
    {
        using var fixture = new Fixture(false);
        var flat = new CodingAgentSessionStore(Path.Combine(fixture.Root, "legacy.json"));
        using var runtime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
        runtime.BindSession(fixture.Runner, flat: flat);
        var file = fixture.Write("flat.cjs", """
            module.exports = pi => {
              pi.registerCommand('name', { handler: () => pi.setSessionName('legacy name') });
              pi.registerCommand('state', { handler: () => pi.appendEntry('state', { value: 1 }) });
            };
            """);
        Assert.True(runtime.Invoke(file, "name", "").Success);
        Assert.Equal("legacy name", flat.Load().Name);
        var result = runtime.Invoke(file, "state", "");
        Assert.False(result.Success);
        Assert.Contains("require a JSONL session", result.Error);
    }

    /// <summary>内存会话替换历史时，即使消息数量不变也要清除旧扩展状态并更新会话标识。</summary>
    [Fact]
    public void MemorySession_ResetsCustomStateWhenTranscriptIsReplaced()
    {
        using var fixture = new Fixture(false);
        var file = fixture.Write("memory.cjs", """
            module.exports = pi => {
              pi.registerCommand('write', { handler: (_, ctx) => { pi.appendEntry('old', {}); return ctx.sessionManager.getSessionId(); } });
              pi.registerCommand('read', { handler: (_, ctx) => JSON.stringify({ id: ctx.sessionManager.getSessionId(), count: ctx.sessionManager.getEntries().filter(e => e.type === 'custom').length }) });
            };
            """);
        var first = fixture.Runtime.Invoke(file, "write", "");
        Assert.True(first.Success, first.Error);
        fixture.Runner.MutableMessages.Clear();
        fixture.Runner.MutableMessages.Add(new UserMessage("new conversation"));
        var second = fixture.Runtime.Invoke(file, "read", "");
        Assert.True(second.Success, second.Error);
        using var state = JsonDocument.Parse(second.StatusMessage!);
        Assert.Equal(0, state.RootElement.GetProperty("count").GetInt32());
        Assert.NotEqual(first.StatusMessage, state.RootElement.GetProperty("id").GetString());
    }

    /// <summary>【CodingAgent】【扩展夹具】管理独立会话、真实 Node 进程及合成扩展。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-extension-session-" + Guid.NewGuid().ToString("N"));
        public FakeCodingAgentRunner Runner { get; } = new((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        public CodingAgentTreeSessionController? Tree { get; }
        public CodingAgentJavaScriptExtensionRuntime Runtime { get; }

        /// <summary>创建临时目录并绑定运行器。</summary>
        /// <param name="persistent">是否创建 JSONL。</param>
        public Fixture(bool persistent)
        {
            Directory.CreateDirectory(Root);
            Runner.MutableMessages.Add(new UserMessage("visible prompt"));
            if (persistent) Tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(Root, "session.jsonl"), Root));
            Runtime = new CodingAgentJavaScriptExtensionRuntime(Root);
            Runtime.BindSession(Runner, Tree);
        }

        /// <summary>写入一个合成扩展。</summary>
        /// <param name="name">文件名。</param>
        /// <param name="source">扩展源码。</param>
        /// <returns>文件绝对路径。</returns>
        public string Write(string name, string source)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, source);
            return path;
        }

        /// <summary>读取指定类型的 JSONL 条目。</summary>
        /// <param name="type">目标类型。</param>
        /// <returns>条目副本。</returns>
        public IReadOnlyList<JsonElement> Entries(string type) => File.ReadLines(Tree!.Path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(entry => entry.GetProperty("type").GetString() == type).ToArray();

        /// <summary>先终止扩展进程，再清理已校验的专用测试目录。</summary>
        public void Dispose()
        {
            Runtime.Dispose();
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(Root).StartsWith("tau-extension-session-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected extension test directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
