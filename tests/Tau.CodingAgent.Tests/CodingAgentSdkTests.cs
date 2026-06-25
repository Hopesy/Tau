using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentSdkTests
{
    [Fact]
    public async Task CreateSessionAsync_UsesSettingsAndLoadsProjectResources()
    {
        using var temp = TempDirectory.Create();
        var cwd = Path.Combine(temp.Path, "project");
        var agentDir = Path.Combine(temp.Path, "home", ".tau");
        Directory.CreateDirectory(Path.Combine(cwd, ".tau", "prompts"));
        Directory.CreateDirectory(Path.Combine(cwd, ".tau", "skills", "reviewer"));
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(
            Path.Combine(cwd, ".tau", "coding-agent-settings.json"),
            """
            {
              "defaultProvider": "test-provider",
              "defaultModel": "test-model",
              "defaultThinkingLevel": "high",
              "steeringMode": "all",
              "followUpMode": "all"
            }
            """);
        File.WriteAllText(Path.Combine(cwd, ".tau", "prompts", "review.md"), "Review this code.");
        File.WriteAllText(
            Path.Combine(cwd, ".tau", "skills", "reviewer", "SKILL.md"),
            """
            ---
            description: Review source changes
            ---
            Check the diff carefully.
            """);
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "Project rule.");

        var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = cwd,
            AgentDirectory = agentDir,
            ModelCatalog = CreateModelCatalog()
        });

        Assert.Equal("test-provider", session.Runner.Model.Provider);
        Assert.Equal("test-model", session.Runner.Model.Id);
        Assert.Equal(ThinkingLevel.High, session.Runner.ThinkingLevel);
        Assert.Equal(AgentQueueMode.All, session.Runner.SteeringMode);
        Assert.Equal(AgentQueueMode.All, session.Runner.FollowUpMode);
        Assert.Contains(session.PromptTemplateStore.Load(), prompt => prompt.Name == "review");
        Assert.Contains(session.SkillStore.Load(), skill => skill.Name == "reviewer");
        Assert.Contains(session.ContextFileStore.Load(), context => context.Content.Contains("Project rule.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSessionAsync_ContinuesExistingTreeSession()
    {
        using var temp = TempDirectory.Create();
        var cwd = Path.Combine(temp.Path, "project");
        var agentDir = Path.Combine(temp.Path, "home", ".tau");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        var sessionPath = Path.Combine(cwd, ".tau", "coding-agent-session.jsonl");
        var store = new CodingAgentTreeSessionStore(sessionPath, cwd);
        store.AppendModelChange("test-provider", "test-model");
        store.AppendSessionInfo("restored session", "test-provider", "test-model");
        store.AppendMessages([new UserMessage("previous prompt")], 0);

        var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = cwd,
            AgentDirectory = agentDir,
            SessionPath = sessionPath,
            ContinueSession = true,
            ModelCatalog = CreateModelCatalog()
        });

        Assert.Equal("test-provider", session.Runner.Model.Provider);
        Assert.Equal("test-model", session.Runner.Model.Id);
        Assert.Equal("restored session", session.Runner.SessionName);
        var message = Assert.Single(session.Runner.Messages);
        var user = Assert.IsType<UserMessage>(message);
        Assert.Equal("previous prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public async Task CreateSessionAsync_NoToolsBuiltInKeepsCustomTools()
    {
        using var temp = TempDirectory.Create();
        string? capturedPrompt = null;
        var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = temp.Path,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            ProviderId = "test-provider",
            ModelId = "test-model",
            NoTools = CodingAgentSdkNoToolsMode.BuiltIn,
            CustomTools = [new StaticAgentTool("custom_tool")],
            ModelCatalog = CreateModelCatalog(),
            ProviderRegistry = CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt)
        });

        await foreach (var _ in session.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("custom_tool", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAsync_NoToolsAllDisablesBuiltInAndCustomTools()
    {
        using var temp = TempDirectory.Create();
        string? capturedPrompt = null;
        var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = temp.Path,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            ProviderId = "test-provider",
            ModelId = "test-model",
            NoTools = CodingAgentSdkNoToolsMode.All,
            CustomTools = [new StaticAgentTool("custom_tool")],
            ModelCatalog = CreateModelCatalog(),
            ProviderRegistry = CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt)
        });

        await foreach (var _ in session.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain("custom_tool", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_PersistsRunnerMessagesToFlatAndTreeSessions()
    {
        using var temp = TempDirectory.Create();
        var cwd = Path.Combine(temp.Path, "project");
        var sessionPath = Path.Combine(cwd, ".tau", "coding-agent-session.jsonl");
        var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = cwd,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            SessionPath = sessionPath,
            ProviderId = "test-provider",
            ModelId = "test-model",
            ModelCatalog = CreateModelCatalog(),
            ProviderRegistry = CreatePromptCapturingRegistry(_ => { })
        });

        await foreach (var _ in session.RunAsync("hello")) { }
        session.Runner.SessionName = "saved session";
        session.Save();

        Assert.NotNull(session.SessionStore);
        var flat = session.SessionStore!.Load();
        Assert.Equal("saved session", flat.Name);
        Assert.Equal(2, flat.Messages.Count);

        Assert.NotNull(session.TreeSessionController);
        var tree = session.TreeSessionController!.LoadSnapshot();
        Assert.Equal("saved session", tree.Name);
        Assert.Equal(2, tree.Messages.Count);
    }

    private static ProviderRegistry CreatePromptCapturingRegistry(Action<LlmContext> capture)
    {
        var registry = new ProviderRegistry();
        registry.Register("sdk-prompt-capture", () => new PromptCapturingProvider(capture), sourceId: "test");
        return registry;
    }

    private static ModelCatalog CreateModelCatalog()
    {
        var catalog = new ModelCatalog();
        catalog.RegisterModel(new Model
        {
            Provider = "test-provider",
            Id = "test-model",
            Name = "Test Model",
            Api = "sdk-prompt-capture",
            Reasoning = true
        });
        return catalog;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-sdk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class StaticAgentTool : IAgentTool
    {
        public StaticAgentTool(string name)
        {
            Name = name;
            using var schema = JsonDocument.Parse("""{"type":"object"}""");
            ParameterSchema = schema.RootElement.Clone();
        }

        public string Name { get; }
        public string Label => Name;
        public string Description => Name;
        public JsonElement ParameterSchema { get; }

        public Task<ToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct = default,
            Func<ToolUpdate, Task>? onUpdate = null) =>
            Task.FromResult(new ToolResult([new TextContent(Name)]));
    }

    private sealed class PromptCapturingProvider : IStreamProvider
    {
        private readonly Action<LlmContext> _capture;

        public PromptCapturingProvider(Action<LlmContext> capture) => _capture = capture;

        public string Api => "sdk-prompt-capture";

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            CreateStream(context);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            CreateStream(context);

        private AssistantMessageStream CreateStream(LlmContext context)
        {
            _capture(context);
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("ok")])));
            return stream;
        }
    }
}
