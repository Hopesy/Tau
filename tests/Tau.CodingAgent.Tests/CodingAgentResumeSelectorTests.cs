using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentResumeSelectorTests
{
    [Fact]
    public void CreateSelectList_SelectsCurrentSessionAndFormatsDescription()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var otherPath = Path.Combine(Path.GetTempPath(), "tau-other-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var state = new CodingAgentResumeSelectorState(
            currentPath,
            Environment.CurrentDirectory,
            [
                new CodingAgentResumeSessionInfo(
                    currentPath,
                    "Current session",
                    "Current session",
                    "openai",
                    "gpt-5.4",
                    12,
                    new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                    Environment.CurrentDirectory,
                    true),
                new CodingAgentResumeSessionInfo(
                    otherPath,
                    "Other session",
                    "Other session",
                    "google",
                    "gemini-2.5-pro",
                    4,
                    new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                    Environment.CurrentDirectory,
                    false)
            ]);

        var selector = CodingAgentResumeSelector.CreateSelectList(state);

        Assert.Equal(currentPath, selector.SelectedItem?.Value);
        Assert.Equal(2, selector.FilteredItems.Count);
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == currentPath &&
            item.Description is not null &&
            item.Description.Contains("current, openai/gpt-5.4, 12 messages", StringComparison.Ordinal));
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == otherPath &&
            item.Description is not null &&
            item.Description.Contains("google/gemini-2.5-pro, 4 messages", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateSelectList_ThreadedSortRendersTreePrefixesFromParentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-resume-selector-threaded-" + Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(directory, "parent.jsonl");
        var childPath = Path.Combine(directory, "coding-agent-sessions", "child.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);

        try
        {
            CreateSessionFile(parentPath, provider: "openai", model: "gpt-5.4", name: "Parent session", "parent branch", cwd: directory);
            CreateSessionFile(childPath, provider: "google", model: "gemini-2.5-pro", name: "Child session", "child branch", parentSessionPath: parentPath, cwd: directory);

            File.SetLastWriteTimeUtc(parentPath, new DateTime(2026, 5, 24, 20, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(childPath, new DateTime(2026, 5, 24, 20, 30, 0, DateTimeKind.Utc));

            var sessions = CodingAgentTreeSessionStore.ListAvailableSessions(parentPath);
            Assert.Equal(Path.GetFullPath(parentPath), sessions.Single(session => session.FilePath.Equals(childPath, StringComparison.OrdinalIgnoreCase)).ParentSessionPath);

            var selector = CodingAgentResumeSelector.CreateSelectList(
                new CodingAgentResumeSelectorState(parentPath, directory, sessions));

            Assert.Equal(parentPath, selector.SelectedItem?.Value);
            Assert.Contains(selector.FilteredItems, item => item.Value == childPath && item.Label.StartsWith("└─ ", StringComparison.Ordinal));
            Assert.Contains(selector.FilteredItems, item => item.Value == parentPath && !item.Label.StartsWith("└─ ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateComponent_SearchInputFiltersByDisplayNameAndPath()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var archivedPath = Path.Combine(Path.GetTempPath(), "tau-archived", Guid.NewGuid().ToString("N"), "review.jsonl");
        var component = CodingAgentResumeSelector.CreateComponent(
            new CodingAgentResumeSelectorState(
                currentPath,
                Environment.CurrentDirectory,
                [
                    new CodingAgentResumeSessionInfo(
                        currentPath,
                        "Current session",
                        "Current session",
                        "openai",
                        "gpt-5.4",
                        12,
                        new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        true),
                    new CodingAgentResumeSessionInfo(
                        archivedPath,
                        null,
                        "Review checkpoint",
                        "google",
                        "gemini-2.5-pro",
                        4,
                        new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        false)
                ]));

        Assert.True(component.HandleInput(CharKey('r')).Consumed);
        Assert.True(component.HandleInput(CharKey('e')).Consumed);
        Assert.True(component.HandleInput(CharKey('v')).Consumed);
        Assert.Equal("rev", component.Filter);
        Assert.Equal([archivedPath], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains("Search: rev", component.Render(80), StringComparer.Ordinal);

        Assert.True(component.HandleInput(Key(ConsoleKey.Backspace)).Consumed);
        Assert.Equal("re", component.Filter);
        Assert.Equal(2, component.FilteredItems.Count);

        Assert.True(component.HandleInput(Key(ConsoleKey.Backspace)).Consumed);
        Assert.Equal("r", component.Filter);
        Assert.Equal(2, component.FilteredItems.Count);
    }

    [Fact]
    public void CreateComponent_CtrlSTogglesSortModesAndThreadedTreePrefixes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-resume-selector-sort-" + Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(directory, "parent.jsonl");
        var childPath = Path.Combine(directory, "coding-agent-sessions", "child.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);

        try
        {
            CreateSessionFile(parentPath, provider: "openai", model: "gpt-5.4", name: "Parent session", "parent branch", cwd: directory);
            CreateSessionFile(childPath, provider: "google", model: "gemini-2.5-pro", name: "Child session", "child branch", parentSessionPath: parentPath, cwd: directory);

            File.SetLastWriteTimeUtc(parentPath, new DateTime(2026, 5, 24, 20, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(childPath, new DateTime(2026, 5, 24, 20, 30, 0, DateTimeKind.Utc));

            var component = CodingAgentResumeSelector.CreateComponent(
                new CodingAgentResumeSelectorState(
                    parentPath,
                    directory,
                    CodingAgentTreeSessionStore.ListAvailableSessions(parentPath)));

            Assert.Contains(component.Render(120), line => line.Contains("Sort: threaded", StringComparison.Ordinal));
            Assert.Contains(component.FilteredItems, item => item.Value == childPath && item.Label.StartsWith("└─ ", StringComparison.Ordinal));

            Assert.True(component.HandleInput(CtrlKey(ConsoleKey.S)).Consumed);
            Assert.Contains(component.Render(120), line => line.Contains("Sort: recent", StringComparison.Ordinal));
            Assert.Contains(component.FilteredItems, item => item.Value == childPath && !item.Label.StartsWith("└─ ", StringComparison.Ordinal));

            Assert.True(component.HandleInput(CtrlKey(ConsoleKey.S)).Consumed);
            Assert.Contains(component.Render(120), line => line.Contains("Sort: relevance", StringComparison.Ordinal));

            Assert.True(component.HandleInput(CtrlKey(ConsoleKey.S)).Consumed);
            Assert.Contains(component.Render(120), line => line.Contains("Sort: threaded", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SelectAsync_SearchInputFiltersAndSelectsMatchingSession()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var archivedPath = Path.Combine(Path.GetTempPath(), "tau-archived", Guid.NewGuid().ToString("N"), "review.jsonl");
        var state = new CodingAgentResumeSelectorState(
            currentPath,
            Environment.CurrentDirectory,
            [
                new CodingAgentResumeSessionInfo(
                    currentPath,
                    "Current session",
                    "Current session",
                    "openai",
                    "gpt-5.4",
                    12,
                    new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                    Environment.CurrentDirectory,
                    true),
                new CodingAgentResumeSessionInfo(
                    archivedPath,
                    null,
                    "Review checkpoint",
                    "google",
                    "gemini-2.5-pro",
                    4,
                    new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                    Environment.CurrentDirectory,
                    false)
            ]);
        var keyReader = new ScriptedKeyReader(
            CharKey('r'),
            CharKey('e'),
            CharKey('v'),
            Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 80, height: 20);

        var selected = await CodingAgentResumeSelector.SelectAsync(state, keyReader, surface);

        Assert.Equal(archivedPath, selected.SelectedPath);
        AssertDiffContains(surface.Diffs[0], "Search: ");
        AssertDiffContains(surface.Diffs[1], "Search: r");
        AssertDiffContains(surface.Diffs[2], "Search: re");
        AssertDiffContains(surface.Diffs[3], "Search: rev");
    }

    [Fact]
    public async Task SelectAsync_CtrlRRenamesCurrentSessionAndReturnsUpdatedNameWhenCancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-resume-selector-rename-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        Directory.CreateDirectory(directory);

        try
        {
            var store = new CodingAgentTreeSessionStore(currentPath);
            store.AppendSessionInfo("Current session", "openai", "gpt-5.4");
            store.AppendMessages([new UserMessage("keep this context")], 0);

            var keyReader = new ScriptedKeyReader(
                CtrlKey(ConsoleKey.R),
                CharKey('R'),
                CharKey('e'),
                CharKey('n'),
                CharKey('a'),
                CharKey('m'),
                CharKey('e'),
                CharKey('d'),
                Key(ConsoleKey.Enter),
                Key(ConsoleKey.Escape));
            var surface = new CapturingRenderSurface(width: 80, height: 20);

            var result = await CodingAgentResumeSelector.SelectAsync(
                new CodingAgentResumeSelectorState(
                    currentPath,
                    Environment.CurrentDirectory,
                    CodingAgentTreeSessionStore.ListAvailableSessions(currentPath)),
                keyReader,
                surface);

            Assert.Null(result.SelectedPath);
            Assert.Equal("Current sessionRenamed", result.RenamedCurrentSessionName);
            Assert.Contains(surface.Diffs, diff => diff.Operations.Any(op => op.Text.Contains("Rename Session", StringComparison.Ordinal)));

            var renamed = CodingAgentTreeSessionStore.ListAvailableSessions(currentPath)
                .Single(session => session.FilePath.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Current sessionRenamed", renamed.Name);
            Assert.Equal("Current sessionRenamed", renamed.DisplayName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SelectAsync_CtrlDDeletesNonCurrentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-resume-selector-delete-" + Guid.NewGuid().ToString("N"));
        var currentPath = Path.Combine(directory, "current.jsonl");
        var otherPath = Path.Combine(directory, "coding-agent-sessions", "other.jsonl");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);

        try
        {
            var currentStore = new CodingAgentTreeSessionStore(currentPath);
            currentStore.AppendSessionInfo("Current session", "openai", "gpt-5.4");
            currentStore.AppendMessages([new UserMessage("keep this context")], 0);

            var otherStore = new CodingAgentTreeSessionStore(otherPath);
            otherStore.AppendSessionInfo("Review checkpoint", "google", "gemini-2.5-pro");
            otherStore.AppendMessages([new UserMessage("remove this branch")], 0);

            var keyReader = new ScriptedKeyReader(
                CharKey('r'),
                CharKey('e'),
                CharKey('v'),
                CtrlKey(ConsoleKey.D),
                Key(ConsoleKey.Enter),
                Key(ConsoleKey.Escape));
            var surface = new CapturingRenderSurface(width: 80, height: 20);

            var result = await CodingAgentResumeSelector.SelectAsync(
                new CodingAgentResumeSelectorState(
                    currentPath,
                    Environment.CurrentDirectory,
                    CodingAgentTreeSessionStore.ListAvailableSessions(currentPath)),
                keyReader,
                surface);

            Assert.Null(result.SelectedPath);
            Assert.True(File.Exists(currentPath));
            Assert.False(File.Exists(otherPath));
            Assert.Contains(surface.Diffs, diff => diff.Operations.Any(op =>
                op.Text.Contains("Session deleted", StringComparison.Ordinal) ||
                op.Text.Contains("Session moved to recycle bin", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateComponent_CtrlDOnCurrentSessionShowsBlockedMessage()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var component = CodingAgentResumeSelector.CreateComponent(
            new CodingAgentResumeSelectorState(
                currentPath,
                Environment.CurrentDirectory,
                [
                    new CodingAgentResumeSessionInfo(
                        currentPath,
                        "Current session",
                        "Current session",
                        "openai",
                        "gpt-5.4",
                        12,
                        new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        true)
                ]));

        Assert.True(component.HandleInput(CtrlKey(ConsoleKey.D)).Consumed);

        Assert.Contains("Cannot delete the currently active session", component.Render(80), StringComparer.Ordinal);
        Assert.False(component.TryDequeueDeleteRequest(out _));
    }

    [Fact]
    public void CreateComponent_CtrlNTogglesNamedFilter()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var unnamedPath = Path.Combine(Path.GetTempPath(), "tau-unnamed-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var component = CodingAgentResumeSelector.CreateComponent(
            new CodingAgentResumeSelectorState(
                currentPath,
                Environment.CurrentDirectory,
                [
                    new CodingAgentResumeSessionInfo(
                        currentPath,
                        "Current session",
                        "Current session",
                        "openai",
                        "gpt-5.4",
                        12,
                        new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        true),
                    new CodingAgentResumeSessionInfo(
                        unnamedPath,
                        null,
                        "Preview derived from first user message",
                        "google",
                        "gemini-2.5-pro",
                        4,
                        new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        false)
                ]));

        Assert.True(component.HandleInput(CtrlKey(ConsoleKey.N)).Consumed);
        Assert.Equal([currentPath], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains(component.Render(80), line => line.Contains("Filter: named", StringComparison.Ordinal));

        Assert.True(component.HandleInput(CtrlKey(ConsoleKey.N)).Consumed);
        Assert.Equal(2, component.FilteredItems.Count);
        Assert.Contains(component.Render(80), line => line.Contains("Filter: all", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateComponent_CtrlPTogglesPathVisibility()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var component = CodingAgentResumeSelector.CreateComponent(
            new CodingAgentResumeSelectorState(
                currentPath,
                Environment.CurrentDirectory,
                [
                    new CodingAgentResumeSessionInfo(
                        currentPath,
                        "Current session",
                        "Current session",
                        "openai",
                        "gpt-5.4",
                        12,
                        new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                        Environment.CurrentDirectory,
                        true)
                ]));

        var withPath = component.Render(320);
        Assert.Contains(currentPath, string.Join("\n", withPath), StringComparison.Ordinal);

        Assert.True(component.HandleInput(CtrlKey(ConsoleKey.P)).Consumed);
        var withoutPath = component.Render(320);
        Assert.DoesNotContain(currentPath, string.Join("\n", withoutPath), StringComparison.Ordinal);
        Assert.Contains(withoutPath, line => line.Contains("Path: off", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateComponent_TabTogglesBetweenCurrentAndAllScope()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var otherPath = Path.Combine(Path.GetTempPath(), "tau-other-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var currentCwd = Environment.CurrentDirectory;
        var component = CodingAgentResumeSelector.CreateComponent(
            new CodingAgentResumeSelectorState(
                currentPath,
                currentCwd,
                [
                    new CodingAgentResumeSessionInfo(
                        currentPath,
                        "Current session",
                        "Current session",
                        "openai",
                        "gpt-5.4",
                        12,
                        new DateTimeOffset(2026, 5, 24, 20, 30, 0, TimeSpan.Zero),
                        currentCwd,
                        true),
                    new CodingAgentResumeSessionInfo(
                        otherPath,
                        "Other session",
                        "Other session",
                        "google",
                        "gemini-2.5-pro",
                        4,
                        new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                        Path.Combine(currentCwd, "..", "other"),
                        false)
                ]));

        Assert.Equal([currentPath], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains(component.Render(120), line => line.Contains("Scope: current", StringComparison.Ordinal));

        Assert.True(component.HandleInput(Key(ConsoleKey.Tab)).Consumed);
        Assert.Equal([currentPath, otherPath], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains(component.Render(120), line => line.Contains("Scope: all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectAsync_WhenCurrentScopeEmpty_TabCanSwitchToAllAndSelect()
    {
        var currentPath = Path.Combine(Path.GetTempPath(), "tau-current-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var otherPath = Path.Combine(Path.GetTempPath(), "tau-other-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var currentCwd = Path.Combine(Path.GetTempPath(), "tau-scope-current-" + Guid.NewGuid().ToString("N"));
        var state = new CodingAgentResumeSelectorState(
            currentPath,
            currentCwd,
            [
                new CodingAgentResumeSessionInfo(
                    otherPath,
                    "Other session",
                    "Other session",
                    "google",
                    "gemini-2.5-pro",
                    4,
                    new DateTimeOffset(2026, 5, 24, 19, 45, 0, TimeSpan.Zero),
                    Path.Combine(currentCwd, "..", "other"),
                    false)
            ]);
        var keyReader = new ScriptedKeyReader(
            Key(ConsoleKey.Tab),
            Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 120, height: 20);

        var selected = await CodingAgentResumeSelector.SelectAsync(state, keyReader, surface);

        Assert.Equal(otherPath, selected.SelectedPath);
        AssertDiffContains(surface.Diffs[0], "Scope: current");
        AssertDiffContains(surface.Diffs[1], "Scope: all");
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo CtrlKey(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: true);

    private static ConsoleKeyInfo CharKey(char value) =>
        new(value, (ConsoleKey)char.ToUpperInvariant(value), shift: false, alt: false, control: false);

    private static void AssertDiffContains(TuiRenderDiff diff, string expected) =>
        Assert.Contains(diff.Operations, operation => operation.Text.Contains(expected, StringComparison.Ordinal));

    private static void CreateSessionFile(
        string path,
        string provider,
        string model,
        string name,
        string userText,
        string? parentSessionPath = null,
        string? cwd = null)
    {
        var store = new CodingAgentTreeSessionStore(path, cwd: cwd);
        if (!string.IsNullOrWhiteSpace(parentSessionPath))
        {
            store.RewriteHeader(parentSessionPath);
        }

        store.StartNew(provider, model, name);
        store.AppendMessages([new UserMessage(userText)], 0);
    }

    private sealed class ScriptedKeyReader(params ConsoleKeyInfo[] keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class CapturingRenderSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public List<TuiRenderDiff> Diffs { get; } = [];

        public void Apply(TuiRenderDiff diff) => Diffs.Add(diff);
    }
}
