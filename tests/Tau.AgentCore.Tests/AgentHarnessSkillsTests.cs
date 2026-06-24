using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessSkillsTests
{
    [Fact]
    public void FormatSkillsForSystemPrompt_ExcludesDisabledSkillsAndEscapesXml()
    {
        var visible = new AgentHarnessSkill(
            "review",
            "Use <review> & \"quote\" 'apostrophe'",
            "body",
            "C:/skills/review/SKILL.md");
        var hidden = new AgentHarnessSkill(
            "hidden",
            "Hidden",
            "body",
            "C:/skills/hidden/SKILL.md",
            DisableModelInvocation: true);

        var prompt = AgentHarnessSkills.FormatSkillsForSystemPrompt([visible, hidden]);

        Assert.Contains("The following skills provide specialized instructions for specific tasks.", prompt, StringComparison.Ordinal);
        Assert.Contains("<name>review</name>", prompt, StringComparison.Ordinal);
        Assert.Contains("Use &lt;review&gt; &amp; &quot;quote&quot; &apos;apostrophe&apos;", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSkillInvocation_WrapsSkillContentAndAdditionalInstructions()
    {
        var skill = new AgentHarnessSkill(
            "review",
            "Review code",
            "Read references/file.md",
            Path.Combine("C:", "skills", "review", "SKILL.md"));

        var invocation = AgentHarnessSkills.FormatSkillInvocation(skill, "Focus tests.");

        Assert.Contains("""<skill name="review" location=""", invocation, StringComparison.Ordinal);
        Assert.Contains("References are relative to", invocation, StringComparison.Ordinal);
        Assert.Contains("Read references/file.md", invocation, StringComparison.Ordinal);
        Assert.EndsWith("Focus tests.", invocation, StringComparison.Ordinal);
    }
}
