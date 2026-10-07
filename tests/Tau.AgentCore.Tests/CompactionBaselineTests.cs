// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【压缩恢复回归】验证压缩快照、分支和 JSONL 持久化的声明语义。</summary>
public sealed class CompactionBaselineTests
{
    /// <summary>压缩应折叠所有旧声明，尾部与压缩后的更新不能重复或丢失。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Compaction_ReplaysBaselineAcrossRepeatedCompactionAndBranching()
    {
        var session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>());
        var root = await session.AppendMessageAsync(Baseline());
        await session.AppendMessageAsync(new UserMessage("old"));
        var kept = await session.AppendMessageAsync(new UserMessage("kept"));
        await session.AppendMessageAsync(new SystemMessage("update")
        {
            Sections = new Dictionary<string, string?> { ["mode"] = "new", ["drop"] = null },
            ToolsRemoved = [new("old")], ToolsAdded = [Definition("new")]
        });
        await session.AppendMessageAsync(new AssistantMessage([new TextContent("answer")]));
        await session.AppendCompactionAsync("summary", kept, 100);
        var compacted = await session.BuildContextAsync();
        Assert.Equal(["system", "compactionSummary", "user", "assistant"], compacted.Messages.Select(message => message.Role));
        var system = Assert.IsType<SystemMessage>(compacted.Messages[0]);
        Assert.Equal("base\n\nupdate", system.Content);
        Assert.Equal("new", Assert.Single(system.Sections!).Value);
        Assert.Equal("new", Assert.Single(system.ToolsAdded!).Name);
        Assert.Null(system.ToolsRemoved);
        var entry = Assert.Single((await session.GetBranchAsync()).OfType<CompactionSessionEntry>());
        Assert.Equal(entry.Timestamp, system.Timestamp);

        await session.AppendMessageAsync(new SystemMessage("after") { ToolsRemoved = [new("new")] });
        Assert.Equal(2, (await session.BuildContextAsync()).Messages.OfType<SystemMessage>().Count());
        await session.AppendCompactionAsync("second", "no-tail", 20);
        var second = await session.BuildContextAsync();
        Assert.Equal(["system", "compactionSummary"], second.Messages.Select(message => message.Role));
        Assert.Equal("base\n\nupdate\n\nafter\n\nnew", Transcript.GetCurrentSystemPrompt(second.Messages));
        Assert.Empty(Transcript.GetCurrentTools(second.Messages));

        await session.MoveToAsync(root, new SessionBranchSummary("branch"));
        var branch = await session.BuildContextAsync();
        Assert.Equal("base\n\noriginal\n\ndiscard", Transcript.GetCurrentSystemPrompt(branch.Messages));
        Assert.Equal("old", Assert.Single(Transcript.GetCurrentTools(branch.Messages)).Name);
        Assert.IsType<AgentBranchSummaryMessage>(branch.Messages[^1]);
    }

    /// <summary>压缩声明通过真实 JSONL 文件重新打开，保留工具 schema、段落和毫秒时间。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Jsonl_ReopensCompactionBaseline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-compaction-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session.jsonl");
            var storage = await JsonlSessionStorage.CreateAsync(path, directory, "baseline-test");
            var session = new AgentHarnessSession<JsonlSessionMetadata>(storage);
            await session.AppendMessageAsync(Baseline());
            await session.AppendMessageAsync(new UserMessage("old"));
            await session.AppendCompactionAsync("summary", "none", 12);
            var reopened = await JsonlSessionStorage.OpenAsync(path);
            var context = await new AgentHarnessSession<JsonlSessionMetadata>(reopened).BuildContextAsync();
            var system = Assert.IsType<SystemMessage>(context.Messages[0]);
            Assert.Equal("base", system.Content);
            Assert.Equal("original", system.Sections!["mode"]);
            Assert.Equal("object", Assert.Single(system.ToolsAdded!).ParameterSchema.GetProperty("type").GetString());
            using var wire = JsonDocument.Parse(File.ReadLines(path).Last());
            var saved = wire.RootElement.GetProperty("systemMessage");
            Assert.Equal("system", saved.GetProperty("role").GetString());
            Assert.Equal(wire.RootElement.GetProperty("timestamp").GetInt64(), saved.GetProperty("timestamp").GetInt64());
            Assert.Equal(saved.GetProperty("timestamp").GetInt64(), system.Timestamp.ToUnixTimeMilliseconds());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>无系统声明的旧压缩条目继续恢复摘要和尾部。</summary>
    [Fact]
    public void LegacyCompaction_WithoutBaselineRemainsReadable()
    {
        var time = DateTimeOffset.UnixEpoch;
        var context = AgentHarnessSession<SessionMetadata>.BuildSessionContext([
            new MessageSessionEntry("u", null, time, new UserMessage("kept")),
            new CompactionSessionEntry("c", "u", time, "summary", "u", 10)
        ]);
        Assert.Equal(["compactionSummary", "user"], context.Messages.Select(message => message.Role));
    }

    /// <summary>无 usage 时只估算最终系统状态，工具移除和分节覆盖不会累计旧成本。</summary>
    [Fact]
    public void TokenEstimate_UsesEffectiveSystemAndToolDefinitions()
    {
        ChatMessage[] messages = [Baseline(), new SystemMessage("")
        {
            Sections = new Dictionary<string, string?> { ["mode"] = "12345678", ["drop"] = null },
            ToolsRemoved = [new("old")]
        }, new UserMessage("1234")];
        var estimate = AgentCompaction.EstimateContextTokens(messages);
        Assert.Equal(4, estimate.Tokens);
        Assert.Equal(estimate.Tokens, estimate.TrailingTokens);
        Assert.True(AgentCompaction.EstimateTokens(Baseline()) > 5);
    }

    /// <summary>压缩和分支摘要的对话预算不应被大型系统更新占满。</summary>
    [Fact]
    public void SummaryPreparation_ExcludesSystemFromConversationBudget()
    {
        var time = DateTimeOffset.UnixEpoch;
        SessionTreeEntry[] entries = [
            new MessageSessionEntry("s", null, time, Baseline()),
            new MessageSessionEntry("u1", "s", time, new UserMessage("1234")),
            new MessageSessionEntry("a1", "u1", time, new AssistantMessage([new TextContent("1234")])),
            new MessageSessionEntry("u2", "a1", time, new UserMessage("1234")),
            new MessageSessionEntry("delta", "u2", time, new SystemMessage(new string('x', 4000))),
            new MessageSessionEntry("a2", "delta", time, new AssistantMessage([new TextContent("1234")]))
        ];
        Assert.Null(AgentCompaction.PrepareCompaction([entries[0]]));
        var preparation = AgentCompaction.PrepareCompaction(entries, new(KeepRecentTokens: 2));
        Assert.NotNull(preparation);
        Assert.Equal("u2", preparation.FirstKeptEntryId);
        Assert.Equal(2, preparation.MessagesToSummarize.Count);
        Assert.DoesNotContain(preparation.MessagesToSummarize, message => message is SystemMessage);
        var branch = AgentBranchSummaries.PrepareBranchEntries(entries, tokenBudget: 10);
        Assert.Equal(4, branch.Messages.Count);
        Assert.Equal(4, branch.TotalTokens);
    }

    /// <summary>损坏的压缩系统消息必须在 JSONL 读取时报告错误。</summary>
    /// <param name="invalidRole">是否破坏角色，否则破坏正文。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Jsonl_RejectsMalformedCompactionBaseline(bool invalidRole)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-invalid-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session.jsonl");
            var storage = await JsonlSessionStorage.CreateAsync(path, directory, "invalid-test");
            var session = new AgentHarnessSession<JsonlSessionMetadata>(storage);
            await session.AppendMessageAsync(Baseline());
            await session.AppendCompactionAsync("summary", "none", 1);
            var lines = File.ReadAllLines(path);
            var node = System.Text.Json.Nodes.JsonNode.Parse(lines[^1])!;
            node["systemMessage"]![invalidRole ? "role" : "content"] = invalidRole ? "user" : null;
            lines[^1] = node.ToJsonString();
            File.WriteAllLines(path, lines);
            await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>构造可重放的分节与工具基线。</summary>
    /// <returns>初始声明。</returns>
    private static SystemMessage Baseline() => new("base")
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Sections = new Dictionary<string, string?> { ["mode"] = "original", ["drop"] = "discard" },
        ToolsAdded = [Definition("old")]
    };

    /// <summary>创建独立工具 schema。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>纯声明。</returns>
    private static Tool Definition(string name)
    {
        using var json = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(name, "tool", json.RootElement.Clone());
    }
}
