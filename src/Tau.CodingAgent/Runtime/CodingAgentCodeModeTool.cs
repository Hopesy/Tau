// 作者：xxx
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本工具】使用受控 JavaScript 沙箱编排当前会话的嵌套工具。</summary>
public sealed partial class CodingAgentCodeModeTool : ICodingAgentToolDefinition
{
    private readonly RuntimeCodingAgentRunner _runner;
    private readonly CodingAgentExtensionCommandStore? _commands;
    private readonly Func<CancellationToken, Task>? _wait;
    private readonly Func<string, CancellationToken, Task>? _waitForScript;
    private readonly Func<JsonElement?>? _settings;
    private readonly CodingAgentCodeModeOptions _options;
    private bool ModelsEnabled => _options.Models && _commands is not null;
    public string Name => "codemode";
    public string Label => Name;
    public string Exposure => "model-only";
    public bool? DefaultActive => false;
    public CodingAgentSourceInfo? SourceInfo => new("builtin:codemode", "builtin");
    public string Description => BaseDescription(ModelsEnabled);
    /// <summary>【CodingAgent】【脚本能力说明】按允许的模型访问范围生成基础提示，不宣告不可调用的能力。</summary>
    /// <param name="models">是否声明模型能力。</param><returns>基础脚本说明。</returns>
    private static string BaseDescription(bool models) => "Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence), run as an async function body in a JavaScript sandbox: top-level `await` and `return` work. No Node, file system, network, or timers.\n" +
        "- `await tools.<name>({ ...args })` resolves to a string, or an object if the tool declares an output schema, and rejects on failure. Calls still running when the script ends are cancelled.\n" +
        "- Optional first line: `// @options: {\"max_output_tokens\": 10000, \"timeout_ms\": 60000}`\n" +
        "- `text(value)`, `image(dataUrlOrImageBlock)`, `console.info(...)`, and top-level `return` add output; `exit()` ends the script.\n" +
        "- `store(key, value)` and `load(key)` keep JSON values across codemode calls on this session branch.\n" +
        "- `ALL_TOOLS`, `searchTools(query, { limit?, namespace? })`, `describeTool(name)`, `describeNamespace(name)` find unlisted tools, including MCP tools. Discovery helpers are async.\n" +
        "- MCP tools resolve to `{ content, structuredContent?, isError? }`, including error results carrying structured content." +
        (!models ? "" : "\n- `models.getModelsOfType(type, provider?)`, `models.getAvailableOfType(type, provider?)`, `models.getModelOfType(type, provider, id)` return model metadata; type is `chat`, `image`, or `classifier`. All models methods are async.\n" +
        "- `models.classify(model, {state: {...}, questions: {id: {type: 'bool', instructions: '...', criteria: {true: '...', false: '...'}}}})` returns answers and usage. Choice questions use label-to-meaning criteria; score questions use a lowest-first array of strings.\n" +
        "- `models.generateImages(model, {input: [{type: 'text', text: 'prompt'}, ...optional image blocks]})` returns output blocks and usage; show image blocks with `image(block)`.");
    public string? PromptSnippet => "Run JavaScript that calls other tools";
    public IReadOnlyList<string> PromptGuidelines => ["Use codemode to batch independent tool calls (Promise.allSettled), chain them, or filter large output, instead of many separate calls."];
    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
    { ["code"] = new JsonObject { ["type"] = "string", ["description"] = "Raw JavaScript source." } }, ["required"] = new JsonArray("code") });
    public ConstrainedSamplingConfig? ConstrainedSampling { get; } = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = CodingAgentCodeModeSource.Grammar } };

    /// <summary>【CodingAgent】【脚本工具创建】绑定工具执行流水线、可选分支状态及后台发现等待。</summary>
    /// <param name="runner">所属会话。</param><param name="commands">分支状态存储，省略时不持久化写入。</param><param name="wait">执行前等待服务器发现。</param><param name="settings">动态读取 codemode 设置对象。</param>
    /// <param name="waitForScript">可选按本次源码选择必要服务器的等待回调。</param><param name="options">模型访问和呈现覆盖选项。</param>
    public CodingAgentCodeModeTool(RuntimeCodingAgentRunner runner, CodingAgentExtensionCommandStore? commands = null, Func<CancellationToken, Task>? wait = null, Func<JsonElement?>? settings = null,
        Func<string, CancellationToken, Task>? waitForScript = null, CodingAgentCodeModeOptions? options = null)
    {
        _options = options ?? new(); _options.Validate();
        _runner = runner; _commands = commands; _wait = wait; _settings = settings; _waitForScript = waitForScript;
    }

    /// <summary>【CodingAgent】【脚本工具呈现】给活动可调用工具补充脚本签名，并在脚本说明中列出非延迟目录。</summary>
    /// <param name="loadout">原始工具组合。</param><returns>描述替换集合。</returns>
    public CodingAgentToolLoadoutChanges? PrepareLoadout(CodingAgentToolLoadout loadout)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = _settings?.Invoke();
        var only = settings is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "only";
        if ((_options.GetMode?.Invoke() ?? _options.Mode) is { } configuredMode)
        {
            if (configuredMode is not ("on" or "only")) throw new ArgumentException("Codemode mode must be on or only");
            only = configuredMode == "only";
        }
        var budget = settings is { ValueKind: JsonValueKind.Object } configured && configured.TryGetProperty("inlineBudget", out var amount) && amount.ValueKind == JsonValueKind.Number &&
            amount.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : 3000;
        budget = _options.GetInlineBudget?.Invoke() ?? _options.InlineBudget ?? budget;
        if (!double.IsFinite(budget) || budget < 0) throw new ArgumentOutOfRangeException(nameof(budget), "Codemode inline budget must be finite and non-negative");
        var callable = loadout.Callable.Where(tool => tool.Name != Name).ToArray();
        var declared = loadout.Declared.Where(tool => callable.Any(candidate => candidate.Name == tool.Name)).ToArray();
        if (!only) foreach (var tool in declared)
            descriptions[tool.Name] = tool.Description.Trim() + $"\n\nCodemode: `tools.{CodingAgentCodeModeSandbox.Identifier(tool.Name)}(args)` resolves to {DescribeOutput(tool.OutputSchema)}.";
        descriptions[Name] = DescribeCatalog(callable.Where(tool => (only || loadout.GetExposure(tool.Name) != "direct") && loadout.GetExposure(tool.Name) != "deferred").ToArray(), budget);
        return new() { Descriptions = descriptions, HiddenDeclarations = only ? declared.Where(tool => loadout.GetExposure(tool.Name) == "direct").Select(tool => tool.Name).ToArray() : [] };
    }

    /// <summary>【CodingAgent】【脚本执行】嵌套调用复用会话校验和扩展钩子，成功后提交状态，模型只看到脚本输出。</summary>
    /// <param name="toolCallId">父调用标识。</param><param name="args">code 源码。</param><param name="ct">取消信号。</param><param name="onUpdate">嵌套进度回调。</param><returns>带调用详情的脚本结果。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var source = CodingAgentCodeModeSource.Parse(args.GetProperty("code").GetString()!);
        if (_wait is not null) await _wait(ct).ConfigureAwait(false);
        if (_waitForScript is not null) await _waitForScript(source.Code, ct).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp(); var state = _commands?.ReadCodeModeStore();
        var callable = _runner.GetCallableTools().Where(tool => tool.Name != Name).ToArray();
        var samples = callable.ToDictionary(tool => tool.Name, CodingAgentCodeModeDeclarations.Sample, StringComparer.Ordinal);
        var calls = new List<JsonObject>(); var gate = new object(); using var updates = new SemaphoreSlim(1, 1);
        using var modelState = new CodeModeModelState();
        var functions = callable.Select(tool => new CodingAgentCodeModeFunction(tool.Name, samples[tool.Name], async (parameters, token) =>
        {
            var record = new JsonObject { ["id"] = toolCallId + "/?", ["name"] = tool.Name, ["args"] = Preview(parameters?.GetRawText() ?? "", 200), ["status"] = "running" };
            lock (gate) calls.Add(record);
            await PublishAsync(calls, gate, updates, onUpdate).ConfigureAwait(false);
            var callStarted = Stopwatch.GetTimestamp();
            var outcome = await _runner.ExecuteNestedToolAsync(toolCallId, tool.Name, parameters ?? JsonSerializer.SerializeToElement(new JsonObject()), token).ConfigureAwait(false);
            var text = string.Join('\n', outcome.Result.Content.OfType<TextContent>().Select(block => block.Text));
            lock (gate)
            {
                record["id"] = outcome.ToolCall.Id; record["durationMs"] = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds;
                record["status"] = outcome.IsError ? token.IsCancellationRequested ? "cancelled" : "error" : "ok";
                if (outcome.IsError) record["error"] = Preview(string.IsNullOrEmpty(text) ? $"Tool \"{tool.Name}\" failed" : text, 500);
            }
            await PublishAsync(calls, gate, updates, onUpdate).ConfigureAwait(false);
            if (tool.OutputSchema is not null && outcome.Result.StructuredContent is { } structured) return structured;
            if (outcome.IsError) throw new InvalidOperationException(string.IsNullOrEmpty(text) ? $"Tool \"{tool.Name}\" failed" : text);
            return JsonSerializer.SerializeToElement(JsonValue.Create(text));
        })).Concat(Discovery(callable, samples)).Concat(ModelGlobals(toolCallId, calls, gate, updates, onUpdate, modelState)).ToArray();
        var result = await CodingAgentCodeModeSandbox.ExecuteAsync(source, functions, state?.Values, ct).ConfigureAwait(false);
        lock (gate) foreach (var call in calls) if (call["status"]?.GetValue<string>() == "running") call["status"] = "cancelled";
        if (result.Success && (result.Set.Count > 0 || result.Delete.Count > 0) && _options.AppendEntry is { } append)
            append("codemode-store", new(result.Set.DeepClone().AsObject(), result.Delete.ToArray()));
        else if (_options.PersistStore && state is not null) _commands!.CommitCodeModeStore(state, result);
        var content = result.Output.ToList();
        if (result.Success && result.Value is { } value) content.Add(new TextContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()));
        if (!result.Success) content.Add(new TextContent("Script error:\n" + result.Error));
        if (modelState.GeneratedImages > 0 && !content.OfType<ImageContent>().Any())
            content.Add(new TextContent($"Note: models.generateImages() returned {modelState.GeneratedImages} image{(modelState.GeneratedImages == 1 ? "" : "s")} that the script did not show. Show each image block of result.output with image(block)."));
        var details = Snapshot(calls, gate);
        // 1. 【CodingAgent】【脚本输出预算】以四字符每 token 限制模型文本，图像保留，完整文本保存供后续读取
        var combined = string.Join('\n', content.OfType<TextContent>().Select(block => block.Text));
        var budget = Math.Min((source.MaxOutputTokens ?? 10000) * 4, int.MaxValue);
        if (combined.Length > budget)
        {
            var head = (int)(budget / 2); var tail = (int)(budget - head);
            var text = $"Warning: truncated output (original token count: {(combined.Length + 3L) / 4})\nTotal output lines: {combined.Split('\n').Length}\n\n" +
                combined[..head] + $"…{(combined.Length - budget + 3) / 4} tokens truncated…" + (tail > 0 ? combined[^tail..] : "");
            try
            {
                var path = await CodingAgentMcpToolResult.SaveToTempFileAsync(Encoding.UTF8.GetBytes(combined), ".txt", CancellationToken.None).ConfigureAwait(false);
                details["fullOutputPath"] = path; text += $"\n\n[Full output: {path} (read with offset/limit)]";
            }
            catch (Exception error) when (error is not OperationCanceledException) { text += $"\n\n[Could not save the full output: {error.Message}]"; }
            content = [new TextContent(text), .. content.OfType<ImageContent>()];
        }
        var header = $"{(result.Success ? "Script completed" : "Script failed")}\nWall time {Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds\nOutput:\n";
        if (content.FirstOrDefault() is TextContent first) content[0] = first with { Text = header + first.Text }; else content.Insert(0, new TextContent(header));
        return new(content, !result.Success, JsonSerializer.SerializeToElement(details)) { Usage = modelState.Usage };
    }

    /// <summary>【CodingAgent】【脚本发现】创建搜索、单工具说明和命名组查询，范围限于当前可调用工具。</summary>
    /// <param name="tools">可调用工具。</param><param name="samples">渲染后的说明。</param><returns>三个脚本全局函数。</returns>
    private IEnumerable<CodingAgentCodeModeFunction> Discovery(IReadOnlyList<IAgentTool> tools, IReadOnlyDictionary<string, string> samples)
    {
        foreach (var name in new[] { "searchTools", "describeTool", "describeNamespace" })
            yield return new(name, "Tool discovery", (args, _) => Task.FromResult(Discover(name, args, tools, samples)), true, true);
    }

    /// <summary>【CodingAgent】【发现分发】验证参数、按 BM25 排序并返回独立的 JSON 元数据。</summary>
    /// <param name="method">发现函数名。</param><param name="args">展开后的实参数组。</param><param name="tools">候选工具。</param><param name="samples">说明。</param><returns>JSON 结果或 undefined。</returns>
    private JsonElement? Discover(string method, JsonElement? args, IReadOnlyList<IAgentTool> tools, IReadOnlyDictionary<string, string> samples)
    {
        if (args is not { ValueKind: JsonValueKind.Array } values || values.GetArrayLength() == 0 || values[0].ValueKind != JsonValueKind.String) throw new ArgumentException(method + "() expects a string");
        var query = values[0].GetString()!;
        if (method == "describeTool")
        {
            var tool = tools.FirstOrDefault(tool => tool.Name == query || CodingAgentCodeModeSandbox.Identifier(tool.Name) == query);
            return tool is null ? null : JsonSerializer.SerializeToElement(JsonValue.Create(samples[tool.Name]));
        }
        if (method == "describeNamespace")
        {
            var matching = tools.Select(tool => (Tool: tool, Namespace: ResolveNamespace(tool))).Where(entry => NamespaceMatches(entry.Namespace, query)).ToArray();
            if (matching.Length == 0) return null;
            var ns = JsonNode.Parse(matching[0].Namespace!.Value.GetRawText())!.AsObject();
            ns["tools"] = new JsonArray(matching.Select(entry => (JsonNode?)JsonValue.Create(CodingAgentCodeModeSandbox.Identifier(entry.Tool.Name))).ToArray());
            return JsonSerializer.SerializeToElement(ns);
        }
        var options = values.GetArrayLength() > 1 && values[1].ValueKind == JsonValueKind.Object ? values[1] : default;
        var limit = 8; string? filter = null;
        if (options.ValueKind == JsonValueKind.Object)
        {
            if (options.TryGetProperty("limit", out var max) && max.ValueKind != JsonValueKind.Null)
            {
                if (max.ValueKind != JsonValueKind.Number || !max.TryGetDouble(out var number) || number <= 0 || !double.IsFinite(number) || number != Math.Truncate(number)) throw new ArgumentException("searchTools() limit must be a positive integer");
                limit = (int)Math.Min(number, int.MaxValue);
            }
            if (options.TryGetProperty("namespace", out var ns) && ns.ValueKind != JsonValueKind.Null)
            { if (ns.ValueKind != JsonValueKind.String) throw new ArgumentException("searchTools() namespace must be a string"); filter = ns.GetString(); }
        }
        var matches = CodingAgentToolSearch.Rank(query, tools.Select(tool => (Tool: tool, Namespace: ResolveNamespace(tool)))
            .Where(entry => string.IsNullOrEmpty(filter) || NamespaceMatches(entry.Namespace, filter))
            .Select(entry => CodingAgentToolSearch.CreateDocument(entry.Tool, entry.Namespace)).ToArray(), limit);
        return JsonSerializer.SerializeToElement(new JsonArray(matches.Select(match => (JsonNode)new JsonObject { ["name"] = CodingAgentCodeModeSandbox.Identifier(match.Name), ["description"] = samples[match.Name] }).ToArray()));
    }

    /// <summary>【CodingAgent】【命名组匹配】支持完整名称、规范标识和 MCP 名称后缀。</summary><param name="value">命名组。</param><param name="query">查询名。</param><returns>是否匹配。</returns>
    private static bool NamespaceMatches(JsonElement? value, string query)
    {
        if (value is not { ValueKind: JsonValueKind.Object } ns || !ns.TryGetProperty("name", out var field) || field.ValueKind != JsonValueKind.String) return false;
        var name = field.GetString()!; var id = CodingAgentCodeModeSandbox.Identifier(name); var wanted = CodingAgentCodeModeSandbox.Identifier(query);
        return name == query || id == wanted || name.Contains("__", StringComparison.Ordinal) && name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..] == query ||
            id.Contains("__", StringComparison.Ordinal) && id[(id.LastIndexOf("__", StringComparison.Ordinal) + 2)..] == wanted;
    }
    /// <summary>【CodingAgent】【宿主命名组解析】显式宿主回调优先，未配置时使用工具自身元数据。</summary>
    /// <param name="tool">工具定义。</param><returns>命名组或空值。</returns>
    private JsonElement? ResolveNamespace(IAgentTool tool) => _options.GetToolNamespace is { } resolve ? resolve(tool.Name) : (tool as ICodingAgentToolDefinition)?.Namespace;
    /// <summary>【CodingAgent】【脚本进度】串行投递独立调用快照，不把嵌套结果正文发送给模型。</summary>
    /// <param name="calls">调用记录。</param><param name="gate">记录锁。</param><param name="updates">投递锁。</param><param name="callback">宿主回调。</param><returns>投递任务。</returns>
    private static async Task PublishAsync(List<JsonObject> calls, object gate, SemaphoreSlim updates, Func<ToolUpdate, Task>? callback)
    { if (callback is null) return; await updates.WaitAsync().ConfigureAwait(false); try { await callback(new("", [], Details: JsonSerializer.SerializeToElement(Snapshot(calls, gate)))).ConfigureAwait(false); } finally { updates.Release(); } }
    /// <summary>【CodingAgent】【调用快照】复制记录避免并发修改已经发布的详情。</summary><param name="calls">记录。</param><param name="gate">同步锁。</param><returns>独立详情。</returns>
    private static JsonObject Snapshot(List<JsonObject> calls, object gate) { lock (gate) return new() { ["calls"] = new JsonArray(calls.Select(call => call.DeepClone()).ToArray()) }; }
    /// <summary>【CodingAgent】【调用预览】限制 UI 中的参数和错误长度。</summary><param name="text">文本。</param><param name="length">字符上限。</param><returns>预览。</returns>
    private static string Preview(string text, int length) => text.Length > length ? text[..(length - 3)] + "..." : text;
}
