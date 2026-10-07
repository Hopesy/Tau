// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【请求配置回归】验证独立会话从配置、认证、真实 Provider 到摘要的完整请求链。</summary>
public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>公开 Agent 门面传递独立配置和认证，调用方显式预算仍优先。</summary>
    /// <param name="explicitBudget">是否覆盖配置预算。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentFacade_PassesConfigurationAndAuth(bool explicitBudget)
    {
        using var fixture = new Fixture("facade", "auth");
        var catalog = fixture.Catalog();
        var agent = new Agent(new AgentOptions
        {
            Model = catalog.GetModel("session-provider", "test-model"),
            ProviderRegistry = fixture.Options().ProviderRegistry!, ConfigurationStore = catalog.ConfigurationStore,
            AuthResolver = catalog.AuthResolver,
            StreamOptions = new() { MaxTokens = explicitBudget ? 23 : null, Reasoning = ThinkingLevel.Off }
        });
        await agent.PromptAsync("synthetic");
        Assert.Null(agent.State.ErrorMessage);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("auth-facade", request.Key);
        Assert.Equal(explicitBudget ? 23 : 37, request.Body.GetProperty("max_output_tokens").GetInt32());
    }

    /// <summary>并存会话使用各自模型和认证配置，普通请求及两类摘要不串用默认配置。</summary>
    /// <param name="source">认证或目录注入方式。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("auth")]
    [InlineData("models")]
    [InlineData("explicit")]
    [InlineData("catalog")]
    public async Task Sdk_UsesSessionConfigurationForRequestsAndSummaries(string source)
    {
        using var first = new Fixture("first", source);
        using var second = new Fixture("second", source);
        var originalCwd = Environment.CurrentDirectory;
        var firstTask = ExerciseAsync(first, source);
        var secondTask = ExerciseAsync(second, source);
        await Task.WhenAll(firstTask, secondTask);
        Assert.Equal(originalCwd, Environment.CurrentDirectory);
        Assert.All(first.Handler.Requests, request => Assert.Contains("first", request.Key!));
        Assert.All(second.Handler.Requests, request => Assert.Contains("second", request.Key!));
    }

    /// <summary>切换模型后重新解析新模型的预算和请求头，不固化创建会话时的选项。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Sdk_ModelSwitchResolvesNewRequestOptions()
    {
        using var fixture = new Fixture("switch", "auth");
        using var session = await CodingAgentSdk.CreateSessionAsync(fixture.Options());
        await DrainAsync(session.RunAsync("first"));
        session.Runner.SelectModel("session-provider", "second-model");
        await DrainAsync(session.RunAsync("second"));
        Assert.Equal(37, fixture.Handler.Requests[0].Body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(19, fixture.Handler.Requests[1].Body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("second-model", fixture.Handler.Requests[1].Body.GetProperty("model").GetString());
        Assert.Equal("model-specific", fixture.Handler.Requests[1].Header);
    }

    /// <summary>显式 AgentDirectory 缺少模型时不回退到 Cwd 下的同名配置。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Sdk_ExplicitDirectoryDoesNotFallBackToProjectModels()
    {
        using var fixture = new Fixture("isolated", "auth");
        var projectConfig = Path.Combine(fixture.Root, ".tau");
        Directory.CreateDirectory(projectConfig);
        File.Move(Path.Combine(fixture.AgentDirectory, "models.json"), Path.Combine(projectConfig, "models.json"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CodingAgentSdk.CreateSessionAsync(fixture.Options()));
        Assert.Empty(fixture.Handler.Requests);
    }

    /// <summary>不指定独立资源目录时，SDK 依据会话 Cwd 读取项目配置，而非进程目录。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Sdk_DefaultConfigurationUsesSessionWorkingDirectory()
    {
        using var fixture = new Fixture("cwd", "models");
        var projectConfig = Path.Combine(fixture.Root, ".tau");
        Directory.CreateDirectory(projectConfig);
        File.Copy(Path.Combine(fixture.AgentDirectory, "models.json"), Path.Combine(projectConfig, "models.json"));
        using var session = await CodingAgentSdk.CreateSessionAsync(fixture.Options(explicitDirectory: false));
        await DrainAsync(session.RunAsync("run"));
        Assert.Equal("models-cwd", Assert.Single(fixture.Handler.Requests).Key);
    }

    /// <summary>SDK 默认注册表读取同一模型配置中的自定义协议。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Sdk_RegistersProtocolFromInjectedCatalog()
    {
        using var fixture = new Fixture("protocol", "catalog");
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var address = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
        var served = ServeChatAsync(server, deadline.Token);
        var path = Path.Combine(fixture.AgentDirectory, "models.json");
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"api\":\"openai-responses\"", "\"api\":\"custom-protocol\",\"apiKind\":\"openai-compatible\"", StringComparison.Ordinal)
            .Replace("https://protocol.invalid/v1", address, StringComparison.Ordinal));
        var catalog = fixture.Catalog();
        var runner = RuntimeCodingAgentRunner.Create("session-provider", "test-model", toolsOverride: [],
            modelCatalogOverride: catalog, workingDirectory: fixture.Root);
        await DrainAsync(runner.RunAsync("synthetic", deadline.Token));
        var request = await served;
        Assert.Contains("POST /chat/completions", request);
        Assert.Contains("Bearer auth-protocol", request);
        Assert.Equal("answer", Assert.Single(runner.Messages.OfType<AssistantMessage>()).Content.OfType<TextContent>().Single().Text);
        Assert.Equal("custom-protocol", runner.Model.Api);
    }

    /// <summary>验证模型目录携带的配置和认证实例保持一致。</summary>
    [Fact]
    public void Catalog_RetainsItsConfigurationAndAuthentication()
    {
        using var fixture = new Fixture("catalog-context", "models");
        var config = new ModelConfigurationStore([Path.Combine(fixture.AgentDirectory, "models.json")]);
        var catalog = new ModelCatalog(configurationStore: config);
        Assert.Same(config, catalog.ConfigurationStore);
        Assert.True(catalog.AuthResolver.GetStatus(catalog.GetModel("session-provider", "test-model")).IsConfigured);
    }

    /// <summary>执行普通请求、分支摘要和压缩，逐次检查认证、请求头与模型身份。</summary>
    /// <param name="fixture">会话夹具。</param>
    /// <param name="source">认证来源。</param>
    /// <returns>异步验证任务。</returns>
    private static async Task ExerciseAsync(Fixture fixture, string source)
    {
        using var session = await CodingAgentSdk.CreateSessionAsync(fixture.Options());
        session.Runner.CompactionSettings = new(KeepRecentTokens: 0);
        Assert.True(session.Runner.GetAuthStatus().IsConfigured);
        await DrainAsync(session.RunAsync("synthetic request"));
        Assert.Equal("answer", string.Concat(session.Messages.OfType<AssistantMessage>().Last().Content.OfType<TextContent>().Select(item => item.Text)));
        await session.Runner.SummarizeBranchAsync(session.Messages);
        await session.Runner.CompactAsync();
        Assert.Equal(3, fixture.Handler.Requests.Count);
        foreach (var request in fixture.Handler.Requests)
        {
            Assert.Equal(source == "explicit" ? "explicit-" + fixture.Marker : source == "models" ? "models-" + fixture.Marker : "auth-" + fixture.Marker, request.Key);
            Assert.Equal(fixture.Marker, request.Header);
            Assert.Equal("test-model", request.Body.GetProperty("model").GetString());
            Assert.Equal("none", request.Body.GetProperty("reasoning").GetProperty("effort").GetString());
        }
        Assert.Equal(37, fixture.Handler.Requests[0].Body.GetProperty("max_output_tokens").GetInt32());
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".tau", "coding-agent-session.jsonl")));
    }

    /// <summary>消费 Agent 事件并在错误时报告失败。</summary>
    /// <param name="events">运行事件。</param>
    /// <returns>消费任务。</returns>
    private static async Task DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        await foreach (var item in events)
        {
            if (item is AgentEndEvent end) Assert.Null(end.ErrorMessage);
            if (item is CodingAgentExtensionErrorEvent extension) Assert.Fail(extension.Error);
        }
    }

    /// <summary>【CodingAgent】【协议注册回归】在回环地址接受一个真实请求并返回 Chat SSE。</summary>
    /// <param name="server">已监听的临时服务器。</param>
    /// <param name="cancellationToken">测试截止信号。</param>
    /// <param name="inspectBody">可选请求体断言。</param>
    /// <returns>请求头，供核对协议路由及认证。</returns>
    private static async Task<string> ServeChatAsync(TcpListener server, CancellationToken cancellationToken, Action<JsonElement>? inspectBody = null)
    {
        using var client = await server.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var header = new StringBuilder();
        var single = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(single, cancellationToken);
            header.Append((char)single[0]);
            Assert.True(header.Length < 32768);
        }
        var length = int.Parse(header.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        using var document = JsonDocument.Parse(body);
        inspectBody?.Invoke(document.RootElement);
        var data = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {Encoding.UTF8.GetByteCount(data)}\r\nConnection: close\r\n\r\n{data}");
        await stream.WriteAsync(response, cancellationToken);
        return header.ToString();
    }

    /// <summary>【CodingAgent】【请求配置回归】持有独立配置目录和内存 HTTP Provider。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-request-config-" + Guid.NewGuid().ToString("N"));
        public string AgentDirectory => Path.Combine(Root, "agent");
        public string Marker { get; }
        public CaptureHandler Handler { get; } = new();
        private readonly HttpClient _client;
        private readonly string _source;

        /// <summary>创建具有独立身份和预算的测试配置。</summary>
        /// <param name="marker">会话标记。</param>
        /// <param name="source">认证来源。</param>
        public Fixture(string marker, string source)
        {
            Marker = marker;
            _source = source;
            _client = new HttpClient(Handler);
            Directory.CreateDirectory(AgentDirectory);
            File.WriteAllText(Path.Combine(AgentDirectory, "models.json"), $$$$"""
                {"providers":{"session-provider":{"api":"openai-responses","baseUrl":"https://{{{{marker}}}}.invalid/v1",
                "apiKey":"models-{{{{marker}}}}","headers":{"X-Session":"{{{{marker}}}}"},"options":{"maxTokens":37},
                "models":[{"id":"test-model","name":"Test","reasoning":true,"maxTokens":99},
                {"id":"second-model","name":"Second","reasoning":true,"headers":{"X-Session":"model-specific"},"options":{"maxTokens":19}}]}}}
                """);
            File.WriteAllText(Path.Combine(AgentDirectory, "auth.json"), source == "models" ? "{}" :
                $$$$"""{"session-provider":{"type":"api_key","key":"auth-{{{{marker}}}}"}}""");
        }

        /// <summary>创建绑定夹具目录的模型及认证上下文。</summary>
        /// <returns>模型目录。</returns>
        public ModelCatalog Catalog()
        {
            var config = new ModelConfigurationStore([Path.Combine(AgentDirectory, "models.json")]);
            return new ModelCatalog(new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([Path.Combine(AgentDirectory, "auth.json")]), configurationStore: config), config);
        }

        /// <summary>创建使用真实 Responses Provider 的 SDK 选项。</summary>
        /// <param name="explicitDirectory">是否显式指定资源目录。</param>
        /// <returns>SDK 创建选项。</returns>
        public CodingAgentSdkCreateSessionOptions Options(bool explicitDirectory = true)
        {
            var registry = new ProviderRegistry();
            registry.Register("openai-responses", new OpenAiResponsesProvider(_client));
            return new()
            {
                Cwd = Root, AgentDirectory = explicitDirectory ? AgentDirectory : null, NoSession = true,
                ProviderId = "session-provider", ModelId = "test-model", ProviderRegistry = registry,
                ModelCatalog = _source == "catalog" ? Catalog() : null,
                ApiKey = _source == "explicit" ? "explicit-" + Marker : null,
                ThinkingLevel = ThinkingLevel.Off, NoTools = CodingAgentSdkNoToolsMode.All,
                IncludeExtensions = false, IncludeSkills = false, IncludeContextFiles = false, IncludePromptTemplates = false
            };
        }

        /// <summary>释放 HTTP 资源并清理本夹具创建的临时目录。</summary>
        public void Dispose()
        {
            _client.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record Request(string? Key, string? Header, JsonElement Body);

    /// <summary>返回只含终值的真实 Responses SSE，捕获请求而不访问外网。</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        /// <summary>记录模型请求并返回可供正文输出和摘要使用的回复。</summary>
        /// <param name="request">待处理的 HTTP 请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟 SSE 响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(new(request.Headers.Authorization?.Parameter, request.Headers.GetValues("X-Session").Single(), body.RootElement.Clone()));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","content":[{"type":"output_text","text":"answer"}]}}

                    data: {"type":"response.completed","response":{"status":"completed"}}


                    """, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
