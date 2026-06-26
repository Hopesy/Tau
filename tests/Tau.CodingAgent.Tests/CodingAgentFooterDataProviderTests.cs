using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentFooterDataProviderTests
{
    [Fact]
    public void GetGitBranch_ReadsBranchFromRegularRepository()
    {
        using var temp = TempDirectory.Create();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main");

        using var provider = new CodingAgentFooterDataProvider(repo);

        Assert.Equal("main", provider.GetGitBranch());
    }

    [Fact]
    public void GetGitBranch_WalksUpFromNestedDirectory()
    {
        using var temp = TempDirectory.Create();
        var nested = Path.Combine(temp.Path, "repo", "src", "app");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(temp.Path, "repo", ".git"));
        File.WriteAllText(Path.Combine(temp.Path, "repo", ".git", "HEAD"), "ref: refs/heads/feature/nested");

        using var provider = new CodingAgentFooterDataProvider(nested);

        Assert.Equal("feature/nested", provider.GetGitBranch());
    }

    [Fact]
    public void GetGitBranch_ReturnsDetachedForDetachedHead()
    {
        using var temp = TempDirectory.Create();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567");

        using var provider = new CodingAgentFooterDataProvider(repo);

        Assert.Equal("detached", provider.GetGitBranch());
    }

    [Fact]
    public void GetGitBranch_ReadsBranchFromWorktreeGitFile()
    {
        using var temp = TempDirectory.Create();
        var worktree = Path.Combine(temp.Path, "work");
        var gitRoot = Path.Combine(temp.Path, "git");
        var worktreeGitDir = Path.Combine(gitRoot, "worktrees", "feature");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../git/worktrees/feature");
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/feature/foo");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

        using var provider = new CodingAgentFooterDataProvider(worktree);

        Assert.Equal("feature/foo", provider.GetGitBranch());
    }

    [Fact]
    public void SetCwd_SwitchesRepositoryAndNotifiesSubscriber()
    {
        using var temp = TempDirectory.Create();
        var first = CreateRepo(temp.Path, "first", "main");
        var second = CreateRepo(temp.Path, "second", "release");
        using var provider = new CodingAgentFooterDataProvider(first);
        var calls = 0;
        provider.OnBranchChange(() => calls++);

        provider.SetCwd(second);

        Assert.Equal(1, calls);
        Assert.Equal("release", provider.GetGitBranch());
    }

    [Fact]
    public void OnBranchChange_UnsubscribeStopsCallback()
    {
        using var temp = TempDirectory.Create();
        var first = CreateRepo(temp.Path, "first", "main");
        var second = CreateRepo(temp.Path, "second", "release");
        using var provider = new CodingAgentFooterDataProvider(first);
        var calls = 0;
        using (var subscription = provider.OnBranchChange(() => calls++))
        {
            subscription.Dispose();
        }

        provider.SetCwd(second);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void RefreshGitBranch_NotifiesOnlyWhenBranchChanges()
    {
        using var temp = TempDirectory.Create();
        var repo = CreateRepo(temp.Path, "repo", "main");
        var headPath = Path.Combine(repo, ".git", "HEAD");
        using var provider = new CodingAgentFooterDataProvider(repo);
        Assert.Equal("main", provider.GetGitBranch());
        var calls = 0;
        provider.OnBranchChange(() => calls++);

        provider.RefreshGitBranch();
        File.WriteAllText(headPath, "ref: refs/heads/next");
        provider.RefreshGitBranch();

        Assert.Equal(1, calls);
        Assert.Equal("next", provider.GetGitBranch());
    }

    [Fact]
    public async Task GitHeadWatcher_NotifiesWhenHeadIsAtomicallyReplaced()
    {
        using var temp = TempDirectory.Create();
        var repo = CreateRepo(temp.Path, "repo", "main");
        var headPath = Path.Combine(repo, ".git", "HEAD");
        using var provider = new CodingAgentFooterDataProvider(repo);
        Assert.Equal("main", provider.GetGitBranch());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnBranchChange(() => changed.TrySetResult());

        var tempHead = Path.Combine(repo, ".git", "HEAD.lock");
        await File.WriteAllTextAsync(tempHead, "ref: refs/heads/renamed");
        File.Move(tempHead, headPath, overwrite: true);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("renamed", provider.GetGitBranch());
    }

    [Fact]
    public void FindGitPaths_ExposesReftablePathsFromCommonGitDir()
    {
        using var temp = TempDirectory.Create();
        var worktree = Path.Combine(temp.Path, "work");
        var gitRoot = Path.Combine(temp.Path, "git");
        var worktreeGitDir = Path.Combine(gitRoot, "worktrees", "feature");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../git/worktrees/feature");
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/feature/reftable");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

        var paths = CodingAgentFooterDataProvider.FindGitPaths(worktree);

        Assert.NotNull(paths);
        Assert.Equal(Path.Combine(gitRoot, "reftable"), paths!.ReftableDir);
        Assert.Equal(Path.Combine(gitRoot, "reftable", "tables.list"), paths.ReftableTablesListPath);
    }

    [Fact]
    public void ShouldPollGitHead_OnlyForWslWindowsMounts()
    {
        var wslEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WSL_DISTRO_NAME"] = "Ubuntu"
        };
        var linuxEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        Assert.True(CodingAgentFooterDataProvider.ShouldPollGitHead(
            "/mnt/c/projects/repo",
            wslEnvironment,
            PlatformID.Unix));
        Assert.False(CodingAgentFooterDataProvider.ShouldPollGitHead(
            "/home/user/repo",
            wslEnvironment,
            PlatformID.Unix));
        Assert.False(CodingAgentFooterDataProvider.ShouldPollGitHead(
            "/mnt/c/projects/repo",
            linuxEnvironment,
            PlatformID.Unix));
        Assert.False(CodingAgentFooterDataProvider.ShouldPollGitHead(
            "C:\\projects\\repo",
            wslEnvironment,
            PlatformID.Win32NT));
    }

    [Fact]
    public void ExtensionStatuses_AreMutableSnapshotAndCanBeCleared()
    {
        using var temp = TempDirectory.Create();
        using var provider = new CodingAgentFooterDataProvider(temp.Path);

        provider.SetExtensionStatus("build", "running");
        provider.SetExtensionStatus("lint", "queued");
        var snapshot = provider.GetExtensionStatuses();
        provider.SetExtensionStatus("build", null);
        provider.ClearExtensionStatuses();

        Assert.Equal("running", snapshot["build"]);
        Assert.Equal("queued", snapshot["lint"]);
        Assert.Empty(provider.GetExtensionStatuses());
    }

    [Fact]
    public void CustomFooterLines_AreMutableSnapshotAndCanBeCleared()
    {
        using var temp = TempDirectory.Create();
        using var provider = new CodingAgentFooterDataProvider(temp.Path);

        provider.SetCustomFooterLines(["alpha", "beta"]);
        var snapshot = provider.GetCustomFooterLines();
        provider.SetCustomFooterLines(null);

        Assert.Equal(["alpha", "beta"], snapshot);
        Assert.Null(provider.GetCustomFooterLines());
    }

    [Fact]
    public void AvailableProviderCount_ClampsNegativeValues()
    {
        using var temp = TempDirectory.Create();
        using var provider = new CodingAgentFooterDataProvider(temp.Path);

        provider.SetAvailableProviderCount(3);
        Assert.Equal(3, provider.GetAvailableProviderCount());

        provider.SetAvailableProviderCount(-1);
        Assert.Equal(0, provider.GetAvailableProviderCount());
    }

    private static string CreateRepo(string parent, string name, string branch)
    {
        var repo = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), $"ref: refs/heads/{branch}");
        return repo;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-footer-data-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
