// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic 协议回归】验证 OAuth 双向工具绑定、跨轮 effort 和配置入口。</summary>
public sealed class AnthropicOAuthEffortTests
{
    private const string OAuthKey = "sk-ant-oat-test-only";
    private const string Identity = "You are Claude Code, Anthropic's official CLI for Claude.";
    private const string Success = """
        event: message_start
        data: {"type":"message_start","message":{"id":"r","usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"answer"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>公开入口均识别 OAuth，添加身份并使用 Bearer 和规范工具名。</summary>
    /// <param name="entry">公开入口名称。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("direct-simple")]
    [InlineData("stream")]
    [InlineData("simple")]
    [InlineData("models")]
    [InlineData("models-simple")]
    public async Task OAuth_AllEntriesUseIdentityAndCanonicalNames(string entry)
    {
        var capture = await SendAsync(Context(Tool("read")), new SimpleStreamOptions { ApiKey = OAuthKey, CacheRetention = CacheRetention.Short }, entry: entry);
        Assert.Equal("Bearer " + OAuthKey, capture.Headers["Authorization"]);
        Assert.False(capture.Headers.ContainsKey("x-api-key"));
        Assert.Equal("claude-cli/2.1.280", capture.Headers["User-Agent"]);
        Assert.Equal("cli", capture.Headers["x-app"]);
        Assert.Equal("application/json", capture.Headers["Accept"]);
        Assert.Equal("true", capture.Headers["anthropic-dangerous-direct-browser-access"]);
        Assert.Equal("claude-code-20250219,oauth-2025-04-20", capture.Headers["anthropic-beta"]);
        Assert.Equal(Identity, capture.Body!.Value.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal("base", capture.Body.Value.GetProperty("system")[1].GetProperty("text").GetString());
        Assert.All(capture.Body.Value.GetProperty("system").EnumerateArray(), block => Assert.True(block.TryGetProperty("cache_control", out _)));
        Assert.Equal("Read", capture.Body.Value.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    /// <summary>没有用户系统提示时也保留 OAuth 身份，缓存 none/short/long 分别生效。</summary>
    /// <param name="cache">缓存策略。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(CacheRetention.None)]
    [InlineData(CacheRetention.Short)]
    [InlineData(CacheRetention.Long)]
    public async Task OAuth_IdentityWithoutPromptRespectsCache(CacheRetention cache)
    {
        var capture = await SendAsync(new(null, [new UserMessage("hi")], null), new AnthropicOptions { ApiKey = OAuthKey, CacheRetention = cache });
        var block = Assert.Single(capture.Body!.Value.GetProperty("system").EnumerateArray());
        Assert.Equal(Identity, block.GetProperty("text").GetString());
        Assert.Equal(cache != CacheRetention.None, block.TryGetProperty("cache_control", out var control));
        if (cache == CacheRetention.Long) Assert.Equal("1h", control.GetProperty("ttl").GetString());
    }

    /// <summary>三个 Anthropic 环境认证入口都能识别 OAuth，使用模拟值而不访问真实凭据。</summary>
    /// <param name="variable">环境变量名称。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_OAUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    public async Task OAuth_EnvironmentCredentialsUseSameProtocol(string variable)
    {
        var capture = await SendAsync(Context(), new AnthropicOptions { Env = new Dictionary<string, string> { [variable] = OAuthKey } });
        Assert.Equal("Bearer " + OAuthKey, capture.Headers["Authorization"]);
        Assert.Equal(Identity, capture.Body!.Value.GetProperty("system")[0].GetProperty("text").GetString());
    }

    /// <summary>普通密钥和 Copilot 不启用 Claude Code 身份及名称映射。</summary>
    /// <param name="copilot">是否使用 Copilot。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_DoesNotAffectOtherAuthModes(bool copilot)
    {
        var capture = await SendAsync(Context(Tool("read")), new AnthropicOptions { ApiKey = copilot ? OAuthKey : "plain-key" }, Model() with { Provider = copilot ? "github-copilot" : "test" });
        Assert.False(capture.Headers.ContainsKey("x-app"));
        Assert.False(capture.Headers.ContainsKey("anthropic-beta"));
        Assert.Equal("base", Assert.Single(capture.Body!.Value.GetProperty("system").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("read", capture.Body.Value.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal(copilot, capture.Headers.ContainsKey("Authorization"));
    }

    /// <summary>规范名称覆盖上游完整工具列表，自定义名称和用户定义保持原样。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task OAuth_MapsAllCanonicalNamesWithoutMutatingTools()
    {
        string[] names = ["Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode", "KillShell", "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch"];
        var tools = names.Select(name => Tool(name.ToLowerInvariant())).Append(Tool("customTool")).ToArray();
        var capture = await SendAsync(new(null, [new UserMessage("hi")], tools), new AnthropicOptions { ApiKey = OAuthKey });
        Assert.Equal(names.Append("customTool"), capture.Body!.Value.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal("read", tools[0].Name);
    }

    /// <summary>历史调用、工具移除和内联新增使用同一映射，响应按当前活动定义恢复本地名称。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task OAuth_RoundTripsChangedToolsAndSseNames()
    {
        var history = new AssistantMessage([new ToolCallContent("call", "read", "{}")]);
        var context = new LlmContext(null,
        [
            new SystemMessage("base") { ToolsAdded = [Tool("read")] }, new UserMessage("hi"), history,
            new SystemMessage("") { ToolsRemoved = [new("read")], ToolsAdded = [Tool("rEaD") with { ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } }] },
            new ToolResultMessage("call", [new TextContent("done")])
        ], null);
        var capture = await SendAsync(context, new AnthropicOptions { ApiKey = OAuthKey }, Model() with
        { Compat = new() { SupportsMidConvoSystemMessages = true, SupportsMidConvoToolChanges = true, SupportsStrictTools = true } }, sse: ToolResponse("Read"));
        var body = capture.Body!.Value;
        Assert.Equal("Read", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("name").GetString());
        Assert.Equal("tool_result", body.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("type").GetString());
        var update = body.GetProperty("messages")[3].GetProperty("content");
        Assert.Equal("Read", update[0].GetProperty("tool").GetProperty("name").GetString());
        Assert.Equal("Read", update[1].GetProperty("tool").GetProperty("definition").GetProperty("name").GetString());
        Assert.True(update[1].GetProperty("tool").GetProperty("definition").GetProperty("strict").GetBoolean());
        Assert.Equal("rEaD", Assert.Single(capture.Result.Content.OfType<ToolCallContent>()).Name);
        Assert.All(capture.Events.OfType<ToolCallStartEvent>(), item => Assert.Equal("rEaD", item.Partial.Content.OfType<ToolCallContent>().Last().Name));
        Assert.Equal("rEaD", Assert.Single(capture.Events.OfType<ToolCallEndEvent>()).ToolCall!.Name);
        Assert.Equal("read", Assert.Single(history.Content.OfType<ToolCallContent>()).Name);
    }

    /// <summary>响应名称恢复不限制内置列表；无匹配和非 OAuth 保持服务端原名。</summary>
    /// <param name="name">本地声明名称。</param>
    /// <param name="responseName">响应名称。</param>
    /// <param name="oauth">是否 OAuth。</param>
    /// <param name="expected">预期恢复名称。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("customTool", "CUSTOMTOOL", true, "customTool")]
    [InlineData("read", "Unknown", true, "Unknown")]
    [InlineData("read", "Read", false, "Read")]
    public async Task OAuth_RestoresOnlyMatchingActiveNames(string name, string responseName, bool oauth, string expected)
    {
        var capture = await SendAsync(Context(Tool(name)), new AnthropicOptions { ApiKey = oauth ? OAuthKey : "key" }, sse: ToolResponse(responseName));
        Assert.Equal(expected, Assert.Single(capture.Result.Content.OfType<ToolCallContent>()).Name);
    }

    /// <summary>调用方请求头优先于身份默认值，显式 beta 会覆盖、去重或清除自动特性。</summary>
    /// <param name="features">请求级 beta 值。</param>
    /// <param name="expected">最终 beta 值。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(" first,second, first, ", "first,second")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task OAuth_HeaderOverridesRemainAuthoritative(string? features, string? expected)
    {
        var capture = await SendAsync(Context(), new AnthropicOptions { ApiKey = OAuthKey, Headers = new Dictionary<string, string> { ["ANTHROPIC-BETA"] = features!, ["User-Agent"] = "custom-client", ["x-app"] = "custom-app", ["Authorization"] = "Bearer custom-test" } },
            Model() with { Headers = new Dictionary<string, string> { ["anthropic-beta"] = "model-beta", ["x-app"] = "model-app" }, Compat = new() { SupportsMidConvoEffort = true } });
        Assert.Equal(expected, capture.Headers.GetValueOrDefault("anthropic-beta"));
        Assert.Equal("custom-client", capture.Headers["User-Agent"]);
        Assert.Equal("custom-app", capture.Headers["x-app"]);
        Assert.Equal("Bearer custom-test", capture.Headers["Authorization"]);
    }

    /// <summary>原生会话等级保留所有 effort，缺省 high；即使禁用常规思考仍启用绑定控制。</summary>
    /// <param name="effort">本轮等级。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Effort_ManagedModeUsesStableTopLevelAndCurrentMarker(string? effort)
    {
        var capture = await SendAsync(Context(), new AnthropicOptions { Effort = effort, ThinkingEnabled = false, Temperature = 0.5f, ThinkingDisplay = "omitted" }, Managed() with { Reasoning = false });
        var body = capture.Body!.Value;
        Assert.Equal("high", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(effort ?? "high", body.GetProperty("messages").EnumerateArray().Last().GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("omitted", body.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("drop_block", body.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.Equal(effort ?? "high", capture.Result.ProviderThinkingLevel);
        Assert.Contains("mid-conversation-output-config-2026-07-01", capture.Headers["anthropic-beta"]);
        Assert.Contains("thinking-binding-controls-2026-08-01", capture.Headers["anthropic-beta"]);
        Assert.Equal(effort ?? "high", Assert.Single(capture.Events.OfType<StartEvent>()).Partial.ProviderThinkingLevel);
    }

    /// <summary>把 SSE 响应追加为历史后，下一轮精确恢复原等级前缀并追加新等级。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Effort_ResponseReplaysHistoricalPrefix()
    {
        var first = await SendAsync(Context(), new AnthropicOptions { Effort = "low", CacheRetention = CacheRetention.None }, Managed());
        var restored = first.Result;
        var second = await SendAsync(new(null, [new SystemMessage("base"), new UserMessage("hi"), restored, new UserMessage("next")], null), new AnthropicOptions { Effort = "max", CacheRetention = CacheRetention.None }, Managed());
        var before = first.Body!.Value.GetProperty("messages").EnumerateArray().ToArray();
        var after = second.Body!.Value.GetProperty("messages").EnumerateArray().ToArray();
        Assert.All(Enumerable.Range(0, before.Length), index => Assert.True(JsonElement.DeepEquals(before[index], after[index])));
        Assert.Equal("max", after[^1].GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("low", restored.ProviderThinkingLevel);
    }

    /// <summary>等级标记只恢复同 API、同供应商且有有效内容的历史，不依赖历史模型名称。</summary>
    /// <param name="api">历史 API。</param>
    /// <param name="provider">历史供应商。</param>
    /// <param name="effort">历史等级。</param>
    /// <param name="empty">历史内容是否为空。</param>
    /// <param name="expectedCount">等级消息数量。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("anthropic-messages", "test", "low", false, 2)]
    [InlineData("anthropic-messages", "other", "low", false, 1)]
    [InlineData("openai-responses", "test", "low", false, 1)]
    [InlineData(null, "test", "low", false, 1)]
    [InlineData("anthropic-messages", "test", null, false, 1)]
    [InlineData("anthropic-messages", "test", "HIGH", false, 1)]
    [InlineData("anthropic-messages", "test", "off", false, 1)]
    [InlineData("anthropic-messages", "test", "low", true, 1)]
    public async Task Effort_HistoryIsGatedBySourceAndContent(string? api, string provider, string? effort, bool empty, int expectedCount)
    {
        var assistant = new AssistantMessage([new TextContent(empty ? "  " : "answer")]) { Api = api, Provider = provider, Model = "older-model", ProviderThinkingLevel = effort };
        var capture = await SendAsync(new(null, [new UserMessage("hi"), assistant, new UserMessage("next")], null), model: Managed());
        Assert.Equal(expectedCount, capture.Body!.Value.GetProperty("messages").EnumerateArray().Count(message => message.TryGetProperty("output_config", out _)));
        Assert.Equal(!empty, capture.Body.Value.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("role").GetString() == "assistant"));
    }

    /// <summary>系统更新、工具调用结果与 effort 保持顺序，缓存放在内容块上，脱敏签名正常重放。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Effort_PreservesToolPairingAndCachePlacement()
    {
        var assistant = new AssistantMessage([new ThinkingContent("") { Redacted = true, ThinkingSignature = "opaque" }, new ToolCallContent("c", "read", "{}")]) { Api = "anthropic-messages", Provider = "test", Model = Managed().Id, ProviderThinkingLevel = "low" };
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [Tool("read")] }, new UserMessage("hi"), assistant,
            new SystemMessage("update"), new ToolResultMessage("c", [new TextContent("done")]),
            new AssistantMessage([new TextContent("second")]) { Api = "anthropic-messages", Provider = "test", ProviderThinkingLevel = "medium" }, new UserMessage("next")], null);
        var capture = await SendAsync(context, new AnthropicOptions { Effort = "max", CacheRetention = CacheRetention.Short }, Managed() with { Compat = Managed().Compat! with { SupportsMidConvoSystemMessages = true } });
        var messages = capture.Body!.Value.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "user", "system", "assistant", "user", "system", "system", "assistant", "user", "system" }, messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("redacted_thinking", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("opaque", messages[2].GetProperty("content")[0].GetProperty("data").GetString());
        Assert.Equal("tool_result", messages[3].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("update", messages[4].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("medium", messages[5].GetProperty("output_config").GetProperty("effort").GetString());
        Assert.True(messages[7].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.Empty(messages[^1].GetProperty("content").EnumerateArray());
    }

    /// <summary>未启用中途能力时 effort 仍只放在顶层，不记录原生历史等级。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Effort_UnsupportedModeUsesTopLevelOnly()
    {
        var capture = await SendAsync(Context(), new AnthropicOptions { Effort = "low", ThinkingEnabled = true }, Model() with { Compat = new() { ForceAdaptiveThinking = true } });
        Assert.Equal("low", capture.Body!.Value.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Single(capture.Body.Value.GetProperty("messages").EnumerateArray());
        Assert.False(capture.Body.Value.GetProperty("thinking").TryGetProperty("block_binding", out _));
        Assert.Null(capture.Result.ProviderThinkingLevel);
    }

    /// <summary>模型原生映射优先于旧兼容映射，直接与配置分发入口返回一致等级。</summary>
    /// <param name="entry">请求入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct-simple")]
    [InlineData("simple")]
    [InlineData("models-simple")]
    public async Task Effort_SimpleEntryUsesModelMap(string entry)
    {
        var model = Managed() with { ThinkingLevelMap = new Dictionary<string, string?> { ["xhigh"] = "max" }, Compat = Managed().Compat! with { ReasoningEffortMap = new Dictionary<string, string> { ["xhigh"] = "low" } } };
        var capture = await SendAsync(Context(), new SimpleStreamOptions { ApiKey = "key", Reasoning = ThinkingLevel.ExtraHigh }, model, entry);
        Assert.Equal("max", capture.Result.ProviderThinkingLevel);
        Assert.Equal("max", capture.Body!.Value.GetProperty("messages").EnumerateArray().Last().GetProperty("output_config").GetProperty("effort").GetString());
    }

    /// <summary>普通简化入口缺省和 Off 关闭思考，off=null 的模型省略 disabled。</summary>
    /// <param name="level">简化入口等级。</param>
    /// <param name="offSupported">是否允许关闭思考。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null, true)]
    [InlineData(ThinkingLevel.Off, true)]
    [InlineData(ThinkingLevel.Off, false)]
    public async Task Effort_SimpleOffDoesNotEnableThinking(ThinkingLevel? level, bool offSupported)
    {
        var capture = await SendAsync(Context(), new SimpleStreamOptions { Reasoning = level, Temperature = 0.5f }, Model() with { Compat = new() { ForceAdaptiveThinking = true }, ThinkingLevelMap = offSupported ? null : new Dictionary<string, string?> { ["off"] = null } }, "direct-simple");
        Assert.Equal(offSupported, capture.Body!.Value.TryGetProperty("thinking", out var thinking));
        if (offSupported) Assert.Equal("disabled", thinking.GetProperty("type").GetString());
        Assert.False(capture.Body.Value.TryGetProperty("output_config", out _));
        Assert.True(capture.Body.Value.TryGetProperty("temperature", out _));
        Assert.False(capture.Headers.ContainsKey("anthropic-beta"));
    }

    /// <summary>已知模型名称不能替代显式自适应能力；旧式思考默认添加交错 beta，显式 false 可关闭。</summary>
    /// <param name="interleaved">交错思考开关。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Effort_AdaptiveRequiresCapabilityAndLegacyBetaUsesThinkingGate(bool? interleaved)
    {
        var capture = await SendAsync(Context(), new AnthropicOptions { ThinkingEnabled = true, ThinkingBudgetTokens = 0, InterleavedThinking = interleaved }, Model() with { Id = "claude-opus-4-7" });
        Assert.Equal("enabled", capture.Body!.Value.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(1024, capture.Body.Value.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal(interleaved != false, capture.Headers.ContainsKey("anthropic-beta"));
        var disabled = await SendAsync(Context(), new AnthropicOptions { ThinkingEnabled = false, InterleavedThinking = true });
        Assert.False(disabled.Headers.ContainsKey("anthropic-beta"));
    }

    /// <summary>请求前回调失败和预取消仍返回本轮 effort，且不发送 HTTP。</summary>
    /// <param name="cancel">是否预先取消。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Effort_FailuresRetainProviderLevel(bool cancel)
    {
        var options = new AnthropicOptions { Effort = "medium", Signal = cancel ? new CancellationToken(true) : default, OnPayload = (_, _) => throw new InvalidOperationException("capture failed") };
        var capture = await SendAsync(Context(), options, Managed());
        Assert.Null(capture.Body);
        Assert.Equal(cancel ? StopReason.Aborted : StopReason.Error, capture.Result.StopReason);
        Assert.Equal("medium", capture.Result.ProviderThinkingLevel);
    }

    /// <summary>配置文件继承、模型级禁用及显式 Off 在带供应商选项的分发路径中生效。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Effort_ConfigurationInheritanceAndExplicitOffArePreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-effort-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"providers":{"test":{"compat":{"supportsMidConvoEffort":true,"forceAdaptiveThinking":true},"options":{"thinkingDisplay":"omitted","thinkingEnabled":true,"effort":"low"},"models":[{"id":"managed","reasoning":true,"thinkingLevelMap":{"xhigh":"max"}},{"id":"disabled","reasoning":true,"compat":{"supportsMidConvoEffort":false}}]}}}
                """);
            var models = new ModelConfigurationStore([path]).ApplyToModels([], ModelTypes.Chat);
            var managed = models.Single(model => model.Id == "managed");
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(managed, TauAiJsonContext.Default.Model), TauAiJsonContext.Default.Model)!;
            Assert.True(restored.Compat!.SupportsMidConvoEffort);
            Assert.False(models.Single(model => model.Id == "disabled").Compat!.SupportsMidConvoEffort);
            var capture = await SendAsync(Context(), new SimpleStreamOptions { ApiKey = "key", Reasoning = ThinkingLevel.ExtraHigh }, Model() with { Id = "managed", Compat = restored.Compat, ThinkingLevelMap = restored.ThinkingLevelMap }, "simple", configuration: new([path]));
            Assert.Equal("max", capture.Result.ProviderThinkingLevel);
            Assert.Equal("omitted", capture.Body!.Value.GetProperty("thinking").GetProperty("display").GetString());
            var disabled = models.Single(model => model.Id == "disabled");
            var off = await SendAsync(Context(), new SimpleStreamOptions { ApiKey = "key", Reasoning = ThinkingLevel.Off }, Model() with { Id = "disabled", Compat = disabled.Compat }, "simple", configuration: new([path]));
            Assert.Equal("disabled", off.Body!.Value.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(off.Body.Value.TryGetProperty("output_config", out _));
            Assert.Null(off.Result.ProviderThinkingLevel);
        }
        finally { File.Delete(path); }
    }

    /// <summary>【AI】【Anthropic 响应回归】初始思考正文及签名与后续增量共同保留到最终历史。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Effort_InitialThinkingContentAndSignatureSurviveDeltas()
    {
        var sse = Success.Replace("\"type\":\"text\",\"text\":\"answer\"", "\"type\":\"thinking\",\"thinking\":\"start\",\"signature\":\"sig\"");
        var marker = "event: content_block_stop";
        sse = sse.Replace(marker, "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\" end\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"nature\"}}\n\n" + marker);
        var capture = await SendAsync(Context(), model: Managed(), sse: sse);
        var thinking = Assert.IsType<ThinkingContent>(Assert.Single(capture.Result.Content));
        Assert.Equal("start end", thinking.Thinking);
        Assert.Equal("signature", thinking.ThinkingSignature);
        Assert.Equal("high", capture.Result.ProviderThinkingLevel);
    }

    /// <summary>【AI】【模型目录回归】既有内置模型声明正确的自适应能力，手工模型不依赖名称猜测。</summary>
    /// <param name="provider">供应商。</param>
    /// <param name="id">模型标识。</param>
    /// <param name="adaptive">预期自适应能力。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("anthropic", "claude-opus-4-6", true)]
    [InlineData("anthropic", "claude-sonnet-4-6", true)]
    [InlineData("anthropic", "claude-opus-4-7", true)]
    [InlineData("anthropic", "claude-opus-4-8", true)]
    [InlineData("anthropic", "claude-fable-5", true)]
    [InlineData("anthropic", "claude-sonnet-5", true)]
    [InlineData("anthropic", "claude-haiku-4-5", false)]
    [InlineData("github-copilot", "claude-sonnet-4.6", true)]
    [InlineData("github-copilot", "claude-opus-4.7", true)]
    [InlineData("github-copilot", "claude-haiku-4.5", false)]
    public async Task Effort_BuiltInCatalogDeclaresAdaptiveCapability(string provider, string id, bool adaptive)
    {
        var model = new ModelCatalog(configurationStore: new ModelConfigurationStore([])).GetModel(provider, id) with { BaseUrl = "https://example.invalid" };
        Assert.Equal(adaptive, model.Compat?.ForceAdaptiveThinking == true);
        var capture = await SendAsync(Context(), new SimpleStreamOptions { ApiKey = "key", Reasoning = ThinkingLevel.High }, model, "direct-simple");
        Assert.Equal(adaptive ? "adaptive" : "enabled", capture.Body!.Value.GetProperty("thinking").GetProperty("type").GetString());
    }

    /// <summary>创建最小模型。</summary>
    /// <returns>模拟模型。</returns>
    private static Model Model() => new() { Id = "test", Name = "Test", Api = "anthropic-messages", Provider = "test", BaseUrl = "https://example.invalid", Reasoning = true, MaxOutputTokens = 32000 };

    /// <summary>创建支持原生 effort 的模型。</summary>
    /// <returns>启用两项思考能力的模型。</returns>
    private static Model Managed() => Model() with { Compat = new() { ForceAdaptiveThinking = true, SupportsMidConvoEffort = true } };

    /// <summary>创建工具定义。</summary>
    /// <param name="name">本地名称。</param>
    /// <returns>空参数工具。</returns>
    private static Tool Tool(string name) => new(name, "description", Json("{\"type\":\"object\"}"));

    /// <summary>创建含开场提示的上下文。</summary>
    /// <param name="tools">工具声明。</param>
    /// <returns>消息式上下文。</returns>
    private static LlmContext Context(params Tool[] tools) => new(null, [new SystemMessage("base") { ToolsAdded = tools }, new UserMessage("hi")], null);

    /// <summary>构造服务端工具调用事件。</summary>
    /// <param name="name">服务端工具名称。</param>
    /// <returns>完整模拟 SSE。</returns>
    private static string ToolResponse(string name) => Success.Replace("\"type\":\"text\",\"text\":\"answer\"", "\"type\":\"tool_use\",\"id\":\"call-next\",\"name\":\"" + name + "\",\"input\":{}").Replace("\"end_turn\"", "\"tool_use\"");

    /// <summary>复制 JSON 值。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>独立 JSON。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>【AI】【Anthropic 协议回归】执行完整请求并捕获报文、请求头、事件和最终助手消息。</summary>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="model">模型覆盖。</param>
    /// <param name="entry">公开入口。</param>
    /// <param name="sse">服务端事件覆盖。</param>
    /// <param name="configuration">配置存储覆盖。</param>
    /// <returns>请求及响应快照。</returns>
    private static async Task<Capture> SendAsync(LlmContext context, StreamOptions? options = null, Model? model = null, string entry = "direct", string? sse = null, ModelConfigurationStore? configuration = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(sse ?? Success));
        using var client = new HttpClient(handler);
        IStreamProvider provider = new AnthropicProvider(client);
        model ??= Model();
        options ??= new AnthropicOptions { ApiKey = "test-key" };
        var environment = new Dictionary<string, string> { ["ANTHROPIC_AUTH_TOKEN"] = "", ["ANTHROPIC_OAUTH_TOKEN"] = "", ["ANTHROPIC_API_KEY"] = "" };
        foreach (var pair in options.Env ?? new Dictionary<string, string>()) environment[pair.Key] = pair.Value;
        options = options with { Env = environment };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, context, (SimpleStreamOptions)options),
            "stream" => StreamFunctions.Stream(registry, model, context, options, configuration ?? new([])),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, (SimpleStreamOptions)options, configuration ?? new([])),
            "models" => models.Stream(model, context, options),
            "models-simple" => models.StreamSimple(model, context, (SimpleStreamOptions)options),
            _ => provider.Stream(model, context, options)
        };
        var events = new List<StreamEvent>();
        await foreach (var item in stream) events.Add(item);
        var headers = handler.Requests.SingleOrDefault()?.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        return new(await stream.ResultAsync, events, headers, handler.Requests.Count == 0 ? null : Json(handler.CapturedBody));
    }

    private sealed record Capture(AssistantMessage Result, List<StreamEvent> Events, Dictionary<string, string> Headers, JsonElement? Body);
}
