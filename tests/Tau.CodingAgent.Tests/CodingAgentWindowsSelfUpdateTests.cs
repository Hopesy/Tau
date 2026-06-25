using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentWindowsSelfUpdateTests
{
    [Fact]
    public void GetQuarantineRoot_ReturnsNearestNodeModulesQuarantineDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-self-update-" + Guid.NewGuid().ToString("N"));
        var packageDirectory = Path.Combine(
            directory,
            "node_modules",
            "@scope",
            "pkg");

        var root = CodingAgentWindowsSelfUpdate.GetQuarantineRoot(packageDirectory);

        Assert.Equal(
            Path.Combine(
                directory,
                "node_modules",
                CodingAgentWindowsSelfUpdate.QuarantineDirectoryName),
            root);
    }

    [Fact]
    public void GetQuarantineRoot_ReturnsNullOutsideNodeModules()
    {
        var packageDirectory = Path.Combine(
            Path.GetTempPath(),
            "tau-self-update-" + Guid.NewGuid().ToString("N"),
            "packages",
            "pkg");

        Assert.Null(CodingAgentWindowsSelfUpdate.GetQuarantineRoot(packageDirectory));
    }

    [Fact]
    public void QuarantineNativeDependencies_MovesLoadedFilesAndCopiesThemBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-self-update-" + Guid.NewGuid().ToString("N"));
        var packageDirectory = Path.Combine(directory, "node_modules", "@scope", "pkg");
        var nativeDirectory = Path.Combine(packageDirectory, "native");
        var nativeFile = Path.Combine(nativeDirectory, "addon.node");
        var outsideFile = Path.Combine(directory, "outside.node");
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllText(nativeFile, "native");
        File.WriteAllText(outsideFile, "outside");

        try
        {
            CodingAgentWindowsSelfUpdate.QuarantineNativeDependencies(
                packageDirectory,
                _ => [nativeFile, outsideFile]);

            Assert.True(File.Exists(nativeFile));
            Assert.Equal("native", File.ReadAllText(nativeFile));
            Assert.Equal("outside", File.ReadAllText(outsideFile));

            var quarantineRoot = Path.Combine(directory, "node_modules", CodingAgentWindowsSelfUpdate.QuarantineDirectoryName);
            var quarantined = Assert.Single(Directory.EnumerateFiles(quarantineRoot, "addon.node", SearchOption.AllDirectories));
            Assert.Equal("native", File.ReadAllText(quarantined));
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
    public void CleanupQuarantine_DeletesQuarantineDirectoryAndIgnoresNonNodeModulesPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-self-update-" + Guid.NewGuid().ToString("N"));
        var packageDirectory = Path.Combine(directory, "node_modules", "pkg");
        var quarantineRoot = Path.Combine(directory, "node_modules", CodingAgentWindowsSelfUpdate.QuarantineDirectoryName);
        Directory.CreateDirectory(quarantineRoot);
        File.WriteAllText(Path.Combine(quarantineRoot, "old.node"), "old");

        try
        {
            CodingAgentWindowsSelfUpdate.CleanupQuarantine(packageDirectory);
            CodingAgentWindowsSelfUpdate.CleanupQuarantine(Path.Combine(directory, "not-node-modules", "pkg"));

            Assert.False(Directory.Exists(quarantineRoot));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
