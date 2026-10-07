// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【会话投影回归】验证原始历史、上下文编辑和协议元数据独立保存。</summary>
public sealed class CodingAgentSessionProjectionTests
{
    /// <summary>两种格式往返保留推理签名、响应信息、工具详情及用量。</summary>
    /// <param name="tree">是否使用 JSONL。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persistence_PreservesProtocolMetadata(bool tree)
    {
        using var fixture = new Fixture();
        var timestamp = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
        var usage = new Usage(12, 4, 3, 2) { CacheWrite1hTokens = 1, ReasoningTokens = 2, TotalTokens = 21 };
        ChatMessage[] messages =
        [
            new UserMessage("user") { Timestamp = timestamp },
            new AssistantMessage([
                new TextContent("answer") { TextSignature = "text-signature" },
                new ThinkingContent("reasoning") { ThinkingSignature = "reasoning-signature", Redacted = true },
                new ToolCallContent("call", "read", "{}") { ThoughtSignature = "call-signature", Namespace = "mcp_workspace" }
            ])
            {
                Usage = usage, StopReason = StopReason.Aborted, ErrorMessage = "interrupted", ResponseId = "response-id",
                ResponseModel = "actual-model", RawStopReason = "cancelled", EndTurn = false,
                Api = "test-api", Provider = "test", Model = "declared-model", Timestamp = timestamp,
                Deferred = new("test", "declared-model", "test-api", "deferred-id") { Data = new { position = 2 } }
            },
            new ToolResultMessage("call", [new TextContent("result")], true)
            {
                ToolName = "read", Details = new { path = "synthetic.txt", count = 3 }, Usage = usage,
                Timestamp = timestamp, AddedToolNames = ["discovered"]
            }
        ];
        var restored = fixture.RoundTrip(messages, tree);
        Assert.Equal(timestamp, Assert.IsType<UserMessage>(restored[0]).Timestamp);
        var assistant = Assert.IsType<AssistantMessage>(restored[1]);
        Assert.Equal("text-signature", Assert.IsType<TextContent>(assistant.Content[0]).TextSignature);
        var thinking = Assert.IsType<ThinkingContent>(assistant.Content[1]);
        Assert.Equal("reasoning-signature", thinking.ThinkingSignature);
        Assert.True(thinking.Redacted);
        Assert.Equal("call-signature", Assert.IsType<ToolCallContent>(assistant.Content[2]).ThoughtSignature);
        Assert.Equal("mcp_workspace", Assert.IsType<ToolCallContent>(assistant.Content[2]).Namespace);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
        Assert.Equal("interrupted", assistant.ErrorMessage);
        Assert.Equal("response-id", assistant.ResponseId);
        Assert.Equal("actual-model", assistant.ResponseModel);
        Assert.Equal("cancelled", assistant.RawStopReason);
        Assert.False(assistant.EndTurn);
        Assert.Equal(1, assistant.Usage!.Value.CacheWrite1hTokens);
        Assert.Equal(2, assistant.Usage.Value.ReasoningTokens);
        Assert.Equal(21, assistant.Usage.Value.TotalTokens);
        Assert.Equal(2, Assert.IsType<JsonElement>(assistant.Deferred!.Data).GetProperty("position").GetInt32());
        var tool = Assert.IsType<ToolResultMessage>(restored[2]);
        Assert.Equal("read", tool.ToolName);
        Assert.Equal(timestamp, tool.Timestamp);
        Assert.Equal("synthetic.txt", Assert.IsType<JsonElement>(tool.Details).GetProperty("path").GetString());
        Assert.Equal(["discovered"], tool.AddedToolNames);
        Assert.Equal(21, tool.Usage!.Value.TotalTokens);
    }

    /// <summary>Harness 的自定义消息、命令输出和摘要不会保存后丢失。</summary>
    /// <param name="tree">是否使用 JSONL。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persistence_PreservesHarnessMessages(bool tree)
    {
        using var fixture = new Fixture();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ChatMessage[] messages =
        [
            new AgentCustomMessage("state", "visible", false, new { nested = new[] { 1, 2 } }, timestamp),
            new AgentBashExecutionMessage("echo synthetic", "output", 7, true, true, "output.txt", timestamp, true),
            new AgentBranchSummaryMessage("branch", "source-id", timestamp),
            new AgentCompactionSummaryMessage("compact", 100, timestamp)
        ];
        var result = fixture.RoundTrip(messages, tree);
        Assert.Equal(4, result.Count);
        Assert.Equal(2, Assert.IsType<JsonElement>(Assert.IsType<AgentCustomMessage>(result[0]).Details).GetProperty("nested").GetArrayLength());
        Assert.Equal(messages[1], result[1]);
        Assert.Equal(messages[2], result[2]);
        Assert.Equal(messages[3], result[3]);
    }

    /// <summary>省略和替换只影响模型投影，原始历史保持完整。</summary>
    [Fact]
    public void ContextEdits_LeaveRawEntriesUntouched()
    {
        using var fixture = new Fixture();
        var ids = fixture.Store.AppendMessages([
            new UserMessage("request"), new AssistantMessage([new TextContent("original")]) { Usage = new(9, 2) },
            new ToolResultMessage("call", [new TextContent("raw result")], true) { ToolName = "read", Details = new { path = "large.txt" } }
        ], 0);
        fixture.Store.AppendContextEdit(ids[1], null);
        fixture.Store.AppendContextEdit(ids[2], Json("""{"content":"short result"}"""));
        var projection = fixture.Store.BuildSessionProjection();
        Assert.Equal(["user", "toolResult"], projection.Messages.Select(message => message.Role));
        var tool = Assert.IsType<ToolResultMessage>(projection.Messages[1]);
        Assert.Equal("short result", Assert.IsType<TextContent>(Assert.Single(tool.Content)).Text);
        Assert.True(tool.IsError);
        Assert.Equal("read", tool.ToolName);
        var raw = projection.Entries.Single(entry => entry.SourceEntry.GetProperty("id").GetString() == ids[1]);
        Assert.Empty(raw.Messages);
        Assert.Equal("original", raw.SourceEntry.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(5, projection.Entries.Count);
        Assert.Equal(2, new CodingAgentTreeSessionStore(fixture.Store.Path).LoadCurrentBranchSnapshot().Messages.Count);
    }

    /// <summary>最新编辑覆盖省略，字符串替换规范化并保留助手用量。</summary>
    [Fact]
    public void LatestEdit_RestoresContentAndPreservesUsage()
    {
        using var fixture = new Fixture();
        var id = Assert.Single(fixture.Store.AppendMessages([new AssistantMessage([new TextContent("original")]) { Usage = new(10, 1), Model = "actual", Provider = "test" }], 0));
        fixture.Store.AppendContextEdit(id, Json("""{"content":"first"}"""));
        fixture.Store.AppendContextEdit(id, null);
        var edit = fixture.Store.AppendContextEdit(id, Json("""{"content":"restored"}"""));
        var projection = fixture.Store.BuildSessionProjection();
        var result = Assert.IsType<AssistantMessage>(Assert.Single(projection.Messages));
        Assert.Equal("restored", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(new Usage(10, 1, 0, 0) { TotalTokens = 11 }, result.Usage);
        Assert.Equal("actual", projection.Model!.ModelId);
        var storedEdit = projection.Entries.Single(entry => entry.SourceEntry.GetProperty("id").GetString() == edit).SourceEntry;
        Assert.Equal(JsonValueKind.Array, storedEdit.GetProperty("replacement").GetProperty("content").ValueKind);
    }

    /// <summary>编辑跟随分支，不能编辑其他分支、系统或不存在的条目。</summary>
    [Fact]
    public void Edits_ValidateBranchAndRole()
    {
        using var fixture = new Fixture();
        var ids = fixture.Store.AppendMessages([new SystemMessage("system"), new UserMessage("first"), new AssistantMessage([new TextContent("answer")])], 0);
        Assert.Throws<ArgumentException>(() => fixture.Store.AppendContextEdit(ids[0], null));
        Assert.Throws<ArgumentException>(() => fixture.Store.AppendContextEdit("missing", null));
        fixture.Store.AppendContextEdit(ids[2], null);
        fixture.Store.BranchTo(ids[1]);
        Assert.Throws<ArgumentException>(() => fixture.Store.AppendContextEdit(ids[2], null));
        Assert.Equal(2, fixture.Store.BuildSessionProjection().Messages.Count);
        Assert.Equal(3, fixture.Store.BuildSessionProjection(ids[2]).Messages.Count);
        Assert.Empty(fixture.Store.BuildSessionProjection(null).Messages);
    }

    /// <summary>错误替换形状在落盘前拒绝。</summary>
    /// <param name="replacement">错误 JSON。</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("""{"content":false}""")]
    public void InvalidReplacement_DoesNotAppend(string replacement)
    {
        using var fixture = new Fixture();
        var id = Assert.Single(fixture.Store.AppendMessages([new UserMessage("original")], 0));
        var before = File.ReadAllText(fixture.Store.Path);
        Assert.Throws<ArgumentException>(() => fixture.Store.AppendContextEdit(id, Json(replacement)));
        Assert.Equal(before, File.ReadAllText(fixture.Store.Path));
    }

    /// <summary>保留范围里的旧压缩条目不重复贡献摘要。</summary>
    [Fact]
    public void Compaction_ProjectsOnlyLatestCheckpoint()
    {
        using var fixture = new Fixture();
        var ids = fixture.Store.AppendMessages([new SystemMessage("system"), new UserMessage("old"), new AssistantMessage([new TextContent("kept")])], 0);
        var first = fixture.Store.AppendCompaction("first summary", ids[2], 100);
        fixture.Store.AppendMessages([new UserMessage("new")], 0);
        fixture.Store.AppendCompaction("second summary", first, 200);
        fixture.Store.AppendThinkingLevelChange("high");
        var projection = fixture.Store.BuildSessionProjection();
        Assert.Single(projection.Messages.OfType<SystemMessage>());
        Assert.Equal("second summary", Assert.Single(projection.Messages.OfType<AgentCompactionSummaryMessage>()).Summary);
        Assert.Equal("new", Assert.Single(projection.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
        Assert.Empty(projection.Entries.Single(entry => entry.SourceEntry.GetProperty("id").GetString() == first).Messages);
        Assert.Equal("high", projection.ThinkingLevel);
    }

    /// <summary>真实 Node 读取的上下文投影保留来源，但省略指定消息。</summary>
    [Fact]
    public void Extension_ReadsContextEntriesAndProjection()
    {
        using var fixture = new Fixture();
        var ids = fixture.Store.AppendMessages([new UserMessage("request"), new AssistantMessage([new TextContent("removed")])], 0);
        fixture.Store.AppendContextEdit(ids[1], null);
        var tree = new CodingAgentTreeSessionController(fixture.Store);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.RestoreSession(tree.LoadSnapshot().ToFlatSnapshot());
        var file = Path.Combine(fixture.Root, "projection.js");
        File.WriteAllText(file, """
            module.exports = pi => pi.registerCommand('read', { handler: (_, ctx) => {
              const entries = ctx.sessionManager.buildContextEntries();
              const projection = ctx.sessionManager.buildSessionProjection();
              if (!entries.some(e => e.type === 'context_edit')) throw Error('missing edit');
              const source = projection.entries.find(e => e.sourceEntry.message?.content?.[0]?.text === 'removed');
              if (!source || source.messages.length) throw Error('source changed');
              if (projection.messages.some(m => m.role === 'assistant')) throw Error('omission ignored');
              return 'projected';
            }});
            """);
        using var runtime = new CodingAgentJavaScriptExtensionRuntime(fixture.Root);
        runtime.BindSession(runner, tree);
        var result = runtime.Invoke(file, "read", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("projected", result.StatusMessage);
    }

    /// <summary>分支导出复制编辑及目标映射，重新打开后仍使用编辑后的上下文。</summary>
    [Fact]
    public void Export_PreservesEditTargets()
    {
        using var fixture = new Fixture();
        var id = Assert.Single(fixture.Store.AppendMessages([new UserMessage("original")], 0));
        fixture.Store.AppendContextEdit(id, Json("""{"content":"replacement"}"""));
        var controller = new CodingAgentTreeSessionController(fixture.Store);
        var exported = Path.Combine(fixture.Root, "export.jsonl");
        controller.ExportCurrentBranch(exported);
        var projection = new CodingAgentTreeSessionStore(exported).BuildSessionProjection();
        Assert.Equal("replacement", Assert.IsType<UserMessage>(Assert.Single(projection.Messages)).Content.OfType<TextContent>().Single().Text);
        var edit = projection.Entries.Single(entry => entry.SourceEntry.GetProperty("type").GetString() == "context_edit").SourceEntry;
        Assert.Contains(projection.Entries, entry => entry.SourceEntry.GetProperty("id").GetString() == edit.GetProperty("targetId").GetString());
    }

    /// <summary>SDK 上下文编辑同步运行器和保存游标，重复保存不会把旧内容加回模型历史。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sdk_ContextEditUpdatesRunnerWithoutDuplicatingHistory()
    {
        using var fixture = new Fixture();
        using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = Path.Combine(fixture.Root, "agent"),
            IncludeExtensions = false, IncludeSkills = false, IncludeContextFiles = false,
            IncludePromptTemplates = false, NoTools = CodingAgentSdkNoToolsMode.All, SystemPrompt = "base"
        });
        session.Runner.AppendMessage(new UserMessage("original"));
        session.Save();
        var tree = session.TreeSessionController!;
        var target = tree.Store.BuildContextEntries().Single(entry =>
            entry.GetProperty("type").GetString() == "message" && entry.GetProperty("message").GetProperty("role").GetString() == "user");
        session.EditContext(target.GetProperty("id").GetString()!, Json("""{"content":"edited"}"""));
        session.Save();
        session.Save();
        Assert.Equal("edited", Assert.Single(session.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
        var projection = tree.Store.BuildSessionProjection();
        Assert.Single(projection.Entries, entry => entry.SourceEntry.GetProperty("type").GetString() == "context_edit");
        Assert.Single(projection.Entries, entry => entry.SourceEntry.GetProperty("type").GetString() == "message" &&
            entry.SourceEntry.GetProperty("message").GetProperty("role").GetString() == "user");
        Assert.Equal("edited", Assert.Single(projection.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
    }

    /// <summary>读取 pi 标准 JSONL，恢复模型、内容编辑、工具参数和消息元数据。</summary>
    [Fact]
    public void PiJsonl_RestoresProjectionAndProtocolFields()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.Path, """
            {"type":"session","version":3,"id":"pi-session","timestamp":"2026-10-04T00:00:00Z","cwd":"synthetic"}
            {"type":"model_change","id":"model","parentId":null,"timestamp":"2026-10-04T00:00:00Z","provider":"test","modelId":"pi-model"}
            {"type":"thinking_level_change","id":"thinking","parentId":"model","timestamp":"2026-10-04T00:00:00Z","thinkingLevel":"high"}
            {"type":"message","id":"user","parentId":"thinking","timestamp":"2026-10-04T00:00:00Z","message":{"role":"user","content":"original","timestamp":1791072000000}}
            {"type":"message","id":"assistant","parentId":"user","timestamp":"2026-10-04T00:00:00Z","message":{"role":"assistant","content":[{"type":"toolCall","id":"call","name":"read","arguments":{"path":"a<b>.txt","nested":[1,true]}}],"api":"test","provider":"test","model":"pi-model","stopReason":"toolUse","usage":{"input":10,"output":2,"cacheRead":3,"cacheWrite":4,"totalTokens":19},"timestamp":1791072000000}}
            {"type":"custom_message","id":"custom","parentId":"assistant","timestamp":"2026-10-04T00:00:00Z","customType":"status","content":"state","display":false,"details":{"count":1}}
            {"type":"context_edit","id":"edit","parentId":"custom","timestamp":"2026-10-04T00:00:00Z","targetId":"user","replacement":{"content":"edited"}}
            """);
        var snapshot = fixture.Store.LoadCurrentBranchSnapshot();
        Assert.Equal("pi-model", snapshot.Model);
        Assert.Equal("high", snapshot.ThinkingLevel);
        var projection = fixture.Store.BuildSessionProjection();
        Assert.Equal("edited", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(projection.Messages[0]).Content)).Text);
        var assistant = Assert.IsType<AssistantMessage>(projection.Messages[1]);
        Assert.Equal(StopReason.ToolUse, assistant.StopReason);
        Assert.Equal(new Usage(10, 2, 3, 4) { TotalTokens = 19 }, assistant.Usage);
        var arguments = Json(Assert.IsType<ToolCallContent>(Assert.Single(assistant.Content)).Arguments);
        Assert.Equal("a<b>.txt", arguments.GetProperty("path").GetString());
        Assert.Equal(2, arguments.GetProperty("nested").GetArrayLength());
        Assert.Equal(1791072000000, assistant.Timestamp!.Value.ToUnixTimeMilliseconds());
        var custom = Assert.IsType<AgentCustomMessage>(projection.Messages[2]);
        Assert.False(custom.Display);
        Assert.Equal(1, Assert.IsType<JsonElement>(custom.Details).GetProperty("count").GetInt32());
    }

    /// <summary>旧 Tau JSONL 的模型名、字符串时间和用量仍能恢复，并可追加标准消息。</summary>
    [Fact]
    public void LegacyJsonl_RemainsReadableWhenAppendingPiMessages()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.Path, """
            {"type":"session","version":1,"id":"legacy","timestamp":"2026-10-04T00:00:00Z","cwd":"synthetic"}
            {"type":"model_change","id":"model","timestamp":"2026-10-04T00:00:00Z","provider":"test","model":"legacy-model"}
            {"type":"message","id":"assistant","parentId":"model","timestamp":"2026-10-04T00:00:00Z","message":{"role":"assistant","content":[{"type":"toolCall","id":"old-call","name":"read","arguments":"{\"path\":\"old.txt\"}"}],"stopReason":2,"usage":{"inputTokens":10,"outputTokens":3},"timestamp":"2026-10-04T00:00:00Z"}}
            """);
        Assert.Equal("legacy-model", fixture.Store.LoadCurrentBranchSnapshot().Model);
        var before = Assert.IsType<AssistantMessage>(Assert.Single(fixture.Store.BuildSessionProjection().Messages));
        Assert.Equal(new Usage(10, 3), before.Usage);
        Assert.Equal("old.txt", Json(Assert.IsType<ToolCallContent>(Assert.Single(before.Content)).Arguments).GetProperty("path").GetString());
        fixture.Store.AppendMessages([new UserMessage("next")], 0);
        var after = fixture.Store.LoadCurrentBranchSnapshot();
        Assert.Equal(2, after.Messages.Count);
        Assert.Equal("legacy-model", after.Model);
    }

    /// <summary>标准对象工具参数可以通过上下文编辑替换，调用标识和消息元数据保持独立。</summary>
    [Fact]
    public void ContextEdit_ParsesObjectToolArguments()
    {
        using var fixture = new Fixture();
        var id = Assert.Single(fixture.Store.AppendMessages([
            new AssistantMessage([new ToolCallContent("call", "read", "{}")]) { StopReason = StopReason.ToolUse }
        ], 0));
        fixture.Store.AppendContextEdit(id, Json("""{"content":[{"type":"toolCall","id":"call","name":"read","arguments":{"path":"updated.txt"}}]}"""));
        var message = Assert.IsType<AssistantMessage>(Assert.Single(fixture.Store.BuildSessionProjection().Messages));
        Assert.Equal(StopReason.ToolUse, message.StopReason);
        Assert.Equal("updated.txt", Json(Assert.IsType<ToolCallContent>(Assert.Single(message.Content)).Arguments).GetProperty("path").GetString());
    }

    /// <summary>超出合法范围的时间戳与损坏尾行一样跳过，不使已保存会话无法恢复。</summary>
    [Fact]
    public void InvalidEpoch_DoesNotBreakEarlierHistory()
    {
        using var fixture = new Fixture();
        var id = Assert.Single(fixture.Store.AppendMessages([new UserMessage("valid")], 0));
        File.AppendAllText(fixture.Store.Path, $$$"""
            {"type":"message","id":"invalid","parentId":"{{{id}}}","timestamp":"2026-10-04T00:00:00Z","message":{"role":"user","content":"invalid","timestamp":9223372036854775807}}
            """);
        Assert.Single(fixture.Store.LoadCurrentBranchSnapshot().Messages);
    }

    /// <summary>创建独立的 JSON 参数。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>独立元素。</returns>
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-projection-" + Guid.NewGuid().ToString("N"));
        public CodingAgentTreeSessionStore Store { get; }
        /// <summary>建立独立临时树。</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "session.jsonl"), Root);
        }
        /// <summary>通过指定格式保存并重新读取。</summary>
        /// <param name="messages">原始消息。</param>
        /// <param name="tree">是否使用树格式。</param>
        /// <returns>重新读取的消息。</returns>
        public IReadOnlyList<ChatMessage> RoundTrip(IReadOnlyList<ChatMessage> messages, bool tree)
        {
            if (tree)
            {
                Store.AppendMessages(messages, 0);
                return new CodingAgentTreeSessionStore(Store.Path).LoadCurrentBranchSnapshot().Messages;
            }
            var flat = new CodingAgentSessionStore(Path.Combine(Root, "session.json"));
            flat.Save(messages, new Model { Provider = "test", Id = "model", Name = "Model", Api = "test" });
            return flat.LoadStrict().Messages;
        }
        /// <summary>清理经过路径校验的专用目录。</summary>
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("tau-projection-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(path, recursive: true);
        }
    }
}
