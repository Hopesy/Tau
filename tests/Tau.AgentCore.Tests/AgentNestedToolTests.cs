// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Runtime;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【嵌套工具】验证共享执行入口的进度边界与标准会话存储。</summary>
public sealed class AgentNestedToolTests
{
    /// <summary>工具未等待的有效进度必须排空，完成后保存的回调不能再发布更新。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RunToolCall_DrainsUpdatesAndIgnoresLateCallbacks()
    {
        var tool = new UpdateTool();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var task = AgentToolCalls.RunAsync(new("parent/1", "update", "{}"), [tool], [], [],
            onUpdate: async update => { count++; received.TrySetResult(); await completion.Task; });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        completion.SetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsError);
        Assert.Equal(7, result.Result.Usage?.InputTokens);
        await tool.Callback!(new("late"));
        Assert.Equal(1, count);
    }

    /// <summary>AgentHarness JSONL 存储往返保留嵌套调用记录与用量，文件操作提取可读取子工具路径。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SessionStorage_PreservesNestedToolCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "tau-nested-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var args = JsonDocument.Parse("""{"path":"file.txt"}""");
            var message = new ToolResultMessage("parent", [new TextContent("done")])
            {
                ToolName = "root", Usage = new Usage(5, 3),
                NestedCalls = new([new() { Id = "parent/1", Name = "edit", Arguments = args.RootElement.Clone(), Status = "ok", DurationMs = 10 }], true)
            };
            var repo = new JsonlSessionRepo(root);
            var session = await repo.CreateAsync(root, id: "nested");
            await session.GetStorage().AppendEntryAsync(new MessageSessionEntry("entry", null, DateTimeOffset.UtcNow, message));
            var stored = await JsonlSessionStorage.OpenAsync((await session.GetMetadataAsync()).Path);
            var entries = await stored.GetEntriesAsync();
            var restored = Assert.IsType<ToolResultMessage>(Assert.Single(entries.OfType<MessageSessionEntry>()).Message);
            Assert.True(restored.NestedCalls?.Complete);
            Assert.Equal("file.txt", Assert.Single(restored.NestedCalls!.Calls).Arguments?.GetProperty("path").GetString());
            Assert.Equal(5, restored.Usage?.InputTokens);
            var operations = AgentCompaction.CreateFileOperations();
            AgentCompaction.ExtractFileOperationsFromMessage(restored, operations);
            Assert.Contains("file.txt", operations.Edited);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class UpdateTool : IAgentTool
    {
        public string Name => "update";
        public string Label => Name;
        public string Description => "Updates";
        public JsonElement ParameterSchema { get; } = Schema();
        public Func<ToolUpdate, Task>? Callback { get; private set; }
        /// <summary>构造独立的空对象参数定义。</summary>
        /// <returns>工具参数 Schema。</returns>
        private static JsonElement Schema()
        {
            using var doc = JsonDocument.Parse("""{"type":"object"}""");
            return doc.RootElement.Clone();
        }
        /// <summary>故意不等待进度，验证执行器负责排空有效更新。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">保存供延迟调用的回调。</param>
        /// <returns>带工具用量的最终结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
        {
            Callback = onUpdate;
            _ = onUpdate?.Invoke(new("progress"));
            return Task.FromResult(new ToolResult([]) { Usage = new Usage(7, 0) });
        }
    }
}
