using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【来源目录】SDK、内置、自动发现及包扩展来源完整，动态注册继承包来源且不能覆盖 SDK 工具。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CreateSessionAsync_PreservesSourcesAcrossDynamicToolRegistration()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, "agent");
        var userExtensions = Path.Combine(agentDir, "extensions");
        var projectExtensions = Path.Combine(temp.Path, ".tau", "extensions");
        var packageRoot = Path.Combine(temp.Path, "pkg");
        Directory.CreateDirectory(userExtensions); Directory.CreateDirectory(projectExtensions); Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(userExtensions, "user.js"), "export default pi=>pi.registerTool({name:'user_tool',parameters:{type:'object'},execute:async()=>({content:[]})});");
        File.WriteAllText(Path.Combine(projectExtensions, "project.js"), "export default pi=>pi.registerTool({name:'project_tool',parameters:{type:'object'},execute:async()=>({content:[]})});");
        var file = Path.Combine(packageRoot, "index.js");
        File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{"pi":{"extensions":["index.js"]}}""");
        File.WriteAllText(file, """
            export default pi=>{
              const tool=name=>({name,description:'package',parameters:{type:'object'},execute:async()=>({content:[]})});
              pi.registerTool(tool('pack'));
              pi.registerCommand('sources',{handler:()=>{
                pi.registerTool(tool('late')); pi.registerTool(tool('custom_tool'));
                pi.sendMessage({customType:'sources',content:JSON.stringify(pi.getAllTools())},{triggerTurn:false});
              }});
            };
            """);
        var manager = new CodingAgentPackageManager(temp.Path, Path.Combine(agentDir, "coding-agent-settings.json"), Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));
        manager.AddSource("./pkg", local: true);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agentDir, NoSession = true, ModelCatalog = CreateModelCatalog(),
            CustomTools = [new StaticAgentTool("custom_tool")]
        });
        await foreach (var ignored in session.RunAsync("/sources")) { }
        var message = Assert.Single(session.Messages.OfType<AgentCustomMessage>());
        using var json = JsonDocument.Parse(Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ContentBlock>>(message.Content))).Text);
        var tools = json.RootElement.EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        foreach (var name in new[] { "pack", "late" })
        {
            var source = tools[name].GetProperty("sourceInfo");
            Assert.Equal(file, source.GetProperty("path").GetString());
            Assert.Equal("./pkg", source.GetProperty("source").GetString());
            Assert.Equal("project", source.GetProperty("scope").GetString());
            Assert.Equal("package", source.GetProperty("origin").GetString());
            Assert.Equal(packageRoot, source.GetProperty("baseDir").GetString());
        }
        foreach (var scope in new[] { "user", "project" })
        {
            var source = tools[scope + "_tool"].GetProperty("sourceInfo");
            Assert.Equal("auto", source.GetProperty("source").GetString());
            Assert.Equal(scope, source.GetProperty("scope").GetString());
            Assert.Equal("top-level", source.GetProperty("origin").GetString());
        }
        Assert.Contains(tools.Values, tool => tool.GetProperty("sourceInfo").GetProperty("source").GetString() == "builtin");
        var custom = tools["custom_tool"].GetProperty("sourceInfo");
        Assert.Equal("<sdk:custom_tool>", custom.GetProperty("path").GetString());
        Assert.Equal("sdk", custom.GetProperty("source").GetString());
        Assert.Equal("temporary", custom.GetProperty("scope").GetString());
        Assert.IsType<StaticAgentTool>(Assert.Single(session.Runner.GetRegisteredTools(), tool => tool.Name == "custom_tool"));
    }

    /// <summary>【CodingAgent】【SDK替换】新建会话和从内存恢复文件后，SDK 保存接口跟随当前控制器与兼容快照路径。</summary>
    /// <param name="memory">是否从内存会话恢复已有文件。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSessionAsync_ReplacementFollowsActualStorage(bool memory)
    {
        using var temp = TempDirectory.Create();
        var extensions = Path.Combine(temp.Path, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "replace.js"), """
            export default pi=>pi.registerCommand('replace',{handler:async(path,ctx)=>{
              const options={withSession:async fresh=>{await fresh.sendUserMessage('replacement message');}};
              if(path)await ctx.switchSession(path,options);else await ctx.newSession(options);
            }});
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, ".tau"), NoSession = memory,
            ProviderId = "test-provider", ModelId = "test-model", NoTools = CodingAgentSdkNoToolsMode.All,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(_ => { })
        });
        await foreach (var ignored in session.RunAsync("original message")) { }
        session.Save();
        var original = session.TreeSessionController?.Path;
        var originalFlat = session.SessionStore?.Path;
        var target = new CodingAgentTreeSessionStore(Path.Combine(temp.Path, "resume.jsonl"), temp.Path);
        if (memory) target.AppendMessages([new UserMessage("resumed message")], 0);
        var failures = new List<CodingAgentExtensionErrorEvent>();
        await foreach (var evt in session.RunAsync(memory ? "/replace " + target.Path : "/replace"))
            if (evt is CodingAgentExtensionErrorEvent failure) failures.Add(failure);
        Assert.Empty(failures);
        session.Save();
        var actual = session.TreeSessionController;
        Assert.NotNull(actual);
        Assert.NotEqual(original, actual.Path);
        Assert.Contains("replacement message", File.ReadAllText(actual.Path));
        Assert.DoesNotContain("original message", File.ReadAllText(actual.Path));
        if (memory) Assert.Equal(target.Path, actual.Path);
        else
        {
            Assert.Equal(Path.ChangeExtension(actual.Path, ".json"), session.SessionStore!.Path);
            Assert.NotEqual(originalFlat, session.SessionStore.Path);
            Assert.Contains("original message", File.ReadAllText(original!));
            Assert.DoesNotContain("replacement message", File.ReadAllText(originalFlat!));
            Assert.Contains("replacement message", File.ReadAllText(session.SessionStore.Path));
        }
    }

    /// <summary>【CodingAgent】【SDK范围】创建选项中的模型范围实际进入运行器，并且不依赖 settings 文件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CreateSessionAsync_AppliesExplicitScopedModels()
    {
        using var temp = TempDirectory.Create();
        var scoped = new Model { Provider = "custom-scope", Id = "scoped", Name = "Scoped", Api = "test" };
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, ".tau"), NoSession = true,
            ModelCatalog = CreateModelCatalog(), IncludeExtensions = false,
            ScopedModels = [new(scoped, "medium")]
        });
        var actual = Assert.Single(session.Runner.GetScopedModels());
        Assert.Equal(scoped, actual.Model);
        Assert.Equal("medium", actual.ThinkingLevel);
    }

    /// <summary>【CodingAgent】【SDK命令】实际 SDK 创建流程绑定命令，不把原始命令文本发送给模型。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CreateSessionAsync_RoutesExtensionCommandsWithImages()
    {
        using var temp = TempDirectory.Create();
        var extensions = Path.Combine(temp.Path, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "command.js"), """
            export default pi=>pi.registerCommand('photo',{handler:args=>pi.sendUserMessage([
              {type:'text',text:args},{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}
            ])});
            """);
        LlmContext? captured = null;
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, ".tau"), NoSession = true,
            ProviderId = "test-provider", ModelId = "test-model", NoTools = CodingAgentSdkNoToolsMode.All,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(context => captured = context)
        });
        Assert.Equal("photo", Assert.Single(session.ExtensionStatus.Commands).InvocationName);
        await foreach (var _ in session.RunAsync("/photo inspect")) { }
        Assert.NotNull(captured);
        var user = Assert.Single(captured.Value.Messages.OfType<UserMessage>());
        Assert.Equal("inspect", Assert.Single(user.Content.OfType<TextContent>()).Text);
        Assert.Equal("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", Assert.Single(user.Content.OfType<ImageContent>()).Data);
    }

    /// <summary>【CodingAgent】【SDK输入】真实 SDK 先运行扩展输入转换，再按会话目录加载和展开模板。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CreateSessionAsync_ExpandsTemplateAfterInputHook()
    {
        using var temp = TempDirectory.Create();
        var extensionPath = Path.Combine(temp.Path, ".tau", "extensions", "input");
        var promptPath = Path.Combine(temp.Path, ".tau", "prompts");
        Directory.CreateDirectory(extensionPath);
        Directory.CreateDirectory(promptPath);
        File.WriteAllText(Path.Combine(extensionPath, "index.js"), """
            export default pi => pi.on('input',e=>{
              if(e.text !== 'original') throw Error('wrong input');
              return {action:'transform',text:'/greet Ada'};
            });
            """);
        File.WriteAllText(Path.Combine(promptPath, "greet.md"), "Hello $1");
        LlmContext? captured = null;
        using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, ".tau"), NoSession = true,
            ProviderId = "test-provider", ModelId = "test-model", NoTools = CodingAgentSdkNoToolsMode.All,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(context => captured = context)
        });
        await foreach (var _ in session.RunAsync("original")) { }
        Assert.NotNull(captured);
        Assert.Equal("Hello Ada", Assert.IsType<TextContent>(Assert.Single(Assert.Single(captured.Value.Messages.OfType<UserMessage>()).Content)).Text);
    }

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

        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
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

    /// <summary>SDK 恢复旧树会话时补齐系统基线并保留原用户消息。</summary>
    /// <returns>测试任务。</returns>
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

        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
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
        Assert.IsType<SystemMessage>(session.Runner.Messages[0]);
        var message = Assert.Single(session.Runner.Messages.OfType<UserMessage>());
        var user = Assert.IsType<UserMessage>(message);
        Assert.Equal("previous prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public async Task CreateSessionAsync_NoToolsBuiltInKeepsCustomTools()
    {
        using var temp = TempDirectory.Create();
        string? capturedPrompt = null;
        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = temp.Path,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            ProviderId = "test-provider",
            ModelId = "test-model",
            NoTools = CodingAgentSdkNoToolsMode.BuiltIn,
            CustomTools = [new StaticAgentTool("custom_tool")],
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(),
            ProviderRegistry = CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt)
        });

        await foreach (var _ in session.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Equal("custom_tool", Assert.Single(Transcript.GetCurrentTools(session.Runner.Messages)).Name);
        Assert.DoesNotContain("- custom_tool:", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAsync_NoToolsAllDisablesBuiltInAndCustomTools()
    {
        using var temp = TempDirectory.Create();
        string? capturedPrompt = null;
        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = temp.Path,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            ProviderId = "test-provider",
            ModelId = "test-model",
            NoTools = CodingAgentSdkNoToolsMode.All,
            CustomTools = [new StaticAgentTool("custom_tool")],
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(),
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
        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = cwd,
            AgentDirectory = Path.Combine(temp.Path, ".tau"),
            SessionPath = sessionPath,
            ProviderId = "test-provider",
            ModelId = "test-model",
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(),
            ProviderRegistry = CreatePromptCapturingRegistry(_ => { })
        });

        await foreach (var _ in session.RunAsync("hello")) { }
        session.Runner.SessionName = "saved session";
        session.Save();

        Assert.NotNull(session.SessionStore);
        var flat = session.SessionStore!.Load();
        Assert.Equal("saved session", flat.Name);
        Assert.Equal(3, flat.Messages.Count);
        Assert.NotEmpty(Assert.IsType<SystemMessage>(flat.Messages[0]).ToolsAdded!);

        Assert.NotNull(session.TreeSessionController);
        var tree = session.TreeSessionController!.LoadSnapshot();
        Assert.Equal("saved session", tree.Name);
        Assert.Equal(3, tree.Messages.Count);
        Assert.NotEmpty(Assert.IsType<SystemMessage>(tree.Messages[0]).ToolsAdded!);
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
