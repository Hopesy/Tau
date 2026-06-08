using Tau.WebUi.Ui;

namespace Tau.WebUi.Tests;

public sealed class WebUiPageTests
{
    [Fact]
    public void Html_IncludesCodingAgentJsonlImportStrategyAuditNotice()
    {
        var html = WebUiPage.Html;

        Assert.Contains("id=\"import-current-branch-only\"", html, StringComparison.Ordinal);
        Assert.Contains("Current branch only", html, StringComparison.Ordinal);
        Assert.Contains("function codingAgentJsonlImportQuery(currentBranchOnly)", html, StringComparison.Ordinal);
        Assert.Contains("return `?currentBranchOnly=${currentBranchOnly ? 'true' : 'false'}`;", html, StringComparison.Ordinal);
        Assert.Contains("previewCodingAgentJsonlImport(text, currentBranchOnly)", html, StringComparison.Ordinal);
        Assert.Contains("importCodingAgentJsonlSession(text, preview, currentBranchOnly)", html, StringComparison.Ordinal);
        Assert.Contains("currentBranchOnly=", html, StringComparison.Ordinal);
        Assert.Contains("strategy=", html, StringComparison.Ordinal);
        Assert.Contains("timelineOnly=", html, StringComparison.Ordinal);
        Assert.Contains("branchTreePersisted=", html, StringComparison.Ordinal);
        Assert.Contains("branch tree not persisted", html, StringComparison.Ordinal);
        Assert.Contains("warningCodes=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_IncludesArtifactRuntimeBridge()
    {
        var html = WebUiPage.Html;

        Assert.Contains("id=\"artifacts-pane\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"refresh-artifacts\"", html, StringComparison.Ordinal);
        Assert.Contains("window.listArtifacts", html, StringComparison.Ordinal);
        Assert.Contains("window.getArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.createOrUpdateArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.deleteArtifact", html, StringComparison.Ordinal);
        Assert.Contains("/runtime/messages", html, StringComparison.Ordinal);
        Assert.Contains("isKnownArtifactSandbox", html, StringComparison.Ordinal);
    }
}
