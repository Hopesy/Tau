// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentCodeModeSandboxTests
{
    /// <summary>【CodingAgent】【脚本选项测试】保留行号和安全整数，拒绝未知字段、非法期限和空代码。</summary>
    /// <param name="input">非法源码。</param>
    [Theory]
    [InlineData("")]
    [InlineData("// @options: {}")]
    [InlineData("// @options: []\nreturn 1")]
    [InlineData("// @options: {\"extra\":1}\nreturn 1")]
    [InlineData("// @options: {\"timeout_ms\":0}\nreturn 1")]
    [InlineData("// @options: {\"timeout_ms\":2147483648}\nreturn 1")]
    [InlineData("// @options: {\"max_output_tokens\":9007199254740992}\nreturn 1")]
    [InlineData("// @options: {\"max_output_tokens\":1.5}\nreturn 1")]
    public void CodeModeSourceRejectsInvalidOptions(string input) => Assert.Throws<ArgumentException>(() => CodingAgentCodeModeSource.Parse(input));

    /// <summary>【CodingAgent】【脚本值测试】验证输出、返回值、状态副本、删除以及引擎全局隔离。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeSandboxKeepsOutputsAndJsonStoreWithoutExposingClr()
    {
        var source = CodingAgentCodeModeSource.Parse("""
            // @options: {"max_output_tokens":0,"timeout_ms":5000.0}
            for (const name of ['process','require','fetch','setTimeout','System','importNamespace','__tau_codemode_bridge']) {
              if (typeof globalThis[name] !== 'undefined') throw Error('host global leaked: '+name);
            }
            const value=load('old'); value.x=99;
            if(load('old').x!==1) throw Error('shared store value');
            store('saved',{answer:42}); store('old',undefined);
            text('中文'); console.info({value:1}); text(undefined);
            return {saved:load('saved'),deleted:load('old')};
            """);
        Assert.StartsWith("\n", source.Code); Assert.Equal(0, source.MaxOutputTokens); Assert.Equal(5000, source.TimeoutMilliseconds);
        var initial = new JsonObject { ["old"] = new JsonObject { ["x"] = 1 } };
        var result = await CodingAgentCodeModeSandbox.ExecuteAsync(source, store: initial);
        Assert.True(result.Success, result.Error);
        Assert.Equal(["中文", "{\"value\":1}", "undefined"], result.Output.OfType<TextContent>().Select(text => text.Text));
        Assert.Equal(42, result.Value!.Value.GetProperty("saved").GetProperty("answer").GetInt32());
        Assert.Equal(42, result.Set["saved"]!["answer"]!.GetValue<int>()); Assert.Equal(["old"], result.Delete);
        Assert.Equal(1, initial["old"]!["x"]!.GetValue<int>());
    }

    /// <summary>【CodingAgent】【脚本并发测试】并发宿主调用乱序完成，参数和结果通过 JSON，错误可被脚本捕获。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeSandboxSupportsConcurrentToolsGlobalsAndRejectedCalls()
    {
        var active = 0; var maximum = 0;
        CodingAgentCodeModeFunction[] functions =
        [
            new("echo-value", "echo", async (args, token) =>
            {
                var count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
                try { await Task.Delay(args!.Value.GetProperty("delay").GetInt32(), token); return args; }
                finally { Interlocked.Decrement(ref active); }
            }),
            new("fail", "fails", (_, _) => throw new InvalidOperationException("expected failure")),
            new("models.echo", "global", (args, _) => Task.FromResult(args), true, true)
        ];
        var result = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("""
            const results=await Promise.allSettled([tools.echo_value({delay:40,value:1}),tools['echo-value']({delay:10,value:2}),tools.fail({})]);
            text(results.map(r=>r.status));
            if(results[2].reason.message!=='expected failure')throw Error('wrong rejection');
            if(ALL_TOOLS[0].name!=='echo_value')throw Error('wrong identifier');
            const reflected=tools.echo_value.GetType;
            if(reflected!==undefined)throw Error('CLR reflection escaped');
            const global=await models.echo('a',{b:2});
            return {values:results.slice(0,2).map(r=>r.value.value),global};
            """), functions);
        Assert.True(result.Success, result.Error); Assert.Equal(2, maximum);
        Assert.Equal([1, 2], result.Value!.Value.GetProperty("values").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal("a", result.Value.Value.GetProperty("global")[0].GetString()); Assert.Equal(0, active);
    }

    /// <summary>【CodingAgent】【脚本结束测试】exit 保留成功写入，返回后取消未 await 的宿主调用，失败不提交状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeSandboxCancelsDetachedCallsAndCommitsOnlySuccessfulWrites()
    {
        var cancelled = false;
        var function = new CodingAgentCodeModeFunction("hold", "hang", async (_, token) =>
        { try { await Task.Delay(Timeout.Infinite, token); return null; } finally { cancelled = token.IsCancellationRequested; } });
        var result = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("tools.hold({}); store('x',1); text('before'); exit(); text('after');"), [function]);
        Assert.True(result.Success, result.Error); Assert.True(cancelled); Assert.Equal(1, result.Set["x"]!.GetValue<int>());
        Assert.Equal("before", Assert.IsType<TextContent>(Assert.Single(result.Output)).Text);
        var failed = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("text('partial'); store('x',2); throw Error('failure');"));
        Assert.False(failed.Success); Assert.Empty(failed.Set); Assert.Contains("failure", failed.Error);
        Assert.Equal("partial", Assert.IsType<TextContent>(Assert.Single(failed.Output)).Text);
    }

    /// <summary>【CodingAgent】【脚本停止测试】死循环、永不完成 Promise 和取消均能退出，不留下工具任务。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeSandboxStopsCpuLoopsStalledPromisesAndCancellation()
    {
        var looping = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("// @options: {\"timeout_ms\":50}\nwhile(true){}" )).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(looping.Success); Assert.Contains("timed out", looping.Error);
        var stalled = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("await new Promise(()=>{});" )).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stalled.Success); Assert.Contains("can never settle", stalled.Error);
        using var token = new CancellationTokenSource(); token.Cancel();
        var cancelled = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("return 1"), token: token.Token);
        Assert.False(cancelled.Success); Assert.Contains("cancelled", cancelled.Error);
    }

    /// <summary>【CodingAgent】【脚本图像测试】图像按文件头识别类型，远程 URL 和无效数据失败，store 拒绝超大值。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeSandboxValidatesImageSignaturesAndStoreBounds()
    {
        var valid = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse("image({type:'image',mimeType:'image/jpeg',data:'iVBORw0KGgo='});"));
        Assert.True(valid.Success, valid.Error); Assert.Equal("image/png", Assert.IsType<ImageContent>(Assert.Single(valid.Output)).MimeType);
        foreach (var code in new[] { "image('https://test/image.png')", "image('data:image/png;base64,aGVsbG8=')", "store('large','x'.repeat(262145))", "store(3,1)" })
        { var failed = await CodingAgentCodeModeSandbox.ExecuteAsync(CodingAgentCodeModeSource.Parse(code)); Assert.False(failed.Success); Assert.NotEmpty(failed.Error!); }
        Assert.Equal("__tool_$", CodingAgentCodeModeSandbox.Identifier("😀-tool-$"));
    }
}
