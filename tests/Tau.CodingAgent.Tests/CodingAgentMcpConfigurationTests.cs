// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpConfigurationTests
{
    /// <summary>【CodingAgent】【MCP 校验】拒绝无效传输、凭据目标及 OAuth 参数。</summary>
    /// <param name="json">待验证配置。</param><param name="error">预期诊断字段。</param>
    [Theory]
    [InlineData("null", "object")]
    [InlineData("[]", "object")]
    [InlineData("{}", "command")]
    [InlineData("{\"type\":\"sse\",\"url\":\"https://example.test\"}", "legacy SSE")]
    [InlineData("{\"url\":\"file:///tmp/server\"}", "http or https")]
    [InlineData("{\"url\":\"http://example.test\",\"auth\":{\"provider\":\"test\"}}", "auth requires")]
    [InlineData("{\"url\":\"https://example.test\",\"headers\":{\"X-Test\":2}}", "headers")]
    [InlineData("{\"command\":\"node\",\"args\":[2]}", "args")]
    [InlineData("{\"command\":\"node\",\"env\":{\"KEY\":null}}", "env")]
    [InlineData("{\"command\":\"node\",\"timeout\":0}", "timeout")]
    [InlineData("{\"command\":\"node\",\"timeout\":1e400}", "timeout")]
    [InlineData("{\"command\":\"node\",\"timeout\":1e40}", "timeout")]
    [InlineData("{\"command\":\"node\",\"enabled\":null}", "enabled")]
    [InlineData("{\"command\":\"node\",\"toolExposure\":{\"x\":\"public\"}}", "toolExposure")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":null}", "oauth")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"callbackPort\":1.5}}", "callbackPort")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"callbackPort\":65536}}", "callbackPort")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"callbackPort\":1234,\"callbackUrl\":\"http://localhost:4321/callback\"}}", "different ports")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"callbackUrl\":\"http://example.test/callback\"}}", "callbackUrl")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"clientRegistration\":\"cimd\",\"clientId\":\"x\"}}", "cannot be combined")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"clientRegistration\":\"cimd\",\"callbackUrl\":\"http://[::1]/callback\"}}", "requires oauth.callbackUrl")]
    [InlineData("{\"url\":\"https://example.test\",\"oauth\":{\"authServerMetadataUrl\":\"http://example.test\"}}", "authServerMetadataUrl")]
    public void McpValidationRejectsInvalidConfiguration(string json, string error) =>
        Assert.Contains(error, Assert.Throws<ArgumentException>(() => CodingAgentMcpServers.Validate("test-server", JsonNode.Parse(json))).Message);

    /// <summary>【CodingAgent】【MCP 策略】精确名称优先，星号顺序稳定且正则字符按字面处理，规范化不修改输入。</summary>
    [Fact]
    public void McpExposureUsesExactNamesThenOrderedPatterns()
    {
        var raw = JsonNode.Parse("""{"command":"node","exposure":"hidden","toolExposure":{"read*":"codemode-deferred","read.secret":"hidden","*":"deferred","read.*":"direct"}}""")!;
        var config = CodingAgentMcpServers.Validate("my-server", raw);
        Assert.Equal("codemode-deferred", raw["toolExposure"]!["read*"]!.GetValue<string>());
        Assert.Equal("codemode", CodingAgentMcpServers.GetToolExposure(config, "read_file"));
        Assert.Equal("hidden", CodingAgentMcpServers.GetToolExposure(config, "read.secret"));
        Assert.Equal("deferred", CodingAgentMcpServers.GetToolExposure(config, "other"));
        Assert.Equal("mcp__my_server", CodingAgentMcpServers.Namespace("my-server"));
        Assert.Throws<ArgumentException>(() => CodingAgentMcpServers.Validate("bad.name", config));
        config["toolExposure"] = JsonNode.Parse("""{"a.b*":"direct"}""");
        Assert.Equal("direct", CodingAgentMcpServers.GetToolExposure(config, "a.b-tail"));
        Assert.Equal("hidden", CodingAgentMcpServers.GetToolExposure(config, "aXb-tail"));
    }

    /// <summary>【CodingAgent】【MCP 地址】支持 HTTP 回环 OAuth，拒绝非回环地址和带查询的回调。</summary>
    /// <param name="url">回调地址。</param><param name="valid">预期是否有效。</param>
    [Theory]
    [InlineData("http://localhost/callback", true)]
    [InlineData("http://127.0.0.1:9000/other", true)]
    [InlineData("http://[::1]/callback", true)]
    [InlineData("https://localhost/callback", false)]
    [InlineData("http://localhost/callback?x=1", false)]
    [InlineData("http://127.0.0.2/callback", false)]
    public void McpCallbackAllowsOnlyNativeLoopbackHosts(string url, bool valid) => Assert.Equal(valid, CodingAgentMcpServers.IsLoopbackRedirectUri(url));

    /// <summary>【CodingAgent】【MCP 分层】未信任项目不读取配置，可信覆盖保留全局凭据且不允许改发其他目标。</summary>
    [Fact]
    public void McpLayeredConfigurationPreservesTrustAndCredentialOwnership()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, """{"autoEnableCodemode":true,"mcpServers":{"shared":{"url":"https://example.test","auth":{"provider":"test"}},"my-server":{"command":"node"}}}""");
        File.WriteAllText(fixture.Project, """{"autoEnableCodemode":false,"mcpServers":{"shared":{"enabled":false,"exposure":"direct"},"my_server":{"command":"node"},"credential":{"url":"https://other.test","auth":{"provider":"test"}},"missing":{"enabled":false},"local":{"command":"node","exposure":"codemode-deferred"}}}""");
        var untrusted = CodingAgentMcpConfiguration.Load(fixture.Agent, fixture.Root, false);
        Assert.Equal(2, untrusted.Servers.Count);
        Assert.True(untrusted.AutoEnableCodemode);
        Assert.Null(untrusted.ProjectConfig);
        Assert.Empty(untrusted.Errors);
        var trusted = CodingAgentMcpConfiguration.Load(fixture.Agent, fixture.Root, true);
        Assert.Equal(3, trusted.Servers.Count);
        Assert.False(trusted.AutoEnableCodemode);
        Assert.Equal(3, trusted.Errors.Count);
        var shared = trusted.Servers.Single(server => server.Name == "shared");
        Assert.Equal(fixture.Global, shared.Source);
        Assert.Equal(fixture.Project, shared.Override);
        Assert.False(shared.Config["enabled"]!.GetValue<bool>());
        Assert.Equal("test", shared.Config["auth"]!["provider"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【MCP 编辑】保留其他字段和制表符缩进，默认值只从完整定义移除。</summary>
    [Fact]
    public void McpConfigurationEditsPreserveUnknownFieldsAndOverrideDefaults()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, "{\n\t\"other\": {\"label\":\"保留\"},\n\t\"mcpServers\":{\"test\":{\"command\":\"node\",\"enabled\":false,\"exposure\":\"direct\"}}\n}\n");
        CodingAgentMcpConfiguration.Update(fixture.Global, "test", true, "codemode");
        var text = File.ReadAllText(fixture.Global);
        var json = JsonNode.Parse(text)!;
        Assert.Contains("\n\t\"other\"", text);
        Assert.Contains("保留", text);
        Assert.False(json["mcpServers"]!["test"]!.AsObject().ContainsKey("enabled"));
        Assert.False(json["mcpServers"]!["test"]!.AsObject().ContainsKey("exposure"));
        CodingAgentMcpConfiguration.Update(fixture.Project, "test", true, "codemode", true);
        var patch = JsonNode.Parse(File.ReadAllText(fixture.Project))!["mcpServers"]!["test"]!;
        Assert.True(patch["enabled"]!.GetValue<bool>());
        Assert.Equal("codemode", patch["exposure"]!.GetValue<string>());
        Assert.True(CodingAgentMcpConfiguration.Add(fixture.Global, "test", new() { ["command"] = "python" }));
        Assert.True(CodingAgentMcpConfiguration.Remove(fixture.Global, "test"));
        Assert.False(CodingAgentMcpConfiguration.Remove(fixture.Global, "test"));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(fixture.Global))!["other"]);
        File.WriteAllText(fixture.Project, "{invalid}");
        Assert.ThrowsAny<JsonException>(() => CodingAgentMcpConfiguration.Add(fixture.Project, "test", new() { ["command"] = "node" }));
        Assert.Equal("{invalid}", File.ReadAllText(fixture.Project));
    }

    /// <summary>【CodingAgent】【MCP 注册】注册表隔离调用方修改，禁止跨扩展覆盖和命名空间冲突。</summary>
    [Fact]
    public void McpRegistryEnforcesOwnershipAndCopiesDefinitions()
    {
        var registry = new CodingAgentMcpServerRegistry();
        var changes = 0;
        registry.Changed += _ => changes++;
        var config = new JsonObject { ["command"] = "node" };
        registry.Register("my-server", config, "owner");
        config["command"] = "changed";
        var snapshot = registry.List();
        Assert.Equal("node", snapshot[0].Config["command"]!.GetValue<string>());
        snapshot[0].Config["command"] = "changed again";
        Assert.Equal("node", registry.List()[0].Config["command"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => registry.Register("my-server", config, "other"));
        Assert.Throws<InvalidOperationException>(() => registry.Register("my_server", config, "owner"));
        Assert.False(registry.Unregister("my-server", "other"));
        registry.Register("my-server", config, "owner");
        Assert.True(registry.Unregister("my-server", "owner"));
        Assert.Equal(3, changes);
        Assert.Empty(registry.List());
    }

    /// <summary>【CodingAgent】【MCP 测试目录】隔离配置文件并限定清理范围。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-mcp-config-" + Guid.NewGuid().ToString("N"));
        public string Agent => Path.Combine(Root, "agent");
        public string Global => Path.Combine(Agent, "mcp.json");
        public string Project => Path.Combine(Root, ".tau", "mcp.json");
        /// <summary>【CodingAgent】【MCP 测试目录】建立本次测试专用目录。</summary>
        public Fixture() { Directory.CreateDirectory(Agent); Directory.CreateDirectory(Path.GetDirectoryName(Project)!); }
        /// <summary>【CodingAgent】【MCP 测试清理】仅删除构造时生成的临时目录。</summary>
        public void Dispose() => Directory.Delete(Root, true);
    }
}
