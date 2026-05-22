namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentExtensionResourceState
{
    private CodingAgentExtensionResources _resources;

    public CodingAgentExtensionResourceState(CodingAgentExtensionResources? resources = null)
    {
        _resources = resources ?? new CodingAgentExtensionResources([], [], []);
    }

    public IReadOnlyList<string> SkillPaths => _resources.SkillPaths;
    public IReadOnlyList<string> PromptPaths => _resources.PromptPaths;
    public IReadOnlyList<string> ThemePaths => _resources.ThemePaths;

    public void Update(CodingAgentExtensionResources resources)
    {
        _resources = resources;
    }
}
