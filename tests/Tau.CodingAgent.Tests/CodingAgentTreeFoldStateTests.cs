using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentTreeFoldStateTests
{
    [Fact]
    public void AppendTreeFoldState_WritesAppendOnlySessionMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-tree-fold-state-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new CodingAgentTreeSessionStore(path);

            var entryId = store.AppendTreeFoldState(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                " entry-b ",
                "entry-a",
                "ENTRY-A"
            });

            var state = store.LoadTreeFoldState();
            Assert.NotNull(state);
            Assert.Equal(["entry-a", "entry-b"], state!.CollapsedEntryIds);

            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, line =>
                line.Contains(entryId, StringComparison.Ordinal) &&
                line.Contains("\"type\":\"tree_state\"", StringComparison.Ordinal) &&
                line.Contains("\"collapsedEntryIds\"", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AppendTreeFoldState_EmptySetClearsPreviousState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-tree-fold-state-clear-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new CodingAgentTreeSessionStore(path);
            store.AppendTreeFoldState(new HashSet<string>(["root"], StringComparer.OrdinalIgnoreCase));
            store.AppendTreeFoldState(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var state = store.LoadTreeFoldState();

            Assert.NotNull(state);
            Assert.Empty(state!.CollapsedEntryIds);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadTreeFoldState_IgnoresMalformedTreeStateEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-tree-fold-state-bad-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new CodingAgentTreeSessionStore(path);
            File.AppendAllText(
                path,
                """{"type":"tree_state","id":"badstate","timestamp":"2026-05-23T00:00:00Z","collapsedEntryIds":[1]}""" + "\n");

            var state = store.LoadTreeFoldState();
            var snapshot = store.LoadCurrentBranchSnapshot();

            Assert.Null(state);
            Assert.Empty(snapshot.Messages);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task InteractiveTreeCommand_PersistsReturnedFoldStateToSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-tree-fold-state-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
            runner.MutableMessages.Add(new UserMessage("root prompt"));
            runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
            var tree = CodingAgentTreeSessionController.OpenOrCreate(path);
            var router = new CodingAgentCommandRouter(
                runner,
                treeSessionController: tree,
                treeNavigator: (_, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(
                    null,
                    0,
                    1,
                    new HashSet<string>(["entry-a"], StringComparer.OrdinalIgnoreCase))));

            var result = await router.TryHandleAsync("/tree --interactive");

            Assert.Equal("tree navigator cancelled", result.Message);
            var state = tree.LoadTreeFoldState();
            Assert.NotNull(state);
            Assert.Equal(["entry-a"], state!.CollapsedEntryIds);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
