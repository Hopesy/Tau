// 作者：xxx
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpToolTests
{
    /// <summary>【CodingAgent】【MCP 结果测试】验证多模态内容、资源保存、程序结果保留和私有顶层元数据移除。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResultConvertsResourcesAndPreservesScriptResult()
    {
        var saved = new List<(byte[] Data, string Extension)>();
        var raw = Parse("""
            {"content":[
              {"type":"text","text":"说明","_meta":{"kept":true}},
              {"type":"image","data":"aW1n","mimeType":"image/png"},
              {"type":"audio","data":"YQ==","mimeType":"audio/wav"},
              {"type":"resource_link","uri":"test://doc","name":"doc","title":"文档","description":"参考","mimeType":"text/plain","size":2048},
              {"type":"resource","resource":{"uri":"test://inline","text":"内嵌文本"}},
              {"type":"resource","resource":{"uri":"test://image","blob":"aW1n","mimeType":"image/png"}},
              {"type":"resource","resource":{"uri":"test://json","blob":"eyJ4IjoxfQ","mimeType":"application/example+json; charset=utf-8"}},
              {"type":"resource","resource":{"uri":"https://test/doc.pdf?download=1","blob":"AAEC","mimeType":"application/pdf"}}
            ],"structuredContent":{"value":42},"isError":false,"_meta":{"private":"metadata"}}
            """);
        var result = await CodingAgentMcpToolResult.ConvertAsync("docs", "read", raw, true, (bytes, extension, _) =>
        { saved.Add((bytes.ToArray(), extension)); return Task.FromResult("saved" + extension); });
        Assert.False(result.IsError);
        Assert.Equal(8, result.Content.Count);
        Assert.Equal("说明", Assert.IsType<TextContent>(result.Content[0]).Text);
        Assert.IsType<ImageContent>(result.Content[1]);
        Assert.Equal("[audio audio/wav omitted]", Assert.IsType<TextContent>(result.Content[2]).Text);
        Assert.Equal("[Resource test://doc \"文档\" (text/plain, 2.0KB): 参考. Read it with read_mcp_resource (server \"docs\")]", Assert.IsType<TextContent>(result.Content[3]).Text);
        Assert.Equal("内嵌文本", Assert.IsType<TextContent>(result.Content[4]).Text);
        Assert.IsType<ImageContent>(result.Content[5]);
        Assert.Equal("{\"x\":1}", Assert.IsType<TextContent>(result.Content[6]).Text);
        Assert.Contains("saved.pdf", Assert.IsType<TextContent>(result.Content[7]).Text);
        Assert.Equal(new byte[] { 0, 1, 2 }, Assert.Single(saved).Data);
        Assert.Equal(".pdf", saved[0].Extension);
        Assert.False(result.StructuredContent!.Value.TryGetProperty("_meta", out _));
        Assert.True(result.StructuredContent.Value.GetProperty("content")[0].GetProperty("_meta").GetProperty("kept").GetBoolean());
        Assert.Equal(42, result.StructuredContent.Value.GetProperty("structuredContent").GetProperty("value").GetInt32());
        Assert.True(raw.TryGetProperty("_meta", out _));
    }

    /// <summary>【CodingAgent】【MCP 截断测试】中文和表情不被切开，完整结构化内容和图像保留。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResultTruncatesAtUtf8BoundariesAndKeepsFullOutput()
    {
        var text = "首\n" + string.Concat(Enumerable.Repeat("中文😀", 5000)) + "\n尾";
        var raw = Parse(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text },
            new JsonObject { ["type"] = "image", ["data"] = "aW1n", ["mimeType"] = "image/png" }) }.ToJsonString());
        byte[]? saved = null;
        var result = await CodingAgentMcpToolResult.ConvertAsync("s", "t", raw, saveOutput: (bytes, extension, _) =>
        { Assert.Equal(".txt", extension); saved = bytes.ToArray(); return Task.FromResult("/output.txt"); });
        Assert.Equal(text, Encoding.UTF8.GetString(saved!));
        var preview = Assert.IsType<TextContent>(result.Content[0]).Text;
        Assert.Contains("Total output lines: 3", preview);
        Assert.Contains("首\n", preview); Assert.Contains("\n尾", preview);
        Assert.Contains("chars truncated", preview); Assert.DoesNotContain("�", preview);
        Assert.True(Encoding.UTF8.GetByteCount(preview) < 21000);
        Assert.IsType<ImageContent>(result.Content[1]);
        Assert.Equal(text, result.StructuredContent!.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("/output.txt", Assert.IsType<JsonElement>(result.Details).GetProperty("fullOutputPath").GetString());
        var failedSave = await CodingAgentMcpToolResult.ConvertAsync("s", "t", raw, saveOutput: (_, _, _) => throw new IOException("disk full"));
        Assert.Contains("Could not save the full output: disk full", Assert.IsType<TextContent>(failedSave.Content[0]).Text);
        Assert.False(Assert.IsType<JsonElement>(failedSave.Details).TryGetProperty("fullOutputPath", out _));
    }

    /// <summary>【CodingAgent】【MCP 错误测试】空内容回退结构化 JSON，无文字的错误仍有可读说明，保存失败有诊断。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResultHandlesErrorsStructuredFallbackAndBinarySaveFailure()
    {
        var fallback = await CodingAgentMcpToolResult.ConvertAsync("s", "t", Parse("""{"content":[],"structuredContent":{"value":42}}"""));
        Assert.Contains("\"value\": 42", Assert.IsType<TextContent>(Assert.Single(fallback.Content)).Text);
        var error = await CodingAgentMcpToolResult.ConvertAsync("s", "t", Parse("""{"content":[],"isError":true}"""));
        Assert.True(error.IsError);
        Assert.Equal("MCP tool s/t returned an error", Assert.IsType<TextContent>(Assert.Single(error.Content)).Text);
        var binary = await CodingAgentMcpToolResult.ConvertAsync("s", "t", Parse("""{"content":[{"type":"resource","resource":{"uri":"custom://file","blob":"AAEC"}}]}"""),
            saveOutput: (_, extension, _) => { Assert.Equal(".bin", extension); throw new IOException("denied"); });
        Assert.Contains("could not be saved: denied", Assert.IsType<TextContent>(Assert.Single(binary.Content)).Text);
    }

    /// <summary>【CodingAgent】【MCP 文件测试】实际保存文件并验证字节及 Unix 私有权限。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOutputSaverWritesPrivateCompleteFile()
    {
        var data = Encoding.UTF8.GetBytes("私有内容");
        var path = await CodingAgentMcpToolResult.SaveToTempFileAsync(data, ".txt");
        try
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
        await Assert.ThrowsAsync<ArgumentException>(() => CodingAgentMcpToolResult.SaveToTempFileAsync(data, "/../../invalid"));
    }

    /// <summary>【CodingAgent】【MCP 定义测试】验证名称冲突、长度限制、Schema、命名组及工具行为提示。</summary>
    [Fact]
    public void McpToolDefinitionNormalizesProviderMetadataAndNames()
    {
        Assert.Equal("mcp__my_server__a_b", CodingAgentMcpTool.CreateName("my-server", "a-b"));
        var collision = CodingAgentMcpTool.CreateName("my-server", "a-b", _ => true);
        Assert.Matches("^mcp__my_server__a_b_[a-f0-9]{8}$", collision);
        Assert.Equal(collision, CodingAgentMcpTool.CreateName("my-server", "a-b", _ => true));
        Assert.NotEqual(collision, CodingAgentMcpTool.CreateName("my-server", "a_b", _ => true));
        Assert.Equal(64, CodingAgentMcpTool.CreateName("s", new string('t', 80)).Length);
        var tool = new CodingAgentMcpTool("s", Parse("""{"name":"t","description":"  ","annotations":{"title":"标题","readOnlyHint":true,"destructiveHint":"false","openWorldHint":false},"inputSchema":{"required":["x"]},"outputSchema":{"type":"object","properties":{"x":{"type":"number"}}}}"""),
            "mcp__s__t", "codemode", Parse("""{"name":"mcp__s","instructions":"提示"}"""), _ => throw new InvalidOperationException());
        Assert.Equal("deferred", tool.Exposure); Assert.Equal("标题", tool.Description); Assert.Equal("s/t", tool.Label);
        Assert.Equal("object", tool.ParameterSchema.GetProperty("type").GetString()); Assert.Empty(tool.ParameterSchema.GetProperty("properties").EnumerateObject());
        Assert.Equal("x", tool.ParameterSchema.GetProperty("required")[0].GetString());
        Assert.True(tool.Annotations!.Value.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tool.Annotations.Value.TryGetProperty("destructiveHint", out _));
        Assert.Equal("number", tool.OutputSchema!.Value.GetProperty("properties").GetProperty("structuredContent").GetProperty("properties").GetProperty("x").GetProperty("type").GetString());
    }

    /// <summary>【CodingAgent】【配置模板测试】有效引用、转义、字面量和缺失变量遵循上游语义。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpConfigurationValuesSupportTemplatesEscapesAndMissingVariables()
    {
        var resolver = new CodingAgentConfigValueResolver(Path.GetTempPath());
        var env = new Dictionary<string, string> { ["TAU_MCP_TEST_A"] = "one", ["TAU_MCP_TEST_B"] = "two" };
        Assert.Equal(" one:two:$:!:${invalid-name}:$9 ", await resolver.ResolveAsync(" $TAU_MCP_TEST_A:${TAU_MCP_TEST_B}:$$:$!:${invalid-name}:$9 ", env));
        Assert.Equal(["TAU_MCP_TEST_A", "TAU_MCP_TEST_B"], CodingAgentConfigValueResolver.GetEnvironmentNames("$TAU_MCP_TEST_A${TAU_MCP_TEST_B}$TAU_MCP_TEST_A"));
        Assert.Empty(CodingAgentConfigValueResolver.GetEnvironmentNames("!echo $SECRET"));
        Assert.Equal("TAU_MCP_TEST_A", await resolver.ResolveAsync("TAU_MCP_TEST_A", env));
        var missing = "TAU_MCP_MISSING_" + Guid.NewGuid().ToString("N");
        Assert.Null(await resolver.ResolveAsync("Bearer $" + missing));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveOrThrowAsync("Bearer $" + missing, "authorization header"));
        Assert.Contains(missing, exception.Message);
    }

    /// <summary>【CodingAgent】【配置命令测试】并发只执行一次，取消等待不会取消共享命令，失败也进入缓存。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpConfigurationCommandCacheSurvivesCallerCancellationAndCachesFailures()
    {
        var calls = 0; var released = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new CodingAgentConfigValueResolver(Path.GetTempPath(), (_, _) => { Interlocked.Increment(ref calls); return released.Task; });
        using var cancel = new CancellationTokenSource();
        var first = resolver.ResolveAsync("!command", token: cancel.Token);
        var second = resolver.ResolveAsync("!command");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        released.SetResult("  resolved\n");
        Assert.Equal("resolved", await second); Assert.Equal("resolved", await resolver.ResolveAsync("!command")); Assert.Equal(1, calls);
        var failed = new CodingAgentConfigValueResolver(Path.GetTempPath(), (_, _) => { Interlocked.Increment(ref calls); throw new IOException("sensitive value"); });
        Assert.Null(await failed.ResolveAsync("!secret command")); Assert.Null(await failed.ResolveAsync("!secret command")); Assert.Equal(2, calls);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.ResolveOrThrowAsync("!secret command", "MCP token"));
        Assert.DoesNotContain("secret", exception.Message); Assert.DoesNotContain("sensitive", exception.Message);
    }

    /// <summary>【CodingAgent】【测试 JSON】创建不依赖反射的独立数据。</summary><param name="json">JSON 文本。</param><returns>独立元素。</returns>
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
