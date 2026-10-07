// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Serialization;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【会话重放回归】工具与系统指令增量应在统一请求入口保持一致。</summary>
public sealed class TranscriptTests
{
    /// <summary>【AI】【会话基线】折叠时保留段落、时间和工具快照，空声明与无声明分别处理。</summary>
    [Fact]
    public void GetCurrentSystemMessage_PreservesStructuredBaselineAndCopiesTools()
    {
        var variants = new Dictionary<string, string> { ["regex"] = "old" };
        var tool = Tool("current") with { ConstrainedSampling = new() { Type = "grammar", Variants = variants } };
        ChatMessage[] messages =
        [
            new SystemMessage("first") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(123), Sections = new Dictionary<string, string?> { ["a"] = "old", ["b"] = "removed" }, ToolsAdded = [Tool("old")] },
            new UserMessage("user"),
            new SystemMessage("later") { Sections = new Dictionary<string, string?> { ["a"] = "new", ["b"] = null, ["empty"] = "" }, ToolsRemoved = [new("old")], ToolsAdded = [tool] }
        ];
        var baseline = Transcript.GetCurrentSystemMessage(messages)!;
        Assert.Equal("first\n\nlater", baseline.Content);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(123), baseline.Timestamp);
        Assert.Equal(new[] { "a", "empty" }, baseline.Sections!.Keys);
        Assert.Equal("new", baseline.Sections["a"]);
        Assert.Null(baseline.ToolsRemoved);
        variants["regex"] = "changed";
        Assert.Equal("old", Assert.Single(baseline.ToolsAdded!).ConstrainedSampling!.Variants!["regex"]);
        Assert.Equal("first\n\nlater\n\nnew", Transcript.GetSystemMessageText(baseline));
        var collapsed = Transcript.ResolveTranscript(new(null, messages, null), false);
        Assert.Equal(new[] { "a", "empty" }, Assert.IsType<SystemMessage>(collapsed.Messages[0]).Sections!.Keys);
        Assert.Null(Transcript.GetCurrentSystemMessage([new UserMessage("no system")]));
        Assert.NotNull(Transcript.GetCurrentSystemMessage([new SystemMessage("")]));
    }

    /// <summary>规范化重复执行不重复声明，空基线不会改变后续系统消息的位置。</summary>
    [Fact]
    public void NormalizeContext_IsIdempotentAndDoesNotCreateEmptyHead()
    {
        var original = new LlmContext("base", [new UserMessage("user"), new SystemMessage("later")], [Tool("a")]);
        var normalized = Transcript.NormalizeContext(original);
        var repeated = Transcript.NormalizeContext(normalized);
        Assert.Same(normalized.Messages, repeated.Messages);
        Assert.Null(repeated.SystemPrompt);
        Assert.Null(repeated.Tools);
        Assert.Equal(3, repeated.Messages.Count);
        Assert.Equal("a", Assert.Single(Assert.IsType<SystemMessage>(repeated.Messages[0]).ToolsAdded!).Name);
        var empty = Transcript.NormalizeContext(new("", original.Messages, []));
        Assert.Same(original.Messages, empty.Messages);
    }

    /// <summary>移除优先于新增，名称区分大小写，删除后重加排在末尾。</summary>
    [Fact]
    public void GetCurrentTools_ReplaysChangesInOrder()
    {
        var first = Tool("a");
        var changed = Tool("a") with { Description = "changed" };
        ChatMessage[] messages =
        [
            new SystemMessage("") { ToolsAdded = [first, Tool("b"), Tool("A")] },
            new UserMessage("user"),
            new SystemMessage("") { ToolsRemoved = [new("a")], ToolsAdded = [changed] }
        ];
        var tools = Transcript.GetCurrentTools(messages);
        Assert.Equal(new[] { "b", "A", "a" }, tools.Select(tool => tool.Name));
        Assert.Same(changed, tools.Last());
        Assert.Equal("description", first.Description);
        var delta = Transcript.GetToolStateChanges([Tool("a"), Tool("b")], [changed, Tool("c")]);
        Assert.Equal(new[] { "a", "b" }, delta.ToolsRemoved.Select(tool => tool.Name));
        Assert.Equal(new[] { "a", "c" }, delta.ToolsAdded.Select(tool => tool.Name));
    }

    /// <summary>格式化空白不产生重新声明，纯定义快照隔离可变约束配置。</summary>
    [Fact]
    public void Declaration_ClonesSchemaAndConstraintConfiguration()
    {
        var variants = new Dictionary<string, string> { ["openai_regex"] = "a" };
        var tool = Tool("a") with { ConstrainedSampling = new() { Type = "grammar", Variants = variants } };
        var snapshot = Transcript.ToToolDeclaration(tool);
        Assert.True(Transcript.DeclarationsEqual(tool, snapshot));
        variants["openai_regex"] = "b";
        Assert.Equal("a", snapshot.ConstrainedSampling!.Variants!["openai_regex"]);
        Assert.False(Transcript.DeclarationsEqual(tool, snapshot));
        using var formatted = JsonDocument.Parse("{ \"type\" : \"object\" }");
        Assert.True(Transcript.DeclarationsEqual(Tool("a"), new Tool("a", "description", formatted.RootElement)));
    }

    /// <summary>命名段落可以替换、删除及重新插入，旧顶层字段作为最早基线。</summary>
    [Fact]
    public void ResolveContext_ReplaysPromptSectionsAndOverridesLegacyTools()
    {
        var user = new UserMessage("user");
        ChatMessage[] messages =
        [
            new SystemMessage("first") { Sections = new Dictionary<string, string?> { ["a"] = "old", ["b"] = "remove" }, ToolsAdded = [Tool("a")] },
            user,
            new SystemMessage("later") { Sections = new Dictionary<string, string?> { ["a"] = "new", ["b"] = null }, ToolsRemoved = [new("legacy")] },
            new SystemMessage("") { Sections = new Dictionary<string, string?> { ["b"] = "last" } }
        ];
        var result = Transcript.ResolveContext(new("base", messages, [Tool("legacy")]));
        Assert.Equal("base\n\nfirst\n\nlater\n\nnew\n\nlast", result.SystemPrompt);
        Assert.Same(user, Assert.Single(result.Messages));
        Assert.Equal("a", Assert.Single(result.Tools!).Name);
        Assert.Equal(4, messages.Length);
        Assert.Equal("old", ((SystemMessage)messages[0]).Sections!["a"]);
    }

    /// <summary>没有系统消息时完全保留旧上下文对象与工具列表。</summary>
    [Fact]
    public void ResolveContext_LeavesLegacyContextUntouched()
    {
        var context = new LlmContext("base", [new UserMessage("user")], [Tool("a")]);
        var result = Transcript.ResolveContext(context);
        Assert.Same(context.Messages, result.Messages);
        Assert.Same(context.Tools, result.Tools);
        Assert.Equal(context.SystemPrompt, result.SystemPrompt);
    }

    /// <summary>RPC 与扩展输出使用 parameters 和毫秒时间戳，保留删除段落的 null。</summary>
    [Fact]
    public void SystemMessageJson_WritesWireFieldsWithoutRuntimeMembers()
    {
        var message = new SystemMessage("instruction")
        {
            Timestamp = DateTimeOffset.UnixEpoch, ToolsAdded = [Tool("a")], ToolsRemoved = [new("old")],
            Sections = new Dictionary<string, string?> { ["removed"] = null }
        };
        var json = SystemMessageJson.ToElement(message);
        Assert.Equal("system", json.GetProperty("role").GetString());
        Assert.Equal("instruction", json.GetProperty("content").GetString());
        Assert.Equal(0, json.GetProperty("timestamp").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sections").GetProperty("removed").ValueKind);
        var declaration = json.GetProperty("toolsAdded")[0];
        Assert.Equal("object", declaration.GetProperty("parameters").GetProperty("type").GetString());
        Assert.False(declaration.TryGetProperty("parameterSchema", out _));
        Assert.False(declaration.TryGetProperty("execute", out _));
        var wireRestored = SystemMessageJson.Read(json);
        Assert.Equal(message.Timestamp, wireRestored.Timestamp);
        Assert.Null(wireRestored.Sections!["removed"]);
        Assert.Equal("old", Assert.Single(wireRestored.ToolsRemoved!).Name);
        Assert.True(Transcript.DeclarationsEqual(Assert.Single(message.ToolsAdded!), Assert.Single(wireRestored.ToolsAdded!)));
        var stored = JsonSerializer.Serialize(message, TauAiJsonContext.Default.SystemMessage);
        var restored = JsonSerializer.Deserialize(stored, TauAiJsonContext.Default.SystemMessage)!;
        Assert.Equal(message.Content, restored.Content);
        Assert.True(Transcript.DeclarationsEqual(Assert.Single(message.ToolsAdded!), Assert.Single(restored.ToolsAdded!)));
    }

    /// <summary>统一 Models 和旧 StreamFunctions 的普通/简化入口均重放声明。</summary>
    /// <param name="unified">是否使用统一 Models。</param>
    /// <param name="simple">是否使用简化流。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Streaming_ResolvesTranscriptAtPublicEntryPoints(bool unified, bool simple)
    {
        var provider = new CaptureProvider();
        var model = new Model { Id = "model", Name = "Model", Api = provider.Api, Provider = "test" };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var context = new LlmContext("base", [new SystemMessage("extra") { ToolsAdded = [Tool("a")] }, new UserMessage("user")], null);
        var models = new Models([new ProviderDefinition("test", provider, [model])]);
        var stream = (unified, simple) switch
        {
            (true, true) => models.StreamSimple(model, context, new() { ApiKey = "key" }),
            (true, false) => models.Stream(model, context, new() { ApiKey = "key" }),
            (false, true) => StreamFunctions.StreamSimple(registry, model, context, new() { ApiKey = "key" }),
            _ => StreamFunctions.Stream(registry, model, context, new() { ApiKey = "key" })
        };
        await stream.ResultAsync;
        Assert.Equal("base\n\nextra", provider.Context.SystemPrompt);
        Assert.IsType<UserMessage>(Assert.Single(provider.Context.Messages));
        Assert.Equal("a", Assert.Single(provider.Context.Tools!).Name);
    }

    /// <summary>创建测试声明。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>独立 schema 的声明。</returns>
    private static Tool Tool(string name)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(name, "description", schema.RootElement.Clone());
    }

    private sealed class CaptureProvider : IStreamProvider
    {
        public string Api => "transcript-capture";
        public LlmContext Context { get; private set; }
        /// <summary>记录实际请求上下文。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>完成的模拟流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Context = context;
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("done")])));
            return stream;
        }
        /// <summary>复用标准模拟流。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>完成的模拟流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
