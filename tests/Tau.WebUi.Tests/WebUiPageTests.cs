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
        Assert.Contains("window.createArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.updateArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.rewriteArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.createOrUpdateArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.htmlArtifactLogs", html, StringComparison.Ordinal);
        Assert.Contains("window.deleteArtifact", html, StringComparison.Ordinal);
        Assert.Contains("window.attachments", html, StringComparison.Ordinal);
        Assert.Contains("window.listAttachments", html, StringComparison.Ordinal);
        Assert.Contains("window.readTextAttachment", html, StringComparison.Ordinal);
        Assert.Contains("window.readBinaryAttachment", html, StringComparison.Ordinal);
        Assert.Contains("window.returnDownloadableFile", html, StringComparison.Ordinal);
        Assert.Contains("type: 'file-returned'", html, StringComparison.Ordinal);
        Assert.Contains("contentBase64: finalContentBase64", html, StringComparison.Ordinal);
        Assert.Contains("isBase64: finalContentBase64 !== null", html, StringComparison.Ordinal);
        Assert.Contains("action: 'htmlArtifactLogs'", html, StringComparison.Ordinal);
        Assert.Contains("old_str, new_str", html, StringComparison.Ordinal);
        Assert.Contains("function isMarkdownArtifact(artifact)", html, StringComparison.Ordinal);
        Assert.Contains("function isSvgArtifact(artifact)", html, StringComparison.Ordinal);
        Assert.Contains("function isPdfArtifact(artifact)", html, StringComparison.Ordinal);
        Assert.Contains("function isTextArtifact(artifact)", html, StringComparison.Ordinal);
        Assert.Contains("textArtifactExtensions", html, StringComparison.Ordinal);
        Assert.Contains("Markdown preview", html, StringComparison.Ordinal);
        Assert.Contains("SVG preview", html, StringComparison.Ordinal);
        Assert.Contains("PDF preview", html, StringComparison.Ordinal);
        Assert.Contains("Generic file", html, StringComparison.Ordinal);
        Assert.Contains("content: finalContent", html, StringComparison.Ordinal);
        Assert.Contains("/runtime/messages", html, StringComparison.Ordinal);
        Assert.Contains("isKnownArtifactSandbox", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_IncludesJavaScriptReplExecutionHost()
    {
        var html = WebUiPage.Html;

        Assert.Contains("const activeReplSandboxes = new Map()", html, StringComparison.Ordinal);
        Assert.Contains("function replSandboxSrcdoc(sandboxId, code)", html, StringComparison.Ordinal);
        Assert.Contains("function executeJavaScriptRepl(code)", html, StringComparison.Ordinal);
        Assert.Contains("window.executeJavaScriptRepl = executeJavaScriptRepl", html, StringComparison.Ordinal);
        Assert.Contains("iframe.sandbox.add('allow-scripts', 'allow-modals')", html, StringComparison.Ordinal);
        Assert.Contains("activeReplSandboxes.set(sandboxId, context)", html, StringComparison.Ordinal);
        Assert.Contains("handleReplRuntimeMessage(replContext, message, event.source)", html, StringComparison.Ordinal);
        Assert.Contains("message.type === 'execution-complete'", html, StringComparison.Ordinal);
        Assert.Contains("message.type === 'execution-error'", html, StringComparison.Ordinal);
        Assert.Contains("window.__runtimePendingSends", html, StringComparison.Ordinal);
        Assert.Contains("returnValue: context.returnValue", html, StringComparison.Ordinal);
        Assert.Contains("function startJavaScriptReplBridgeLoop(sessionId)", html, StringComparison.Ordinal);
        Assert.Contains("/javascript-repl/next?timeoutMs=25000", html, StringComparison.Ordinal);
        Assert.Contains("/javascript-repl/${encodeURIComponent(requestId)}/result", html, StringComparison.Ordinal);
        Assert.Contains("normalizeReplFileForBridge", html, StringComparison.Ordinal);
        Assert.Contains("sessionId,", html, StringComparison.Ordinal);
    }
}
