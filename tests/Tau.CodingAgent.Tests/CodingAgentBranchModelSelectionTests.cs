// 作者：xxx
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentBranchModelSelectionTests
{
    /// <summary>【CodingAgent】【分支恢复】即使投影不再包含响应，也按完整历史和当前虚拟注册确定选择。</summary>
    /// <param name="registered">旧虚拟模型是否仍注册。</param><param name="interveningChange">响应前是否改为物理选择。</param>
    /// <param name="latestChange">响应之后是否又显式选择模型。</param><param name="expected">预期提供方。</param>
    [Theory]
    [InlineData(true, false, false, "router")]
    [InlineData(false, false, false, "physical")]
    [InlineData(true, true, false, "physical")]
    [InlineData(false, false, true, "latest")]
    [InlineData(true, false, true, "latest")]
    public void VirtualBranchSelectionUsesFullHistory(bool registered, bool interveningChange, bool latestChange, string expected)
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        if (registered) catalog.SetVirtualModels([new() { Provider = "router", Id = "auto", Name = "Auto", Api = "pi-virtual" }]);
        var branch = new List<CodingAgentTreeSessionEntry> { new() { Type = "model_change", Provider = "router", Model = "auto" } };
        if (interveningChange) branch.Add(new() { Type = "model_change", Provider = "other", Model = "target" });
        branch.Add(new() { Type = "message", Message = new() { Role = "assistant", Api = "physical-api", Provider = "physical", Model = "target", StopReason = Tau.Ai.StopReason.Error } });
        branch.Add(new() { Type = "compaction", Summary = "A compacted history" });
        if (latestChange) branch.Add(new() { Type = "model_change", Provider = "latest", Model = "selected" });
        var selection = CodingAgentBranchModelSelection.Resolve(new([], "router", "auto", null) { BranchEntries = branch }, catalog);
        Assert.Equal(expected, selection.Provider);
        Assert.Equal(expected == "router" ? "auto" : expected == "latest" ? "selected" : "target", selection.Model);
    }
}
