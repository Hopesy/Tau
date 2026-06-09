using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.WebUi.Contracts;
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
    public async Task JavaScriptReplTool_ExecutesThroughBrowserBridgeAndReturnsFileDetails()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var bridge = new WebUiJavaScriptReplBridge(TimeSpan.FromSeconds(5));
        var tool = WebUiTools.CreateJavaScriptReplTool(sessionId, bridge);

        Assert.Equal("javascript_repl", tool.Name);
        Assert.Contains("title", tool.ParameterSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("code", tool.ParameterSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("returnDownloadableFile", tool.Description, StringComparison.Ordinal);
        Assert.Contains("createOrUpdateArtifact", tool.Description, StringComparison.Ordinal);

        var executeTask = tool.ExecuteAsync(
            "call-repl",
            Json("""{"title":"Testing","code":"return 1"}"""),
            CancellationToken.None);
        var request = await bridge.WaitForNextAsync(sessionId, TimeSpan.FromSeconds(1));
        Assert.NotNull(request);
        Assert.Equal("call-repl", request!.ToolCallId);
        Assert.Equal("Testing", request.Title);
        Assert.Equal("return 1", request.Code);
        Assert.True(bridge.Complete(
            sessionId,
            request.Id,
            new WebJavaScriptReplResultRequest(
                Output: "=> 1",
                Files:
                [
                    new WebJavaScriptReplFileDto(
                        "result.txt",
                        "text/plain",
                        5,
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello")))
                ])));

        var result = await executeTask;

        Assert.False(result.IsError);
        Assert.Equal("=> 1", ReadText(result));
        var details = Assert.IsType<WebJavaScriptReplToolDetails>(result.Details);
        var file = Assert.Single(details.Files);
        Assert.Equal("result.txt", file.FileName);
        Assert.Equal("text/plain", file.MimeType);
        Assert.Equal(5, file.Size);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello")), file.ContentBase64);
    }

    [Fact]
    public async Task JavaScriptReplTool_ReturnsExplicitErrorWhenNoBrowserCompletesRequest()
    {
        var bridge = new WebUiJavaScriptReplBridge(TimeSpan.FromMilliseconds(50));
        var tool = WebUiTools.CreateJavaScriptReplTool("no-browser", bridge);

        var result = await tool.ExecuteAsync(
            "call-repl",
            Json("""{"title":"Testing","code":"return 1"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timed out waiting for an active WebUi page", ReadText(result), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionTools_RegisterArtifactsAndJavaScriptReplWhenBridgeIsAvailable()
    {
        var store = new WebArtifactStore(Path.Combine(Path.GetTempPath(), $"tau-webui-session-tools-{Guid.NewGuid():N}.json"));
        var service = new WebArtifactService(store);
        var bridge = new WebUiJavaScriptReplBridge(TimeSpan.FromSeconds(5));

        var tools = WebUiTools.CreateSessionTools("session-tools", service, bridge);

        Assert.Contains(tools, candidate => candidate.Name == "artifacts");
        Assert.Contains(tools, candidate => candidate.Name == "javascript_repl");
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

    [Fact]
    public async Task RuntimeRunner_CanExecuteWebUiJavaScriptReplToolThroughBridge()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var store = new WebArtifactStore(Path.Combine(Path.GetTempPath(), $"tau-webui-runtime-repl-artifacts-{Guid.NewGuid():N}.json"));
        var artifacts = new WebArtifactService(store);
        var bridge = new WebUiJavaScriptReplBridge(TimeSpan.FromSeconds(5));
        var registry = new ProviderRegistry();
        var faux = Faux.Register(registry);
        faux.SetResponses([
            Faux.AssistantMessage(
                [
                    Faux.ToolCall(
                        "javascript_repl",
                        new Dictionary<string, object?>
                        {
                            ["title"] = "Calculating sum",
                            ["code"] = "return 2 + 3"
                        },
                        "call-repl")
                ],
                stopReason: StopReason.ToolUse),
            Faux.AssistantMessage("repl done")
        ]);
        var catalog = new ModelCatalog();
        catalog.RegisterModel(faux.GetModel());
        var tools = RuntimeCodingAgentRunner.CreateDefaultTools()
            .Concat(WebUiTools.CreateSessionTools(sessionId, artifacts, bridge))
            .ToArray();
        var runner = RuntimeCodingAgentRunner.Create(
            faux.GetModel().Provider,
            faux.GetModel().Id,
            toolsOverride: tools,
            providerRegistryOverride: registry,
            modelCatalogOverride: catalog);

        var eventsTask = Task.Run(async () =>
        {
            var collected = new List<AgentEvent>();
            await foreach (var evt in runner.RunAsync("run javascript"))
            {
                collected.Add(evt);
            }

            return collected;
        });
        var request = await bridge.WaitForNextAsync(sessionId, TimeSpan.FromSeconds(1));
        Assert.NotNull(request);
        Assert.Equal("call-repl", request!.ToolCallId);
        Assert.Equal("Calculating sum", request.Title);
        Assert.Equal("return 2 + 3", request.Code);
        Assert.True(bridge.Complete(
            sessionId,
            request.Id,
            new WebJavaScriptReplResultRequest(
                Output: "=> 5",
                Files:
                [
                    new WebJavaScriptReplFileDto(
                        "sum.json",
                        "application/json",
                        9,
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"sum":5}""")))
                ])));

        var events = await eventsTask;

        Assert.Contains(events.OfType<ToolExecutionStartEvent>(), evt => evt.ToolName == "javascript_repl");
        var toolEnd = Assert.Single(events.OfType<ToolExecutionEndEvent>(), evt => evt.ToolName == "javascript_repl");
        Assert.False(toolEnd.Result.IsError);
        Assert.Equal("=> 5", ReadText(toolEnd.Result));
        var details = Assert.IsType<WebJavaScriptReplToolDetails>(toolEnd.Result.Details);
        Assert.Equal("sum.json", Assert.Single(details.Files).FileName);
        Assert.Contains(runner.Messages.OfType<ToolResultMessage>(), message => message.ToolCallId == "call-repl");
        Assert.True(runner.ToolResultDetailsByToolCallId.ContainsKey("call-repl"));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadText(Tau.Agent.ToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(static content => content.Text));
}
