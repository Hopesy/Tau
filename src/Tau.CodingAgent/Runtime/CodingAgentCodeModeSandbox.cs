// 作者：xxx
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Jint;
using Jint.Native;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本选项】保留已移除选项行的代码和完整执行期限。</summary>
public sealed record CodingAgentCodeModeSource(string Code, long? MaxOutputTokens, int? TimeoutMilliseconds)
{
    public const string Grammar = "start: options_source | plain_source\noptions_source: OPTIONS_LINE NEWLINE SOURCE\nplain_source: SOURCE\nOPTIONS_LINE: /[ \\t]*\\/\\/ @options:[^\\r\\n]*/\nNEWLINE: /\\r?\\n/\nSOURCE: /[\\s\\S]+/\n";

    /// <summary>【CodingAgent】【脚本源码】校验首行选项且保留行号，拒绝未知选项和无效数值。</summary>
    /// <param name="input">JavaScript 源码。</param><returns>源码及已校验选项。</returns>
    public static CodingAgentCodeModeSource Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Expected JavaScript source text (non-empty). Provide JS only, optionally with a first line `// @options: {\"max_output_tokens\": 1000}`.");
        var newline = input.IndexOf('\n');
        var line = (newline < 0 ? input : input[..newline]).TrimEnd('\r').TrimStart();
        const string prefix = "// @options:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return new(input, null, null);
        var code = newline < 0 ? "" : input[newline..];
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("The @options line must be followed by JavaScript source on subsequent lines");
        JsonDocument document;
        try { document = JsonDocument.Parse(line[prefix.Length..].Trim()); }
        catch (JsonException error) { throw new ArgumentException("@options must be valid JSON with supported fields `max_output_tokens` and `timeout_ms`", error); }
        using (document)
        {
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("@options must be a JSON object with supported fields `max_output_tokens` and `timeout_ms`");
            long? output = null; int? timeout = null;
            foreach (var field in value.EnumerateObject())
            {
                if (field.Name is not ("max_output_tokens" or "timeout_ms")) throw new ArgumentException($"@options only supports `max_output_tokens` and `timeout_ms`; got `{field.Name}`");
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 9_007_199_254_740_991 || Math.Truncate(number) != number)
                    throw new ArgumentException($"@options field `{field.Name}` must be a non-negative safe integer");
                if (field.Name == "max_output_tokens") output = (long)number;
                else
                {
                    if (number == 0 || number > int.MaxValue) throw new ArgumentException("@options field `timeout_ms` must be a positive integer up to 2147483647");
                    timeout = (int)number;
                }
            }
            return new(code, output, timeout);
        }
    }
}

/// <summary>【CodingAgent】【脚本宿主函数】参数与返回值以 JSON 跨越引擎边界，避免向脚本暴露宿主对象。</summary>
public sealed record CodingAgentCodeModeFunction(string Name, string Description,
    Func<JsonElement?, CancellationToken, Task<JsonElement?>> Execute, bool Global = false, bool Spread = false);

/// <summary>【CodingAgent】【脚本结果】保留部分输出、返回值及仅成功时提交的状态变更。</summary>
public sealed record CodingAgentCodeModeResult(bool Success, IReadOnlyList<ContentBlock> Output, JsonElement? Value, JsonObject Set, IReadOnlyList<string> Delete, string? Error);

/// <summary>【CodingAgent】【脚本引擎】独立 Jint 实例执行脚本，宿主异步调用通过单线程消息泵完成 Promise。</summary>
public static class CodingAgentCodeModeSandbox
{
    private static readonly Lazy<string> Prelude = new(ReadPrelude);

    /// <summary>【CodingAgent】【脚本标识】把工具名转换为 JavaScript 标识符，保留原名称的方括号访问。</summary>
    /// <param name="name">工具名。</param><returns>规范标识符。</returns>
    public static string Identifier(string name)
    {
        var result = new System.Text.StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var valid = rune.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '$' || result.Length > 0 && rune.Value is >= '0' and <= '9';
            result.Append(valid ? rune.ToString() : "_");
        }
        return result.Length == 0 ? "_" : result.ToString();
    }

    /// <summary>【CodingAgent】【异步脚本】在工作线程建立沙箱，循环计算和等待工具均受同一取消期限约束。</summary>
    /// <param name="source">已解析源码。</param><param name="functions">允许的工具和全局函数。</param><param name="store">当前分支的 JSON 状态。</param>
    /// <param name="token">会话取消信号。</param><returns>脚本结果，不把普通脚本错误抛到会话外。</returns>
    public static Task<CodingAgentCodeModeResult> ExecuteAsync(CodingAgentCodeModeSource source, IReadOnlyList<CodingAgentCodeModeFunction>? functions = null,
        JsonObject? store = null, CancellationToken token = default) => Task.Run(() => RunAsync(source, functions ?? [], store, token), CancellationToken.None);

    /// <summary>【CodingAgent】【脚本消息泵】所有引擎操作串行执行，异步工具完成后只向队列投递纯 JSON。</summary>
    /// <param name="source">源码。</param><param name="functions">宿主能力列表。</param><param name="store">状态快照。</param><param name="token">取消信号。</param><returns>已排空调用的执行结果。</returns>
    private static async Task<CodingAgentCodeModeResult> RunAsync(CodingAgentCodeModeSource source, IReadOnlyList<CodingAgentCodeModeFunction> functions, JsonObject? store, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (source.TimeoutMilliseconds is { } deadline) lifetime.CancelAfter(deadline);
        var completions = Channel.CreateUnbounded<Completion>(new() { SingleReader = true, AllowSynchronousContinuations = false });
        var calls = new List<Task>(); var output = new List<ContentBlock>(); var set = new JsonObject(); var deleted = new List<string>();
        var finished = false; var success = false; JsonElement? returned = null; string? error = null;
        var available = functions.GroupBy(function => (function.Global, function.Name)).ToDictionary(group => group.Key, group => group.First());
        using var engine = new Engine(options =>
        {
            options.CancellationToken(lifetime.Token).LimitMemory(256 * 1024 * 1024).LimitRecursion(1024);
            options.Interop.AllowGetType = false; options.Interop.AllowSystemReflection = false;
            options.Constraints.MaxArraySize = 16 * 1024 * 1024;
            options.Constraints.RegexTimeout = TimeSpan.FromSeconds(1);
        });
        // 1. 【CodingAgent】【沙箱边界】桥接仅存在于预置脚本闭包中，业务代码无法通过全局名称取得 CLR 委托
        Action<JsValue, JsValue, JsValue, JsValue> bridge = (kind, first, second, third) =>
        {
            switch (kind.AsString())
            {
                case "call": case "global":
                    var id = (long)first.AsNumber(); var name = second.AsString();
                    var parameters = third.IsUndefined() ? (JsonElement?)null : ParseJson(third.AsString());
                    calls.Add(CallAsync(id, available.GetValueOrDefault((kind.AsString() == "global", name)), parameters, completions.Writer, lifetime.Token));
                    break;
                case "output":
                    if (!finished) output.Add(first.AsString() == "image" ? new ImageContent(second.AsString(), third.AsString()) : new TextContent(second.AsString()));
                    break;
                case "done":
                    if (finished) break;
                    finished = true; success = first.AsBoolean();
                    if (success)
                    {
                        returned = second.IsUndefined() ? null : ParseJson(second.AsString());
                        if (!third.IsUndefined())
                            foreach (var write in ParseJson(third.AsString()).EnumerateArray())
                            {
                                var key = write[0].GetString()!;
                                if (write.GetArrayLength() == 1) deleted.Add(key); else set[key] = JsonNode.Parse(write[1].GetString()!);
                            }
                    }
                    else
                    {
                        var problem = ParseJson(second.AsString());
                        error = problem.TryGetProperty("stack", out var stack) ? stack.GetString() : problem.TryGetProperty("message", out var message) ? message.GetString() : problem.GetRawText();
                    }
                    break;
            }
        };
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            // 1. 【CodingAgent】【阻塞能力】移除可以绕过消息泵阻塞线程的共享内存原语，脚本异步能力仅来自宿主桥接
            engine.Execute("delete globalThis.Atomics; delete globalThis.SharedArrayBuffer");
            engine.SetValue("__tau_codemode_bridge", bridge);
            var bridgeValue = engine.GetValue("__tau_codemode_bridge");
            engine.Execute("delete globalThis.__tau_codemode_bridge");
            var tools = new JsonArray(functions.Where(function => !function.Global).Select(function => (JsonNode)new JsonObject
            { ["name"] = function.Name, ["jsName"] = Identifier(function.Name), ["description"] = function.Description }).ToArray());
            var globals = new JsonArray(functions.Where(function => function.Global).Select(function => (JsonNode)new JsonObject { ["name"] = function.Name, ["spread"] = function.Spread }).ToArray());
            var stored = new JsonObject(); foreach (var pair in store ?? []) stored[pair.Key] = pair.Value?.ToJsonString() ?? "null";
            var runtime = engine.Invoke(engine.Evaluate(Prelude.Value, "codemode-prelude.js"), bridgeValue, tools.ToJsonString(), globals.ToJsonString(), stored.ToJsonString()).AsObject();
            var script = engine.Evaluate("(async function(tools, console) {\n" + source.Code + "\n})", "codemode-script.js");
            engine.Invoke(runtime.Get("run"), script);
            // 2. 【CodingAgent】【Promise 调度】脚本无计时器或 I/O，没有待处理宿主调用时立即报告不可恢复的等待
            while (!finished)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                engine.Advanced.ProcessTasks();
                if (finished) break;
                engine.Invoke(runtime.Get("stalled"));
                if (finished) break;
                var completion = await completions.Reader.ReadAsync(lifetime.Token).ConfigureAwait(false);
                engine.Invoke(runtime.Get("settle"), completion.Id, completion.Success, completion.Payload is null ? JsValue.Undefined : JsValue.FromObject(engine, completion.Payload));
            }
        }
        catch (Exception failure)
        {
            if (!finished)
            {
                success = false;
                error = lifetime.IsCancellationRequested ? token.IsCancellationRequested ? "Script cancelled" : "Script timed out" : failure.Message;
            }
        }
        finally
        {
            // 3. 【CodingAgent】【脚本清理】结束或失败时取消尚未完成的调用，并排空它们的结果后再释放引擎
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(calls).ConfigureAwait(false);
            completions.Writer.TryComplete();
        }
        return new(success, output, returned, success ? set : new(), success ? deleted : [], error);
    }

    /// <summary>【CodingAgent】【脚本宿主调用】隔离宿主错误，异步完成不直接触碰 JavaScript 引擎。</summary>
    /// <param name="id">请求标识。</param><param name="function">白名单能力。</param><param name="args">JSON 参数。</param>
    /// <param name="writer">单读者完成队列。</param><param name="token">脚本生命周期。</param><returns>结果已排队的任务。</returns>
    private static async Task CallAsync(long id, CodingAgentCodeModeFunction? function, JsonElement? args, ChannelWriter<Completion> writer, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (function is null) throw new InvalidOperationException("Unknown script host function");
            var result = await function.Execute(args, token).ConfigureAwait(false);
            writer.TryWrite(new(id, true, result?.GetRawText()));
        }
        catch (Exception error) { writer.TryWrite(new(id, false, token.IsCancellationRequested ? "Tool call cancelled" : error.Message)); }
    }

    /// <summary>【CodingAgent】【预置脚本】读取带上游许可说明的嵌入式脚本，仅首次加载。</summary><returns>预置脚本文本。</returns>
    private static string ReadPrelude()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tau.CodingAgent.Runtime.JavaScript.codemode-prelude.js") ?? throw new InvalidOperationException("Missing codemode prelude");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    /// <summary>【CodingAgent】【JSON 桥接】解析独立 JSON 元素。</summary><param name="json">JSON 文本。</param><returns>独立值。</returns>
    private static JsonElement ParseJson(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    /// <summary>【CodingAgent】【调用完成】只有可序列化的数据进入引擎调度队列。</summary>
    private sealed record Completion(long Id, bool Success, string? Payload);
}
