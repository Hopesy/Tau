using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebUiToolsTests
{
    [Fact]
    public async Task ArtifactsTool_ExecutesSessionBoundArtifactOperations()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var store = new WebArtifactStore(Path.Combine(Path.GetTempPath(), $"tau-webui-tool-artifacts-{Guid.NewGuid():N}.json"));
        var service = new WebArtifactService(store);
        var tool = new WebUiArtifactsTool(sessionId, service);

        var create = await tool.ExecuteAsync(
            "call-create",
            Json("""{"command":"create","filename":"notes.md","content":"hello world"}"""),
            CancellationToken.None);
        Assert.False(create.IsError);
        Assert.Equal("Created file notes.md", ReadText(create));
        Assert.Contains("command", tool.ParameterSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Artifacts Storage", tool.Description, StringComparison.Ordinal);

        var update = await tool.ExecuteAsync(
            "call-update",
            Json("""{"command":"update","filename":"notes.md","old_str":"world","new_str":"Tau"}"""),
            CancellationToken.None);
        Assert.False(update.IsError);
        Assert.Equal("Updated file notes.md", ReadText(update));

        var get = await tool.ExecuteAsync(
            "call-get",
            Json("""{"command":"get","filename":"notes.md"}"""),
            CancellationToken.None);
        Assert.False(get.IsError);
        Assert.Equal("hello Tau", ReadText(get));

        var duplicate = await tool.ExecuteAsync(
            "call-duplicate",
            Json("""{"command":"create","filename":"notes.md","content":"again"}"""),
            CancellationToken.None);
        Assert.True(duplicate.IsError);
        Assert.Contains("already exists", ReadText(duplicate), StringComparison.Ordinal);

        var delete = await tool.ExecuteAsync(
            "call-delete",
            Json("""{"command":"delete","filename":"notes.md"}"""),
            CancellationToken.None);
        Assert.False(delete.IsError);
        Assert.Equal("Deleted file notes.md", ReadText(delete));
        Assert.Empty(service.ListArtifacts(sessionId));
    }

    [Fact]
    public async Task JavaScriptReplContractTool_ExposesUpstreamSchemaButKeepsExecutionGapExplicit()
    {
        var tool = WebUiTools.CreateJavaScriptReplContractTool();

        Assert.Equal("javascript_repl", tool.Name);
        Assert.Contains("title", tool.ParameterSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("code", tool.ParameterSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("returnDownloadableFile", tool.Description, StringComparison.Ordinal);
        Assert.Contains("createOrUpdateArtifact", tool.Description, StringComparison.Ordinal);

        var result = await tool.ExecuteAsync(
            "call-repl",
            Json("""{"title":"Testing","code":"return 1"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("browser-side window.executeJavaScriptRepl", ReadText(result), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionTools_RegisterArtifactsAndKeepJavaScriptReplExecutionGapUnregistered()
    {
        var store = new WebArtifactStore(Path.Combine(Path.GetTempPath(), $"tau-webui-session-tools-{Guid.NewGuid():N}.json"));
        var service = new WebArtifactService(store);

        var tools = WebUiTools.CreateSessionTools("session-tools", service);

        var tool = Assert.Single(tools);
        Assert.Equal("artifacts", tool.Name);
        Assert.DoesNotContain(tools, candidate => candidate.Name == "javascript_repl");
    }

    [Fact]
    public async Task RuntimeRunner_CanExecuteWebUiArtifactsTool()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var store = new WebArtifactStore(Path.Combine(Path.GetTempPath(), $"tau-webui-runtime-artifacts-{Guid.NewGuid():N}.json"));
        var artifacts = new WebArtifactService(store);
        var registry = new ProviderRegistry();
        var faux = Faux.Register(registry);
        faux.SetResponses([
            Faux.AssistantMessage(
                [
                    Faux.ToolCall(
                        "artifacts",
                        new Dictionary<string, object?>
                        {
                            ["command"] = "create",
                            ["filename"] = "runtime.md",
                            ["content"] = "created by tool"
                        },
                        "call-artifact")
                ],
                stopReason: StopReason.ToolUse),
            Faux.AssistantMessage("artifact done")
        ]);
        var catalog = new ModelCatalog();
        catalog.RegisterModel(faux.GetModel());
        var tools = RuntimeCodingAgentRunner.CreateDefaultTools()
            .Concat(WebUiTools.CreateSessionTools(sessionId, artifacts))
            .ToArray();
        var runner = RuntimeCodingAgentRunner.Create(
            faux.GetModel().Provider,
            faux.GetModel().Id,
            toolsOverride: tools,
            providerRegistryOverride: registry,
            modelCatalogOverride: catalog);

        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("create a runtime artifact"))
        {
            events.Add(evt);
        }

        Assert.Contains(events.OfType<ToolExecutionStartEvent>(), evt => evt.ToolName == "artifacts");
        var toolEnd = Assert.Single(events.OfType<ToolExecutionEndEvent>(), evt => evt.ToolName == "artifacts");
        Assert.False(toolEnd.Result.IsError);
        var artifact = artifacts.GetArtifact(sessionId, "runtime.md");
        Assert.NotNull(artifact);
        Assert.Equal("created by tool", artifact!.Content);
        Assert.Contains(runner.Messages.OfType<ToolResultMessage>(), message => message.ToolCallId == "call-artifact");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadText(Tau.Agent.ToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(static content => content.Text));
}
