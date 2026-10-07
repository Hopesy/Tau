// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Mistral;

public sealed partial class MistralProvider
{
    /// <summary>【Mistral】【流式解析】独立处理思考数组、工具交错及结束原因后的用量。</summary>
    private sealed class MistralStreamState
    {
        private readonly Model _model;
        private readonly AssistantMessageStream _stream;
        private AssistantMessage _partial;
        private readonly Dictionary<string, int> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _arguments = [];
        private int? _current;

        /// <summary>【Mistral】【流状态】创建带来源身份和零用量的初始消息。</summary>
        /// <param name="model">模型。</param><param name="api">协议。</param><param name="stream">输出事件流。</param>
        public MistralStreamState(Model model, string api, AssistantMessageStream stream)
        {
            _model = model;
            _stream = stream;
            _partial = new() { Provider = model.Provider, Api = api, Model = model.Id, Content = [],
                Usage = new(0, 0, 0, 0, Cost: default(UsageCost)) { TotalTokens = 0 } };
        }

        /// <summary>【Mistral】【流开始】HTTP 成功后发布开始事件。</summary>
        public void Start() => _stream.Push(new StartEvent(_partial));

        /// <summary>【Mistral】【分片处理】先读取元数据和结束原因，再处理同一分片中的正文及工具。</summary>
        /// <param name="chunk">供应商 JSON 分片。</param>
        public void Process(JsonElement chunk)
        {
            if (string.IsNullOrEmpty(_partial.ResponseId) && String(chunk, "id") is { Length: > 0 } id)
                _partial = _partial with { ResponseId = id };
            if (Property(chunk, "usage") is { ValueKind: JsonValueKind.Object } usage) ApplyUsage(usage);
            if (Property(chunk, "choices") is not { ValueKind: JsonValueKind.Array } choices || choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (String(choice, "finish_reason") is { Length: > 0 } reason)
            {
                var stop = reason switch { "stop" => StopReason.EndTurn, "length" or "model_length" => StopReason.MaxTokens,
                    "tool_calls" => StopReason.ToolUse, _ => StopReason.Error };
                _partial = _partial with { StopReason = stop, RawStopReason = reason, EndTurn = stop == StopReason.EndTurn,
                    ErrorMessage = stop == StopReason.Error ? "Provider stopped with: " + reason : null };
            }
            if (Property(choice, "delta") is not { ValueKind: JsonValueKind.Object } delta) return;
            // 1. 【Mistral】【结构化正文】字符串、text 和 thinking 数组分别累积，空分片不打断当前块
            if (Property(delta, "content") is { } content)
            {
                if (content.ValueKind == JsonValueKind.String) AppendText(content.GetString()!, false);
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String) AppendText(part.GetString()!, false);
                        else if (String(part, "type") == "text") AppendText(String(part, "text") ?? "", false);
                        else if (String(part, "type") == "thinking" && Property(part, "thinking") is { ValueKind: JsonValueKind.Array } thoughts)
                            AppendText(string.Concat(thoughts.EnumerateArray().Select(item => String(item, "text") ?? "")), true);
                    }
            }
            // 2. 【Mistral】【工具增量】调用按 index 优先定位，缺失 ID 使用确定性九字符标识
            if (Property(delta, "tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                foreach (var call in calls.EnumerateArray()) AppendTool(call);
        }

        /// <summary>【Mistral】【文本增量】按内容类型切换块，保留每个块独立生命周期。</summary>
        /// <param name="value">未清理增量。</param><param name="thinking">是否为思考。</param>
        private void AppendText(string value, bool thinking)
        {
            var delta = SanitizeText(value);
            if (delta.Length == 0) return;
            if (_current is not { } current || (_partial.Content[current] is ThinkingContent) != thinking)
            {
                CloseCurrent();
                var blocks = _partial.Content.ToList();
                _current = blocks.Count;
                blocks.Add(thinking ? new ThinkingContent("") : new TextContent(""));
                _partial = _partial with { Content = blocks };
                _stream.Push(thinking ? new ThinkingStartEvent(_current.Value, _partial) : new TextStartEvent(_current.Value, _partial));
            }
            var index = _current.Value;
            Replace(index, thinking ? ((ThinkingContent)_partial.Content[index]) with { Thinking = ((ThinkingContent)_partial.Content[index]).Thinking + delta }
                : ((TextContent)_partial.Content[index]) with { Text = ((TextContent)_partial.Content[index]).Text + delta });
            _stream.Push(thinking ? new ThinkingDeltaEvent(index, delta, _partial) : new TextDeltaEvent(index, delta, _partial));
        }

        /// <summary>【Mistral】【工具分片】追加原始参数并产生可读取的 JSON 快照，终态统一闭合。</summary>
        /// <param name="call">调用分片。</param>
        private void AppendTool(JsonElement call)
        {
            CloseCurrent();
            var function = Property(call, "function") ?? throw new InvalidOperationException("Mistral tool call is missing function");
            var id = String(call, "id");
            var providerIndex = Property(call, "index");
            if (providerIndex is { ValueKind: JsonValueKind.Null }) providerIndex = null;
            if (string.IsNullOrEmpty(id) || id == "null")
                id = MistralToolCallIdNormalizer.Derive("toolcall:" + (providerIndex?.GetRawText() ?? "0"), 0);
            var key = providerIndex is { ValueKind: JsonValueKind.Number } ? "index:" + providerIndex.Value.GetRawText() : "id:" + id;
            if (!_tools.TryGetValue(key, out var index))
            {
                var blocks = _partial.Content.ToList();
                index = blocks.Count;
                blocks.Add(new ToolCallContent(id, String(function, "name") ?? "", "{}"));
                _partial = _partial with { Content = blocks };
                _tools[key] = index;
                _arguments[index] = "";
                _stream.Push(new ToolCallStartEvent(index, _partial, id, String(function, "name")));
            }
            var raw = Property(function, "arguments");
            var delta = raw is { ValueKind: JsonValueKind.String } ? raw.Value.GetString()! : raw?.GetRawText() ?? "{}";
            var accumulated = _arguments[index] + delta;
            _arguments[index] = accumulated;
            Replace(index, ((ToolCallContent)_partial.Content[index]) with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(accumulated) });
            _stream.Push(new ToolCallDeltaEvent(index, delta, _partial));
        }

        /// <summary>【Mistral】【用量】按主线优先级读取缓存计数，并把缓存从输入总量扣除后计费。</summary>
        /// <param name="usage">供应商用量。</param>
        private void ApplyUsage(JsonElement usage)
        {
            var prompt = Number(Property(usage, "prompt_tokens"));
            var output = Number(Property(usage, "completion_tokens"));
            JsonElement? raw = null;
            foreach (var (parent, name) in new[] { ("promptTokensDetails", "cachedTokens"), ("prompt_tokens_details", "cached_tokens"),
                ("promptTokenDetails", "cachedTokens"), ("prompt_token_details", "cached_tokens"), ("", "numCachedTokens"), ("", "num_cached_tokens") })
            {
                var value = parent.Length == 0 ? Property(usage, name) : Property(Property(usage, parent) ?? default, name);
                if (value is null || value.Value.ValueKind == JsonValueKind.Null) continue;
                raw = value;
                break;
            }
            var cached = Math.Clamp(Number(raw), 0, Math.Max(0, prompt));
            var valueUsage = new Usage(Math.Max(0, prompt - cached), output, cached, 0)
            { TotalTokens = Number(Property(usage, "total_tokens")) is var total && total != 0 ? total : prompt + output };
            _partial = _partial with { Usage = valueUsage with { Cost = ModelCatalog.CalculateCost(_model, valueUsage) } };
        }

        /// <summary>【Mistral】【流完成】闭合内容和全部工具，缺少结束原因必须产生错误终态。</summary>
        public void Complete()
        {
            CloseCurrent();
            foreach (var index in _tools.Values)
            {
                var call = ((ToolCallContent)_partial.Content[index]) with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(_arguments[index]) };
                Replace(index, call);
                _stream.Push(new ToolCallEndEvent(index, _partial, call));
            }
            if (_partial.StopReason is null) { Fail("Mistral stream ended without a finish reason"); return; }
            if (_partial.StopReason == StopReason.Error) { Fail(_partial.ErrorMessage ?? "Provider returned an error"); return; }
            _partial = _partial with { Timestamp = DateTimeOffset.UtcNow };
            _stream.Push(new DoneEvent(_partial));
        }

        /// <summary>【Mistral】【流失败】保留已经接收的内容、标识和用量，发送唯一终态。</summary>
        /// <param name="message">错误原因。</param><param name="reason">错误或取消状态。</param>
        public void Fail(string message, StopReason reason = StopReason.Error)
        {
            _partial = _partial with { StopReason = reason, ErrorMessage = message, EndTurn = false, Timestamp = DateTimeOffset.UtcNow };
            _stream.Push(new ErrorEvent(message, _partial, _partial));
        }

        /// <summary>【Mistral】【块完成】切换类型或开始工具调用之前结束当前正文或思考。</summary>
        private void CloseCurrent()
        {
            if (_current is not { } index) return;
            if (_partial.Content[index] is TextContent text) _stream.Push(new TextEndEvent(index, _partial, text.Text));
            else if (_partial.Content[index] is ThinkingContent thinking) _stream.Push(new ThinkingEndEvent(index, _partial, thinking.Thinking));
            _current = null;
        }

        /// <summary>【Mistral】【内容快照】替换单块而不修改已经发布的历史事件。</summary>
        /// <param name="index">内容位置。</param><param name="block">新块。</param>
        private void Replace(int index, ContentBlock block)
        {
            var content = _partial.Content.ToArray();
            content[index] = block;
            _partial = _partial with { Content = content };
        }

        /// <summary>【Mistral】【字段读取】读取可选 JSON 属性。</summary>
        /// <param name="value">对象。</param><param name="name">字段。</param><returns>属性或 null。</returns>
        private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;

        /// <summary>【Mistral】【文本读取】读取字符串字段。</summary>
        /// <param name="value">对象。</param><param name="name">字段。</param><returns>字符串或 null。</returns>
        private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

        /// <summary>【Mistral】【计数读取】读取整数计数，非法类型回退零。</summary>
        /// <param name="value">可选计数。</param><returns>整型计数。</returns>
        private static int Number(JsonElement? value) => value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result) ? result : 0;
    }
}
