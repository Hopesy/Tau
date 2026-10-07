// 作者：xxx
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【文件修改回归】验证原生参数、无损编辑、原文批处理、标准补丁及取消队列。</summary>
public sealed class CodingAgentFileMutationTests
{
    /// <summary>【CodingAgent】【参数兼容】容忍模型产生的单对象、字符串及旧顶层参数。</summary>
    /// <param name="json">原始调用参数。</param><param name="count">规范后的替换数量。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("""{"path":"a","edits":{"oldText":"a","newText":"b"}}""", 1)]
    [InlineData("""{"path":"a","edits":"[{\"oldText\":\"a\",\"newText\":\"b\"}]"}""", 1)]
    [InlineData("""{"path":"a","edits":"{\"oldText\":\"a\",\"newText\":\"b\"}"}""", 1)]
    [InlineData("""{"path":"a","oldText":"a","newText":"b","edits":[{"oldText":"c","newText":"d"}]}""", 2)]
    [InlineData("""{"path":"a","old_string":"a","new_string":"b"}""", 1)]
    public async Task Arguments_NormalizeNativeAndLegacyShapes(string json, int count)
    {
        using var raw = JsonDocument.Parse(json);
        var prepared = await new EditFileTool().PrepareArgumentsAsync(raw.RootElement);
        Assert.Equal(count, prepared.GetProperty("edits").GetArrayLength());
        Assert.False(prepared.TryGetProperty("oldText", out _));
        Assert.False(prepared.TryGetProperty("old_string", out _));
        Assert.Equal(json, raw.RootElement.GetRawText());
    }

    /// <summary>【CodingAgent】【编辑保真】保留 BOM 和 CRLF，只规范实际触及的行，所有匹配基于原文。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Edit_PreservesBomCrlfAndUntouchedUnicodeWhitespace()
    {
        using var fixture = new Fixture();
        var original = "\uFEFFkeep ‘smart’\t \r\nfirst “Ａ”\r\nsecond target\r\nkeep — tail  \r\n";
        await File.WriteAllBytesAsync(fixture.File, Encoding.UTF8.GetBytes(original));
        var result = await new EditFileTool(fixture.Root).ExecuteAsync("edit", Args("file.txt",
            new("first \"A\"", "changed\nextra"), new("second target", "first “Ａ”")));
        Assert.False(result.IsError, Text(result));
        Assert.Equal("\uFEFFkeep ‘smart’\t \r\nchanged\r\nextra\r\nfirst “Ａ”\r\nkeep — tail  \r\n", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.File)));
        var details = Assert.IsType<CodingAgentEditDetails>(result.Details);
        Assert.Equal(2, details.FirstChangedLine);
        Assert.Contains("+2 changed", details.Diff);
        Assert.Contains("--- file.txt\n+++ file.txt\n", details.Patch);
    }

    /// <summary>【CodingAgent】【原文匹配】替换列表不能依赖同一次调用较早替换产生的新内容。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Edit_MatchesAllBlocksAgainstOriginal()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.File, "first\nsecond\n");
        var result = await new EditFileTool(fixture.Root).ExecuteAsync("edit", Args("file.txt", new("first", "second"), new("second", "last")));
        Assert.False(result.IsError, Text(result));
        Assert.Equal("second\nlast\n", await File.ReadAllTextAsync(fixture.File));
    }

    /// <summary>【CodingAgent】【拒绝错误修改】不唯一、重叠、空匹配、找不到和无实际改动均不写入文件。</summary>
    /// <param name="content">原文件。</param><param name="json">替换参数。</param><param name="error">预期错误片段。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("same\nsame", """[{"oldText":"same","newText":"new"}]""", "2 occurrences")]
    [InlineData("‘a’\n'a'", """[{"oldText":"'a'","newText":"new"}]""", "2 occurrences")]
    [InlineData("abcdef", """[{"oldText":"abcd","newText":"a"},{"oldText":"cdef","newText":"b"}]""", "overlap")]
    [InlineData("abc", """[{"oldText":"","newText":"new"}]""", "must not be empty")]
    [InlineData("abc", """[{"oldText":"xyz","newText":"new"}]""", "Could not find")]
    [InlineData("abc", """[{"oldText":"abc","newText":"abc"}]""", "No changes made")]
    [InlineData("abc", "[]", "at least one")]
    public async Task Edit_InvalidBlocksLeaveFileUnchanged(string content, string json, string error)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.File, content);
        using var arguments = JsonDocument.Parse("{\"path\":\"file.txt\",\"edits\":" + json + "}");
        var result = await new EditFileTool(fixture.Root).ExecuteAsync("invalid", arguments.RootElement);
        Assert.True(result.IsError);
        Assert.Contains(error, Text(result));
        Assert.Equal(content, await File.ReadAllTextAsync(fixture.File));
    }

    /// <summary>【CodingAgent】【修改队列】取消正在写入的工具也要等后端完成，后续编辑才能读取新内容。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Queue_CancelledWriteHoldsFileUntilBackendFinishes()
    {
        using var fixture = new Fixture();
        var backend = new BlockingBackend();
        using var cancellation = new CancellationTokenSource();
        var write = new WriteFileTool(fixture.Root, backend).ExecuteAsync("write", WriteArgs("file.txt", "first"), cancellation.Token);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var edit = new EditFileTool(fixture.Root, backend).ExecuteAsync("edit", Args("./file.txt", new CodingAgentEdit("first", "second")));
        Assert.False(write.IsCompleted);
        Assert.False(edit.IsCompleted);
        Assert.Equal(0, backend.Reads);
        backend.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.False((await edit).IsError);
        Assert.Equal("second", backend.Content);
        Assert.Equal(1, backend.Reads);
    }

    /// <summary>【CodingAgent】【队列隔离】不同文件可并发修改，失败后同文件队列仍可继续执行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Queue_DifferentFilesProceedAndFailuresReleaseNext()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CodingAgentFileMutations.RunAsync<int>(fixture.File, async () => { entered.SetResult(); await release.Task; throw new IOException("expected"); });
        await entered.Task;
        var second = CodingAgentFileMutations.RunAsync(fixture.File, () => Task.FromResult(2));
        Assert.Equal(3, await CodingAgentFileMutations.RunAsync(Path.Combine(fixture.Root, "other"), () => Task.FromResult(3)));
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Equal(2, await second);
    }

    /// <summary>【CodingAgent】【新文件写入】父目录自动创建，按 UTF-8 写入并返回原生成功消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Write_CreatesParentsAndWritesUtf8WithoutImplicitBom()
    {
        using var fixture = new Fixture();
        var result = await new WriteFileTool(fixture.Root).ExecuteAsync("write", WriteArgs("sub/new.txt", "中文😀"));
        Assert.Equal("Successfully wrote to sub/new.txt", Text(result));
        Assert.Equal(Encoding.UTF8.GetBytes("中文😀"), await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "sub", "new.txt")));
    }

    /// <summary>【CodingAgent】【补丁验证】使用 Git 实际应用随机增删、重复行、空文件和末行换行变化的补丁。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task EditDiff_PatchesApplyToExactExpectedContent()
    {
        using var fixture = new Fixture();
        var random = new Random(6401);
        var patches = new StringBuilder();
        var expected = new Dictionary<string, string>();
        for (var index = 0; index < 100; index++)
        {
            var beforeLines = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "line-" + random.Next(0, 7)).ToArray();
            var afterLines = beforeLines.ToList();
            for (var change = 0; change < 8; change++)
            {
                if (afterLines.Count > 0 && random.Next(2) == 0) afterLines.RemoveAt(random.Next(afterLines.Count));
                else afterLines.Insert(random.Next(afterLines.Count + 1), "added-" + random.Next(0, 7));
            }
            var before = string.Join('\n', beforeLines) + (random.Next(2) == 0 ? "\n" : "");
            var after = string.Join('\n', afterLines) + (random.Next(2) == 0 ? "\n" : "");
            if (before == after) continue;
            var name = $"case-{index}.txt";
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, name), before);
            expected.Add(name, after);
            var details = CodingAgentEditDiff.Create(name, before, after);
            Assert.NotNull(details.FirstChangedLine);
            patches.Append(details.Patch);
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "changes.patch"), patches.ToString());
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = fixture.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-c", "core.autocrlf=false", "apply", "-p0", "--", "changes.patch" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error + await output);
        foreach (var file in expected) Assert.Equal(file.Value, await File.ReadAllTextAsync(Path.Combine(fixture.Root, file.Key)));
    }

    /// <summary>【CodingAgent】【测试参数】创建原生数组形式的编辑参数。</summary>
    /// <param name="path">文件路径。</param><param name="edits">替换列表。</param><returns>工具参数。</returns>
    private static JsonElement Args(string path, params CodingAgentEdit[] edits)
    {
        var array = new JsonArray();
        foreach (var edit in edits) array.Add((JsonNode)new JsonObject { ["oldText"] = edit.OldText, ["newText"] = edit.NewText });
        return JsonDocument.Parse(new JsonObject { ["path"] = path, ["edits"] = array }.ToJsonString()).RootElement.Clone();
    }

    /// <summary>【CodingAgent】【写入参数】创建写入参数。</summary>
    /// <param name="path">文件路径。</param><param name="content">文件内容。</param><returns>工具参数。</returns>
    private static JsonElement WriteArgs(string path, string content) => JsonDocument.Parse(new JsonObject { ["path"] = path, ["content"] = content }.ToJsonString()).RootElement.Clone();

    /// <summary>【CodingAgent】【测试文本】提取工具结果文本。</summary>
    /// <param name="result">工具结果。</param><returns>文本。</returns>
    private static string Text(ToolResult result) => string.Join('\n', result.Content.OfType<TextContent>().Select(block => block.Text));

    /// <summary>【CodingAgent】【测试目录】隔离文件修改，结束时清理本次临时目录。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-edit-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "file.txt");
        /// <summary>【CodingAgent】【目录创建】创建测试目录。</summary>
        public Fixture() => Directory.CreateDirectory(Root);
        /// <summary>【CodingAgent】【目录清理】删除仅属于本次测试的目录。</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    /// <summary>【CodingAgent】【测试后端】仅阻塞第一次写入，验证真实后端任务完成前不能释放队列。</summary>
    private sealed class BlockingBackend : ICodingAgentEditOperations, ICodingAgentWriteOperations
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public string Content { get; private set; } = "";
        private int _writes;
        /// <summary>【CodingAgent】【模拟读取】返回当前后端内容。</summary>
        /// <param name="path">文件路径。</param><returns>当前字节。</returns>
        public Task<byte[]> ReadFileAsync(string path) { Reads++; return Task.FromResult(Encoding.UTF8.GetBytes(Content)); }
        /// <summary>【CodingAgent】【模拟写入】第一次写入等待测试放行。</summary>
        /// <param name="path">文件路径。</param><param name="content">文本。</param><returns>完整写入任务。</returns>
        public async Task WriteFileAsync(string path, string content)
        {
            if (_writes++ == 0) { Started.SetResult(); await Release.Task; }
            Content = content;
        }
        /// <summary>【CodingAgent】【模拟访问】允许读写测试文件。</summary>
        /// <param name="path">文件路径。</param><returns>已完成任务。</returns>
        public Task AccessAsync(string path) => Task.CompletedTask;
        /// <summary>【CodingAgent】【模拟目录】远程目录已经存在。</summary>
        /// <param name="path">目录路径。</param><returns>已完成任务。</returns>
        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
    }
}
