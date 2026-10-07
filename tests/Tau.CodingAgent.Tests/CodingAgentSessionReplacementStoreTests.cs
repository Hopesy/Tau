// 作者：xxx
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentSessionReplacementStoreTests
{
    /// <summary>新会话使用独立文件与标识，保留原会话字节、工作目录和可选父会话路径。</summary>
    [Fact]
    public void NewSession_CreatesIndependentFileWithoutChangingOriginal()
    {
        using var fixture = new StoreFixture();
        fixture.Store.AppendMessages([new UserMessage("original")], 0);
        var original = File.ReadAllText(fixture.Store.Path);
        var next = fixture.Store.CreateNewSession(fixture.Store.Path);
        Assert.NotEqual(fixture.Store.Path, next.Path);
        Assert.Equal(Path.GetDirectoryName(fixture.Store.Path), Path.GetDirectoryName(next.Path));
        Assert.NotEqual(fixture.Store.GetSessionHeader().Id, next.GetSessionHeader().Id);
        Assert.Equal(fixture.Root, next.GetSessionHeader().Cwd);
        Assert.Equal(fixture.Store.Path, next.GetSessionHeader().ParentSession);
        Assert.Empty(next.ReadExtensionSnapshot().Entries);
        Assert.Equal(original, File.ReadAllText(fixture.Store.Path));
    }

    /// <summary>分叉保留原消息标识和省略引用，使用其他分支上更新的最新标签并丢弃已删除标签。</summary>
    [Fact]
    public void Fork_PreservesSourceIdsAndLatestResolvedLabels()
    {
        using var fixture = new StoreFixture();
        var ids = fixture.Store.AppendMessages([new UserMessage("question"), new AssistantMessage([new TextContent("failed")])], 0);
        var originalLabel = fixture.Store.AppendLabelChange(ids[0], "old");
        var edit = fixture.Store.AppendContextEdit(ids[1], null);
        fixture.Store.AppendLabelChange(ids[0], "latest");
        fixture.Store.AppendLabelChange(ids[1], "removed");
        fixture.Store.AppendLabelChange(ids[1], null);
        fixture.Store.AppendMessages([new UserMessage("outside selected branch")], 0);
        var original = File.ReadAllText(fixture.Store.Path);
        var fork = fixture.Store.CreateBranchedSession(edit);
        var entries = fork.ReadExtensionSnapshot().Entries;
        Assert.DoesNotContain(entries, entry => entry.Id == originalLabel);
        Assert.Equal(ids, entries.Where(entry => entry.Type == "message").Select(entry => entry.Id));
        Assert.Equal("latest", fork.GetLabel(ids[0]));
        Assert.Null(fork.GetLabel(ids[1]));
        Assert.Equal(ids[1], Assert.Single(entries, entry => entry.Type == "context_edit").TargetId);
        Assert.Single(fork.BuildSessionProjection().Messages.OfType<UserMessage>());
        Assert.Empty(fork.BuildSessionProjection().Messages.OfType<AssistantMessage>());
        Assert.Equal(fixture.Store.Path, fork.GetSessionHeader().ParentSession);
        Assert.Equal(original, File.ReadAllText(fixture.Store.Path));
        for (var i = 0; i < entries.Count; i++) Assert.Equal(i == 0 ? null : entries[i - 1].Id, entries[i].ParentId);
    }

    /// <summary>标签用作压缩边界时分叉映射到下一真实条目，分支摘要 fromId 指向离开的叶节点。</summary>
    [Fact]
    public void Fork_RemapsRemovedLabelBoundaryAndKeepsBranchSummaryOrigin()
    {
        using var fixture = new StoreFixture();
        var oldUser = Assert.Single(fixture.Store.AppendMessages([new UserMessage("old")], 0));
        var label = fixture.Store.AppendLabelChange(oldUser, "old label");
        var kept = fixture.Store.AppendMessages([new UserMessage("keep"), new AssistantMessage([new TextContent("answer")])], 0);
        var compact = fixture.Store.AppendCompaction("history", label, 100);
        var fork = fixture.Store.CreateBranchedSession(compact);
        var saved = Assert.Single(fork.ReadExtensionSnapshot().Entries, entry => entry.Type == "compaction");
        Assert.Equal(kept[0], saved.FirstKeptEntryId);
        Assert.Contains(fork.BuildSessionProjection().Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "keep"));
        var summaryId = fixture.Store.BranchWithSummary(oldUser, "other branch summary");
        var summary = Assert.Single(fixture.Store.ReadExtensionSnapshot().Entries, entry => entry.Id == summaryId);
        Assert.Equal(compact, summary.FromId);
        Assert.Equal(oldUser, summary.ParentId);
    }

    /// <summary>为持久会话替换测试提供独立目录。</summary>
    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-replacement-" + Guid.NewGuid().ToString("N"));
        public CodingAgentTreeSessionStore Store { get; }

        /// <summary>创建独立存储。</summary>
        public StoreFixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "original.jsonl"), Root);
        }

        /// <summary>删除本测试创建的临时目录。</summary>
        public void Dispose() => Directory.Delete(Root, true);
    }
}
