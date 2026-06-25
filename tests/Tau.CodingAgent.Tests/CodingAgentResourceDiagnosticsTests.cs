using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentResourceDiagnosticsTests
{
    [Fact]
    public void FromExtension_NormalizesSeverityAndPath()
    {
        var diagnostic = CodingAgentResourceDiagnostics.FromExtension(
            new CodingAgentExtensionDiagnostic("error", "failed to load extension", " ext.ts ", "project"));

        Assert.Equal(CodingAgentResourceDiagnosticTypes.Error, diagnostic.Type);
        Assert.Equal("failed to load extension", diagnostic.Message);
        Assert.Equal("ext.ts", diagnostic.Path);
        Assert.Null(diagnostic.Collision);
    }

    [Fact]
    public void FromPackage_MapsSourceToPath()
    {
        var resources = new CodingAgentPackageResources(
            [],
            [],
            [],
            [],
            [new CodingAgentPackageDiagnostic("warning", "package not installed", "npm:@scope/pkg", "user")]);

        var diagnostic = Assert.Single(resources.ResourceDiagnostics);

        Assert.Equal(CodingAgentResourceDiagnosticTypes.Warning, diagnostic.Type);
        Assert.Equal("package not installed", diagnostic.Message);
        Assert.Equal("npm:@scope/pkg", diagnostic.Path);
    }

    [Fact]
    public void Collision_CreatesStructuredResourceCollision()
    {
        var diagnostic = CodingAgentResourceDiagnostics.Collision(
            CodingAgentResourceTypes.Skill,
            "review",
            "project/SKILL.md",
            "package/SKILL.md",
            "project",
            "npm:pkg");

        Assert.Equal(CodingAgentResourceDiagnosticTypes.Collision, diagnostic.Type);
        Assert.NotNull(diagnostic.Collision);
        Assert.Equal(CodingAgentResourceTypes.Skill, diagnostic.Collision.ResourceType);
        Assert.Equal("review", diagnostic.Collision.Name);
        Assert.Equal("project/SKILL.md", diagnostic.Collision.WinnerPath);
        Assert.Equal("package/SKILL.md", diagnostic.Collision.LoserPath);
        Assert.Equal("project", diagnostic.Collision.WinnerSource);
        Assert.Equal("npm:pkg", diagnostic.Collision.LoserSource);
    }
}
