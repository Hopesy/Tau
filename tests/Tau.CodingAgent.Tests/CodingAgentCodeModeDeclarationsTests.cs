// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentCodeModeDeclarationsTests
{
    /// <summary>【CodingAgent】【Schema 对照】覆盖上游字面量、联合、对象、元组及引用展开约定。</summary>
    /// <param name="schema">输入 Schema。</param><param name="expected">上游的类型文本。</param>
    [Theory]
    [InlineData("false", "never")]
    [InlineData("""{"type":["string","null"]}""", "string | null")]
    [InlineData("""{"enum":["a",1,null,"a"]}""", "\"a\" | 1 | null")]
    [InlineData("""{"allOf":[{"anyOf":[{"type":"string"},{"type":"number"}]},{"const":1}]}""", "(string | number) & 1")]
    [InlineData("""{"type":"object","properties":{"max-lines":{"type":"number"},"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""", "{ city: string; \"max-lines\"?: number; }")]
    [InlineData("""{"type":"array","prefixItems":[{"type":"string"},{"type":"number"}]}""", "[string, number]")]
    [InlineData("""{"type":"object","properties":{"value":{"$ref":"#/$defs/a~1b~0c"}},"$defs":{"a/b~c":{"type":"integer"}}}""", "{ value?: number; }")]
    [InlineData("""{"type":"object","properties":{"next":{"$ref":"#"}}}""", "{ next?: { next?: unknown; }; }")]
    public void CodeModeSchemaMatchesNativeDeclarationFixtures(string schema, string expected) => Assert.Equal(expected, CodingAgentCodeModeDeclarations.SchemaType(Parse(schema)));

    /// <summary>【CodingAgent】【声明预算】展开次数和字符上限限制大 Schema，同时保留中文属性说明与 MCP 包络类型。</summary>
    [Fact]
    public void CodeModeDeclarationsBoundReferencesAndRenderCommentsAndMcpResults()
    {
        var properties = new JsonObject(); for (var index = 0; index < 40; index++) properties["p" + index.ToString("D2")] = new JsonObject { ["$ref"] = "#/$defs/value" };
        var schema = Parse(new JsonObject { ["type"] = "object", ["properties"] = properties, ["$defs"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } } }.ToJsonString());
        var type = CodingAgentCodeModeDeclarations.SchemaType(schema);
        Assert.Contains("p31?: string", type); Assert.Contains("p32?: unknown", type); Assert.Equal("unknown", CodingAgentCodeModeDeclarations.SchemaType(schema, 10));
        Assert.Equal("{\n  // 中文\n  // 第二行\n  value?: string;\n}", CodingAgentCodeModeDeclarations.SchemaType(Parse("""{"properties":{"value":{"type":"string","description":"中文\n第二行"}}}""")));
        var mcp = Parse("""{"properties":{"content":{"type":"array","items":{"type":"object"}},"isError":{"type":"boolean"},"_meta":{"type":"object"},"structuredContent":{"properties":{"ok":{"type":"boolean"}},"required":["ok"]}}}""");
        Assert.Equal("CallToolResult<{ ok: boolean; }>", CodingAgentCodeModeDeclarations.OutputType(mcp));
        Assert.Contains("structuredContent?: TStructured", CodingAgentCodeModeDeclarations.McpTypeScriptPreamble);
    }

    /// <summary>【CodingAgent】【目录分配】短声明跨命名组公平分配，预算为零保留组标题，延迟工具始终不进入提示。</summary>
    [Fact]
    public void CodeModeLoadoutBalancesNamespacesAndExcludesDeferredTools()
    {
        using var temp = new CodeModeDirectory();
        var runner = RuntimeCodingAgentRunner.Create(workingDirectory: temp.Path);
        var settings = Parse("""{"mode":"only","inlineBudget":150}""");
        var code = new CodingAgentCodeModeTool(runner, settings: () => settings);
        IAgentTool[] tools = [new CatalogTool("large", "alpha", new string('L', 5000)), new CatalogTool("small_a", "alpha", "short"),
            new CatalogTool("small_b", "beta", "short"), new CatalogTool("deferred", "secret_namespace", "not listed", "deferred")];
        var changes = code.PrepareLoadout(new([code, .. tools], tools, [code, .. tools]))!;
        var description = changes.Descriptions["codemode"];
        Assert.Contains("### `small_a`", description); Assert.Contains("### `small_b`", description); Assert.DoesNotContain("### `large`", description);
        Assert.Contains("## alpha (some tools not listed)", description); Assert.DoesNotContain("secret_namespace", description);
        Assert.Equal(["large", "small_a", "small_b"], changes.HiddenDeclarations);
        settings = Parse("""{"mode":"only","inlineBudget":0}""");
        description = code.PrepareLoadout(new([code, .. tools], tools, [code, .. tools]))!.Descriptions["codemode"];
        Assert.Contains("## beta (tools not listed)", description); Assert.DoesNotContain("### `", description);
    }

    /// <summary>【CodingAgent】【公开目录】验证无会话目录的默认能力、显式命名组、延迟排除和预算控制。</summary>
    [Fact]
    public void CodeModePublicDescriptionUsesExplicitCatalogOptions()
    {
        IAgentTool[] tools = [new CatalogTool("codemode", "self", "self tool"), new CatalogTool("ordinary", "implicit", "ordinary tool"),
            new CatalogTool("secret", "secret", "hidden tool", "deferred")];
        Assert.Equal(["ordinary", "secret"], CodingAgentCodeModeTool.GetCallableTools(tools).Select(tool => tool.Name));
        var description = CodingAgentCodeModeTool.CreateDescription(tools);
        Assert.Contains("### \u0060ordinary\u0060", description);
        Assert.Contains("### \u0060secret\u0060", description);
        Assert.DoesNotContain("### \u0060codemode\u0060", description);
        Assert.DoesNotContain("## implicit", description);
        Assert.DoesNotContain("models.getModelsOfType", description);
        var options = new CodingAgentCodeModeDescriptionOptions
        {
            Models = true, InlineBudget = 0, Deferred = new HashSet<string> { "secret" },
            Namespaces = new Dictionary<string, JsonElement> { ["ordinary"] = Parse("""{"name":"explicit","description":" group details "}""") }
        };
        description = CodingAgentCodeModeTool.CreateDescription(tools, options);
        Assert.Contains("models.getModelsOfType", description);
        Assert.Contains("## explicit (tools not listed)\ngroup details", description);
        Assert.DoesNotContain("### \u0060", description);
        Assert.DoesNotContain("hidden tool", description);
        Assert.DoesNotContain("secret", description);
        description = CodingAgentCodeModeTool.CreateDescription(tools, options with { InlineBudget = null });
        Assert.Contains("## explicit\ngroup details", description);
        Assert.Contains("### \u0060ordinary\u0060", description);
        Assert.DoesNotContain("secret", description);
        Assert.Throws<ArgumentOutOfRangeException>(() => CodingAgentCodeModeTool.CreateDescription(tools, options with { InlineBudget = double.NaN }));
    }

    /// <summary>【CodingAgent】【声明独立性】公开声明不持有调用方 JSON 文档的生命周期，缺失输出结构回退字符串。</summary>
    [Fact]
    public void CodeModePublicDeclarationClonesSchemasAndDefaultsToText()
    {
        CodingAgentCodeModeDeclaration declaration;
        using (var document = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"number"}}}"""))
            declaration = CodingAgentCodeModeTool.ToDeclaration(new SchemaTool(document.RootElement));
        Assert.Equal("object", declaration.InputSchema.GetProperty("type").GetString());
        Assert.Equal("object", declaration.OutputSchema.GetProperty("type").GetString());
        Assert.Equal("number", declaration.OutputSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("string", CodingAgentCodeModeTool.ToDeclaration(new CatalogTool("text", "", "")).OutputSchema.GetProperty("type").GetString());
    }

    /// <summary>【CodingAgent】【动态脚本配置】仅覆盖指定选项，其余选项在每次声明准备时读取最新设置。</summary>
    [Fact]
    public void CodeModeOptionsOverrideOnlySpecifiedSettings()
    {
        using var temp = new CodeModeDirectory();
        var runner = RuntimeCodingAgentRunner.Create(workingDirectory: temp.Path);
        var settings = Parse("""{"mode":"on","inlineBudget":0}""");
        var code = new CodingAgentCodeModeTool(runner, settings: () => settings, options: new() { Mode = "only" });
        var leaf = new CatalogTool("leaf", "group", "tool");
        var loadout = new CodingAgentToolLoadout([code, leaf], [leaf], [code, leaf]);
        var changes = code.PrepareLoadout(loadout)!;
        Assert.Equal(["leaf"], changes.HiddenDeclarations);
        Assert.DoesNotContain("### \u0060leaf\u0060", changes.Descriptions["codemode"]);
        settings = Parse("""{"mode":"on","inlineBudget":1000}""");
        changes = code.PrepareLoadout(loadout)!;
        Assert.Equal(["leaf"], changes.HiddenDeclarations);
        Assert.Contains("### \u0060leaf\u0060", changes.Descriptions["codemode"]);
        var fixedBudget = new CodingAgentCodeModeTool(runner, settings: () => settings, options: new() { InlineBudget = 0 });
        Assert.Empty(fixedBudget.PrepareLoadout(loadout)!.HiddenDeclarations);
        settings = Parse("""{"mode":"only","inlineBudget":1000}""");
        changes = fixedBudget.PrepareLoadout(loadout)!;
        Assert.Equal(["leaf"], changes.HiddenDeclarations);
        Assert.DoesNotContain("### \u0060leaf\u0060", changes.Descriptions["codemode"]);
    }

    /// <summary>【CodingAgent】【声明生命周期夹具】保留借用 Schema，供测试公开接口是否克隆。</summary><param name="schema">借用文档元素。</param>
    private sealed class SchemaTool(JsonElement schema) : IAgentTool
    {
        public string Name => "schema"; public string Label => Name; public string Description => "schema tool";
        public JsonElement ParameterSchema => schema; public JsonElement? OutputSchema => schema;
        /// <summary>【CodingAgent】【空执行】声明测试不调用执行逻辑。</summary><param name="toolCallId">标识。</param><param name="args">参数。</param><param name="ct">取消。</param><param name="onUpdate">更新。</param><returns>空结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => Task.FromResult(new ToolResult([]));
    }

    /// <summary>【CodingAgent】【声明 JSON】创建可独立使用的 JSON 元素。</summary><param name="json">文本。</param><returns>独立元素。</returns>
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    /// <summary>【CodingAgent】【目录测试工具】只用于构造不同大小和访问策略的声明。</summary><param name="name">名称。</param><param name="group">命名组。</param><param name="description">说明。</param><param name="exposure">访问策略。</param>
    private sealed class CatalogTool(string name, string group, string description, string exposure = "direct") : ICodingAgentToolDefinition
    {
        public string Name => name; public string Label => name; public string Description => description; public string Exposure => exposure;
        public JsonElement ParameterSchema => Parse("""{"type":"object"}""");
        public JsonElement? Namespace => Parse(new JsonObject { ["name"] = group }.ToJsonString());
        /// <summary>【CodingAgent】【测试空执行】目录测试不执行工具。</summary><param name="toolCallId">标识。</param><param name="args">参数。</param><param name="ct">取消。</param><param name="onUpdate">更新。</param><returns>空结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => Task.FromResult(new ToolResult([]));
    }
    /// <summary>【CodingAgent】【声明测试目录】隔离运行器的工作目录。</summary>
    private sealed class CodeModeDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-codemode-declarations-" + Guid.NewGuid().ToString("N"));
        /// <summary>【CodingAgent】【测试目录创建】创建唯一的临时目录。</summary>
        public CodeModeDirectory() => Directory.CreateDirectory(Path);
        /// <summary>【CodingAgent】【测试目录清理】仅删除此夹具创建的目录。</summary>
        public void Dispose() => Directory.Delete(Path, true);
    }
}
