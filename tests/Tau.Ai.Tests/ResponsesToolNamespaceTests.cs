// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed partial class ResponsesGrammarTests
{
    /// <summary>【AI】【工具命名空间】四种传输保留初始和终态 namespace，仅同模型回放。</summary>
    /// <param name="protocol">传输路径。</param><param name="custom">是否为语法工具。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("azure", false)]
    [InlineData("azure", true)]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("codex-ws", false)]
    [InlineData("codex-ws", true)]
    public async Task NamespaceSurvivesStreamAndSameModelReplay(string protocol, bool custom)
    {
        foreach (var (initial, final, expected) in new (string?, string?, string?)[]
        {
            ("first", "final", "final"), ("first", null, "first"), (null, "late", "late"), ("first", "", ""), (null, null, null)
        })
        {
            var tool = custom ? GrammarTool() : GrammarTool() with { ConstrainedSampling = null };
            var context = new LlmContext(null, [new UserMessage("run")], [tool]);
            var capture = await SendAsync(protocol, context, Enabled(), events:
            [NamespaceItem("added", custom, initial), NamespaceItem("done", custom, final), Terminal]);
            Assert.Equal(StopReason.ToolUse, capture.Result.StopReason);
            var call = Assert.IsType<ToolCallContent>(Assert.Single(capture.Result.Content));
            Assert.Equal(expected, call.Namespace);
            var start = Assert.Single(capture.Events.OfType<ToolCallStartEvent>());
            Assert.Equal(initial, Assert.IsType<ToolCallContent>(start.Partial.Content[start.ContentIndex]).Namespace);
            Assert.Equal(expected, Assert.Single(capture.Events.OfType<ToolCallEndEvent>()).ToolCall!.Namespace);

            // 1. 【AI】【命名空间边界】完成消息在同模型、换模型、换提供方和换协议时使用不同回放策略
            foreach (var source in new[] { capture.Result, capture.Result with { Model = "other" },
                capture.Result with { Provider = "other" }, capture.Result with { Api = "anthropic-messages" } })
            {
                var request = Body(await SendAsync(protocol, context with { Messages = [source, new ToolResultMessage(call.Id, [new TextContent("ok")])] }, Enabled()));
                var replay = Assert.Single(request.GetProperty("input").EnumerateArray(), item => Type(item) == (custom ? "custom_tool_call" : "function_call"));
                var same = ReferenceEquals(source, capture.Result);
                Assert.Equal(same && expected is not null, replay.TryGetProperty("namespace", out var value));
                if (same && expected is not null) Assert.Equal(expected, value.GetString());
            }
        }
    }

    /// <summary>【AI】【事件夹具】创建带可选命名空间的函数或语法输出项。</summary>
    /// <param name="stage">added 或 done。</param><param name="custom">语法标记。</param><param name="toolNamespace">可选空间。</param>
    /// <returns>供应商 JSON 事件。</returns>
    private static string NamespaceItem(string stage, bool custom, string? toolNamespace)
    {
        var item = new Dictionary<string, object>
        {
            ["type"] = custom ? "custom_tool_call" : "function_call", ["id"] = custom ? "ctc_1" : "fc_1",
            ["call_id"] = "call_1", ["name"] = "execute", [custom ? "input" : "arguments"] = custom ? "print(1)" : "{}"
        };
        if (toolNamespace is not null) item["namespace"] = toolNamespace;
        return JsonSerializer.Serialize(new Dictionary<string, object>
        { ["type"] = "response.output_item." + stage, ["output_index"] = 0, ["item"] = item }, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
    }
}
