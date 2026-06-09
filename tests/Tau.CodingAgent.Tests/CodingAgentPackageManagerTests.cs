using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentPackageManagerTests
{
    [Fact]
    public void AddListAndRemoveSource_UsesUserAndProjectScopes()
    {
        using var temp = TempDirectory.Create();
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));

        var addedUser = manager.AddSource("npm:@foo/bar", local: false);
        var addedProject = manager.AddSource("./local-package", local: true);
        var duplicateUser = manager.AddSource("npm:@foo/bar", local: false);

        Assert.True(addedUser);
        Assert.True(addedProject);
        Assert.False(duplicateUser);
        Assert.Collection(
            manager.ListConfiguredPackages(),
            package =>
            {
                Assert.Equal("npm:@foo/bar", package.Source);
                Assert.Equal("user", package.Scope);
                Assert.Null(package.InstalledPath);
            },
            package =>
            {
                Assert.Equal("./local-package", package.Source);
                Assert.Equal("project", package.Scope);
            });

        Assert.True(manager.RemoveSource("npm:@foo/bar", local: false));
        Assert.False(manager.RemoveSource("npm:@foo/bar", local: false));
        Assert.Single(manager.ListConfiguredPackages());
    }

    [Fact]
    public void ResolveResources_LoadsLocalPackageManifestPaths()
    {
        using var temp = TempDirectory.Create();
        var packageRoot = Path.Combine(temp.Path, "pkg");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(Path.Combine(packageRoot, "ext"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "skills"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "prompts"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "themes"));
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            """
            {
              "pi": {
                "extensions": ["ext"],
                "skills": ["skills"],
                "prompts": ["prompts"],
                "themes": ["themes"]
              }
            }
            """);

        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));
        manager.AddSource("./pkg", local: true);

        var resources = manager.ResolveResources();

        Assert.Empty(resources.Diagnostics);
        Assert.Equal(Path.Combine(packageRoot, "ext"), Assert.Single(resources.ExtensionPaths));
        Assert.Equal(Path.Combine(packageRoot, "skills"), Assert.Single(resources.SkillPaths));
        Assert.Equal(Path.Combine(packageRoot, "prompts"), Assert.Single(resources.PromptPaths));
        Assert.Equal(Path.Combine(packageRoot, "themes"), Assert.Single(resources.ThemePaths));
    }

    [Fact]
    public void ResolveResources_LoadsConventionDirectoriesWhenManifestIsMissing()
    {
        using var temp = TempDirectory.Create();
        var packageRoot = Path.Combine(temp.Path, "pkg");
        Directory.CreateDirectory(Path.Combine(packageRoot, "skills"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "prompts"));

        var settingsPath = Path.Combine(temp.Path, ".tau", "coding-agent-settings.json");
        new CodingAgentSettingsStore(settingsPath).Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            Packages: [new CodingAgentPackageSource("./pkg")]));
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: settingsPath);

        var resources = manager.ResolveResources();

        Assert.Empty(resources.Diagnostics);
        Assert.Empty(resources.ExtensionPaths);
        Assert.Equal(Path.Combine(packageRoot, "skills"), Assert.Single(resources.SkillPaths));
        Assert.Equal(Path.Combine(packageRoot, "prompts"), Assert.Single(resources.PromptPaths));
        Assert.Empty(resources.ThemePaths);
    }

    [Fact]
    public void InstallAndPersist_ProjectNpmPackageRunsPrefixInstallAndStoresSource()
    {
        using var temp = TempDirectory.Create();
        var runner = new FakePackageCommandRunner();
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"),
            commandRunner: runner);

        manager.InstallAndPersist("npm:@foo/bar@1.2.3", local: true);

        var command = Assert.Single(runner.Commands);
        var installRoot = Path.Combine(temp.Path, ".tau", "npm");
        Assert.Equal("npm", command.FileName);
        Assert.Equal(temp.Path, command.WorkingDirectory);
        Assert.Equal(["install", "@foo/bar@1.2.3", "--prefix", installRoot], command.Arguments);
        Assert.True(File.Exists(Path.Combine(installRoot, ".gitignore")));
        Assert.Contains("tau-extensions", File.ReadAllText(Path.Combine(installRoot, "package.json")), StringComparison.Ordinal);

        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, ".tau", "coding-agent-settings.json")).Load();
        Assert.Equal("npm:@foo/bar@1.2.3", Assert.Single(settings.Packages!).Source);
    }

    [Fact]
    public void InstallAndPersist_GlobalNpmPackageUsesConfiguredNpmCommand()
    {
        using var temp = TempDirectory.Create();
        var runner = new FakePackageCommandRunner();
        var userSettingsPath = Path.Combine(temp.Path, "user-settings.json");
        new CodingAgentSettingsStore(userSettingsPath).Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            NpmCommand: ["mise", "exec", "node@20", "--", "npm"]));
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: userSettingsPath,
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"),
            commandRunner: runner);

        manager.InstallAndPersist("npm:@foo/bar", local: false);

        var command = Assert.Single(runner.Commands);
        Assert.Equal("mise", command.FileName);
        Assert.Equal(["exec", "node@20", "--", "npm", "install", "-g", "@foo/bar"], command.Arguments);
        var settings = new CodingAgentSettingsStore(userSettingsPath).Load();
        Assert.Equal("npm:@foo/bar", Assert.Single(settings.Packages!).Source);
    }

    [Fact]
    public void GitPackageInstallUpdateAndRemove_RunExpectedCommandsAndDeleteOnlyInstallPath()
    {
        using var temp = TempDirectory.Create();
        var runner = new FakePackageCommandRunner();
        var projectSettingsPath = Path.Combine(temp.Path, ".tau", "coding-agent-settings.json");
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: projectSettingsPath,
            commandRunner: runner);

        manager.InstallAndPersist("git:github.com/example/pkg@main", local: true);

        var targetDir = Path.Combine(temp.Path, ".tau", "git", "github.com", "example", "pkg");
        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal("git", command.FileName);
                Assert.Equal(["clone", "https://github.com/example/pkg", targetDir], command.Arguments);
                Assert.Null(command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal("git", command.FileName);
                Assert.Equal(["checkout", "main"], command.Arguments);
                Assert.Equal(targetDir, command.WorkingDirectory);
            });

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "package.json"), "{}");
        runner.Commands.Clear();
        new CodingAgentSettingsStore(projectSettingsPath).Save(new CodingAgentSettingsSnapshot(
            null,
            null,
            Packages: [new CodingAgentPackageSource("git:github.com/example/pkg")]));

        manager.Update("git:github.com/example/pkg");

        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal("git", command.FileName);
                Assert.Equal(["pull", "--ff-only"], command.Arguments);
                Assert.Equal(targetDir, command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal("npm", command.FileName);
                Assert.Equal(["install", "--omit=dev"], command.Arguments);
                Assert.Equal(targetDir, command.WorkingDirectory);
            });

        runner.Commands.Clear();
        Assert.True(manager.RemoveAndPersist("git:github.com/example/pkg", local: true));

        Assert.False(Directory.Exists(targetDir));
        Assert.Null(new CodingAgentSettingsStore(projectSettingsPath).Load().Packages);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Update_SkipsConfiguredPackagesWhenPiOfflineIsEnabled()
    {
        using var temp = TempDirectory.Create();
        var previous = Environment.GetEnvironmentVariable("PI_OFFLINE");
        try
        {
            Environment.SetEnvironmentVariable("PI_OFFLINE", "1");
            var runner = new FakePackageCommandRunner();
            var manager = new CodingAgentPackageManager(
                cwd: temp.Path,
                userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
                projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"),
                commandRunner: runner);
            Assert.True(manager.AddSource("npm:@foo/bar", local: false));

            manager.Update();

            Assert.Empty(runner.Commands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_OFFLINE", previous);
        }
    }

    [Fact]
    public void PackageCli_CommandFailureReturnsErrorAndDoesNotPersistSource()
    {
        using var temp = TempDirectory.Create();
        var runner = new FakePackageCommandRunner();
        runner.Results.Enqueue(new CodingAgentPackageCommandResult(1, StandardError: "registry failed"));
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"),
            commandRunner: runner);
        var output = new StringWriter();
        var error = new StringWriter();

        var handled = CodingAgentPackageCli.TryHandle(
            ["install", "npm:@foo/bar"],
            output,
            error,
            manager,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Package command failed: npm exited with code 1", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Empty(manager.ListConfiguredPackages());
    }

    [Fact]
    public void PackageCli_InstallsListsAndRemovesProjectPackage()
    {
        using var temp = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "pkg"));
        var manager = new CodingAgentPackageManager(
            cwd: temp.Path,
            userSettingsPath: Path.Combine(temp.Path, "user-settings.json"),
            projectSettingsPath: Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));
        var output = new StringWriter();
        var error = new StringWriter();

        var handledInstall = CodingAgentPackageCli.TryHandle(
            ["install", "./pkg", "--local"],
            output,
            error,
            manager,
            out var installExitCode);
        var handledList = CodingAgentPackageCli.TryHandle(
            ["list"],
            output,
            error,
            manager,
            out var listExitCode);
        var handledRemove = CodingAgentPackageCli.TryHandle(
            ["uninstall", "./pkg", "-l"],
            output,
            error,
            manager,
            out var removeExitCode);

        Assert.True(handledInstall);
        Assert.True(handledList);
        Assert.True(handledRemove);
        Assert.Equal(0, installExitCode);
        Assert.Equal(0, listExitCode);
        Assert.Equal(0, removeExitCode);
        Assert.Contains("Installed ./pkg", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Project packages:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Removed ./pkg", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(manager.ListConfiguredPackages());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ExtensionStore_LoadsAdditionalPackageExtensionPaths()
    {
        using var temp = TempDirectory.Create();
        var extensions = Path.Combine(temp.Path, "pkg", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(
            Path.Combine(extensions, "hello.json"),
            """
            {
              "name": "hello",
              "description": "Hello package command",
              "response": "hello"
            }
            """);

        var store = new CodingAgentExtensionCommandStore(
            cwd: temp.Path,
            userExtensionsDirectory: Path.Combine(temp.Path, "missing-user-extensions"),
            explicitPaths: [],
            additionalPathsProvider: () => [extensions]);

        var command = Assert.Single(store.Load());
        Assert.Equal("hello", command.Name);
        Assert.Equal("path", command.Scope);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-package-manager-{Guid.NewGuid():N}");
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

    private sealed class FakePackageCommandRunner : ICodingAgentPackageCommandRunner
    {
        public List<CodingAgentPackageCommand> Commands { get; } = [];

        public Queue<CodingAgentPackageCommandResult> Results { get; } = [];

        public CodingAgentPackageCommandResult Run(CodingAgentPackageCommand command)
        {
            Commands.Add(command);
            return Results.Count == 0 ? new CodingAgentPackageCommandResult(0) : Results.Dequeue();
        }
    }
}
