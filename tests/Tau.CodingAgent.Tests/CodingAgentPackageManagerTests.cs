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
    public void PackageCli_InstallsListsAndRemovesProjectPackage()
    {
        using var temp = TempDirectory.Create();
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
}
