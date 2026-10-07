// 作者：xxx
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【命令回归】覆盖真实 Shell、取消、双输出限制及自定义后端生命周期。</summary>
public sealed class CodingAgentShellToolTests
{
    /// <summary>【CodingAgent】【真实 Bash】验证 Bash 语法、Unicode、引号、双管道及非零退出结果。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Bash_ExecutesNativeSyntaxAndPreservesStructuredFailure()
    {
        var result = await new ShellTool().ExecuteAsync("bash", Args("""printf '%s' '你好😀"'; printf '%s' "$((2 + 3))"; printf 'err' >&2; exit 7"""));
        Assert.True(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal(7, structured.GetProperty("exit_code").GetInt32());
        Assert.Contains("你好😀\"", structured.GetProperty("output").GetString());
        Assert.Contains("5", structured.GetProperty("output").GetString());
        Assert.Contains("err", structured.GetProperty("output").GetString());
        Assert.Contains("Command exited with code 7", Text(result));
        Assert.False(structured.GetProperty("truncated").GetBoolean());
    }

    /// <summary>【CodingAgent】【真实 PowerShell】验证 Windows 原生 PowerShell 中文输出与退出状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task PowerShell_UsesNativeEncodingAndPlatformContract()
    {
        var result = await new PowerShellTool().ExecuteAsync("ps", Args("[Console]::Write('中文😀'); exit 3"));
        Assert.True(result.IsError);
        if (!OperatingSystem.IsWindows()) { Assert.Contains("only available on Windows", Text(result)); return; }
        Assert.Equal("中文😀", result.StructuredContent!.Value.GetProperty("output").GetString());
        Assert.Equal(3, result.StructuredContent.Value.GetProperty("exit_code").GetInt32());
    }

    /// <summary>【CodingAgent】【实际取消】收到真实子进程 PID 后取消，并验证 Bash 及其子进程均已结束。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Bash_CancellationRetainsOutputAndTerminatesChild()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var updates = new List<ToolUpdate>();
        var result = await new ShellTool().ExecuteAsync("cancel", Args("sleep 30 & child=$!; ps -p $$; ps -p $child; printf 'child:%s\n' \"$child\"; wait"), cancellation.Token, update =>
        {
            updates.Add(update);
            if (update.Text.Contains("child:")) cancellation.Cancel();
            return Task.CompletedTask;
        });
        Assert.True(result.IsError);
        Assert.Empty(updates[0].Content!);
        Assert.Contains("child:", Text(result));
        Assert.Contains("Command aborted", Text(result));
        Assert.DoesNotContain("timed out", Text(result));
        Assert.Null(result.StructuredContent);
        var pid = Text(result).Split("child:")[1].Split('\n')[0].Trim();
        var probe = await new ShellTool().ExecuteAsync("probe", Args($"for i in {{1..50}}; do kill -0 {pid} 2>/dev/null || {{ printf gone; exit; }}; sleep 0.1; done; ps -p {pid}; printf alive"));
        Assert.True(probe.StructuredContent!.Value.GetProperty("output").GetString() == "gone", Text(result) + "\nPROBE\n" + Text(probe));
    }

    /// <summary>【CodingAgent】【超时区别】秒数与旧毫秒参数均触发超时，保留超时前的输出。</summary>
    /// <param name="legacy">是否使用旧毫秒参数。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_IsExplicitAndRetainsPartialOutput(bool legacy)
    {
        var backend = new Operations(async (_, data, token) => { await data("before-timeout"); await Task.Delay(Timeout.Infinite, token); return 0; });
        var tool = new ShellTool(options: new() { Operations = backend });
        var args = JsonDocument.Parse(legacy ? """{"command":"ignored","timeout_ms":100}""" : """{"command":"ignored","timeout":0.1}""").RootElement;
        var result = await tool.ExecuteAsync("timeout", args);
        Assert.True(result.IsError);
        Assert.Contains("before-timeout", Text(result));
        Assert.Contains("Command timed out after 0.1 seconds", Text(result));
    }

    /// <summary>【CodingAgent】【超时边界】非法超时在启动后端前被拒绝。</summary>
    /// <param name="value">超时秒数。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483.648)]
    public async Task Timeout_RejectsInvalidSeconds(double value)
    {
        var tool = new ShellTool(options: new() { Operations = new Operations((_, _, _) => throw new InvalidOperationException("must not run")) });
        var args = JsonDocument.Parse(new JsonObject { ["command"] = "unused", ["timeout"] = value }.ToJsonString()).RootElement;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tool.ExecuteAsync("invalid", args));
    }

    /// <summary>【CodingAgent】【双层截断】验证模型尾部、程序前缀及磁盘完整输出独立，UTF-8 截断不破坏字符。</summary>
    /// <param name="large">是否超过程序输出限制。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_UsesIndependentLimitsAndPreservesFullFile(bool large)
    {
        var raw = large ? new string('x', 1024 * 1024 - 1) + "😀尾部" : string.Concat(Enumerable.Repeat("中文行\n", 2300));
        var tool = new ShellTool(options: new() { Operations = new Operations(async (_, data, _) => { await data(raw); return 0; }) });
        var result = await tool.ExecuteAsync("large", Args("unused"));
        var details = Assert.IsType<ShellToolDetails>(result.Details);
        try
        {
            Assert.True(details.Truncation!.Truncated);
            Assert.True(details.Truncation.OutputBytes <= 50 * 1024);
            Assert.True(details.Truncation.OutputLines <= 2000);
            Assert.Equal(Encoding.UTF8.GetByteCount(raw), details.Truncation.TotalBytes);
            Assert.Equal(raw, await File.ReadAllTextAsync(details.FullOutputPath!));
            var structured = result.StructuredContent!.Value;
            Assert.Equal(large, structured.GetProperty("truncated").GetBoolean());
            Assert.Equal(large ? new string('x', 1024 * 1024 - 1) : raw, structured.GetProperty("output").GetString());
            Assert.Equal(large, structured.TryGetProperty("full_output_path", out _));
            Assert.DoesNotContain("�", Text(result));
        }
        finally { File.Delete(details.FullOutputPath!); }
    }

    /// <summary>【CodingAgent】【后端生命周期】输出在等待中的命令执行期间可见，结束后迟到数据被忽略。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Updates_StreamBeforeCompletionAndIgnoreLateData()
    {
        var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, Task>? late = null;
        var backend = new Operations(async (_, data, token) =>
        {
            late = data;
            await data("partial");
            await visible.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await data(" final");
            return 0;
        });
        var updates = new List<ToolUpdate>();
        var result = await new ShellTool(options: new() { Operations = backend }).ExecuteAsync("stream", Args("unused"), onUpdate: update =>
        {
            updates.Add(update);
            if (update.Text == "partial") visible.TrySetResult();
            return Task.CompletedTask;
        });
        Assert.Equal("partial final", Text(result));
        var count = updates.Count;
        await late!("late");
        Assert.Equal(count, updates.Count);
        Assert.Equal("partial final", updates[^1].Text);
    }

    /// <summary>【CodingAgent】【启动钩子】前缀、目录和环境在启动前可调整，关闭元数据时移除继承会话信息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SpawnHook_SeesPrefixAndCleanEnvironment()
    {
        CodingAgentShellSpawnContext? received = null;
        var tool = new ShellTool(options: new()
        {
            ExposeSessionEnvironment = false, CommandPrefix = "prefix",
            SpawnHook = context => context with { Command = context.Command + "\nhook", Environment = new Dictionary<string, string?>(context.Environment) { ["HOOK"] = "yes" } },
            Operations = new Operations((context, _, _) => { received = context; return Task.FromResult<int?>(0); })
        });
        tool.BindSession(() => new(), () => new Dictionary<string, string?> { ["PI_MODEL"] = "hidden" });
        Assert.Equal("(no output)", Text(await tool.ExecuteAsync("hook", Args("command"))));
        Assert.Equal("prefix\ncommand\nhook", received!.Command);
        Assert.Equal("yes", received.Environment["HOOK"]);
        Assert.False(received.Environment.ContainsKey("PI_MODEL"));
        Assert.Empty(tool.PromptGuidelines);
    }

    /// <summary>【CodingAgent】【测试参数】构造包含任意命令文本的参数。</summary>
    /// <param name="command">命令。</param><returns>独立 JSON 参数。</returns>
    private static JsonElement Args(string command) => JsonDocument.Parse(new JsonObject { ["command"] = command }.ToJsonString()).RootElement.Clone();

    /// <summary>【CodingAgent】【测试文本】连接工具返回的文本块。</summary>
    /// <param name="result">工具结果。</param><returns>文本。</returns>
    private static string Text(ToolResult result) => string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    /// <summary>【CodingAgent】【输出失败】进度接收器失败时取消正在等待的后端并传播原始错误。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task UpdateFailure_CancelsBackendAndPropagatesOriginalError()
    {
        var stopped = false;
        var backend = new Operations(async (_, data, token) =>
        {
            await data("partial");
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
            return 0;
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new ShellTool(options: new() { Operations = backend })
            .ExecuteAsync("callback", Args("unused"), onUpdate: update => update.Text.Length == 0 ? Task.CompletedTask : throw new InvalidOperationException("receiver failed")));
        Assert.Equal("receiver failed", failure.Message);
        Assert.True(stopped);
    }

    /// <summary>【CodingAgent】【字符管道】跨多个读取块接收非 BMP 字符时，完整文件及字节统计不产生替代字符。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Bash_LargeUnicodeOutputPreservesCharacterBoundaries()
    {
        var result = await new ShellTool().ExecuteAsync("unicode", Args("for ((i=0;i<16000;i++)); do printf '中😀'; done"));
        Assert.False(result.IsError);
        var details = Assert.IsType<ShellToolDetails>(result.Details);
        try
        {
            var expected = string.Concat(Enumerable.Repeat("中😀", 16000));
            Assert.Equal(expected, await File.ReadAllTextAsync(details.FullOutputPath!));
            Assert.Equal(expected, result.StructuredContent!.Value.GetProperty("output").GetString());
            Assert.Equal(112000, details.Truncation!.TotalBytes);
            Assert.DoesNotContain("�", Text(result));
        }
        finally { File.Delete(details.FullOutputPath!); }
    }

    /// <summary>【CodingAgent】【宿主 Bash】RPC 执行器保留流标签，并使用自身目录、前缀及元数据。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task HostRunner_UsesBashContextAndStreamNames()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "tau-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            File.WriteAllText(Path.Combine(cwd, "probe.txt"), "directory-marker");
            var runner = new SystemCodingAgentShellRunner(cwd, () => new() { CommandPrefix = "export PREFIX=ok" },
                () => new Dictionary<string, string?> { ["PI_MODEL"] = "host-model" });
            var events = new List<CodingAgentShellEvent>();
            var result = await runner.ExecuteAsync("cat probe.txt; printf '%s' \"$PREFIX/$PI_MODEL\"; printf error >&2; exit 4", new ProgressSink(events.Add));
            Assert.Equal(4, result.ExitCode);
            Assert.False(result.Cancelled);
            Assert.Contains("directory-marker", result.Output);
            Assert.Contains("ok/host-model", result.Output);
            Assert.Contains(events, item => item.Stream == "stdout" && item.Text.Contains("directory-marker"));
            Assert.Contains(events, item => item.Stream == "stderr" && item.Text.Contains("error"));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    /// <summary>【CodingAgent】【测试进度】同步保存命令事件，避免线程池进度通知晚于断言。</summary>
    private sealed class ProgressSink(Action<CodingAgentShellEvent> receive) : IProgress<CodingAgentShellEvent>
    {
        /// <summary>【CodingAgent】【事件接收】立即转发当前事件。</summary>
        /// <param name="value">命令事件。</param>
        public void Report(CodingAgentShellEvent value) => receive(value);
    }

    /// <summary>【CodingAgent】【测试后端】注入可控命令行为，无需外部模型或网络。</summary>
    private sealed class Operations(Func<CodingAgentShellSpawnContext, Func<string, Task>, CancellationToken, Task<int?>> execute) : ICodingAgentShellOperations
    {
        /// <summary>【CodingAgent】【测试执行】转发上下文、输出回调和取消信号。</summary>
        /// <param name="context">上下文。</param><param name="onData">输出回调。</param><param name="token">取消信号。</param><returns>退出码。</returns>
        public Task<int?> ExecuteAsync(CodingAgentShellSpawnContext context, Func<string, Task> onData, CancellationToken token) => execute(context, onData, token);
    }
}

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【会话环境】内置命令使用当前会话目录、模型及可热更新的命令前缀。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Shell_UsesSessionMetadataAndReloadedSettings()
    {
        using var temp = TempDirectory.Create();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), ModelCatalog = CreateModelCatalog(),
            ProviderId = "test-provider", ModelId = "test-model", IncludeExtensions = false
        });
        var tool = Assert.IsType<ShellTool>(session.Runner.GetRegisteredTools().Single(tool => tool.Name == "bash"));
        foreach (var prefix in new[] { "first", "second" })
        {
            session.SettingsStore.Save(session.SettingsStore.Load() with { ShellCommandPrefix = "export TAU_PROBE=" + prefix });
            var result = await tool.ExecuteAsync("env", JsonDocument.Parse(new JsonObject
            { ["command"] = "printf '%s\n' \"$PI_SESSION_ID\" \"$PI_SESSION_FILE\" \"$PI_PROVIDER\" \"$PI_MODEL\" \"$PI_REASONING_LEVEL\" \"$TAU_PROBE\"" }.ToJsonString()).RootElement);
            Assert.False(result.IsError);
            var lines = result.StructuredContent!.Value.GetProperty("output").GetString()!.TrimEnd().Split('\n');
            Assert.Equal(session.Runner.SessionId, lines[0]);
            Assert.Equal(session.TreeSessionController!.Path, lines[1]);
            Assert.Equal("test-provider", lines[2]);
            Assert.Equal("test-model", lines[3]);
            Assert.Equal(CodingAgentThinkingLevels.Format(session.Runner.ThinkingLevel), lines[4]);
            Assert.Equal(prefix, lines[5]);
        }
    }
}
