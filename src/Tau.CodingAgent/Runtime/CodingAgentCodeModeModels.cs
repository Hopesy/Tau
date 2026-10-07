// 作者：xxx
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCodeModeTool
{
    /// <summary>【CodingAgent】【脚本模型桥接】提供目录和非聊天能力，所有入参及出参均为 JSON 副本。</summary>
    /// <param name="parentId">父工具标识。</param><param name="calls">调用详情。</param><param name="gate">详情同步锁。</param>
    /// <param name="updates">进度投递锁。</param><param name="onUpdate">宿主进度。</param><param name="state">本脚本的用量及并发限制。</param><returns>脚本全局方法。</returns>
    private IEnumerable<CodingAgentCodeModeFunction> ModelGlobals(string parentId, List<JsonObject> calls, object gate, SemaphoreSlim updates,
        Func<ToolUpdate, Task>? onUpdate, CodeModeModelState state)
    {
        if (!ModelsEnabled) yield break;
        foreach (var method in new[] { "getModelsOfType", "getAvailableOfType", "getModelOfType", "classify", "generateImages" })
        {
            yield return new("models." + method, "Session model registry", async (parameters, token) =>
            {
                var args = parameters is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
                var first = args.ElementAtOrDefault(0); var second = args.ElementAtOrDefault(1);
                if (method is "classify" or "generateImages")
                    return await CallModelAsync(method, first, second, parentId, calls, gate, updates, onUpdate, state, token).ConfigureAwait(false);
                var type = ModelType(first); string? provider = null;
                if (second.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    if (second.ValueKind != JsonValueKind.String) throw new ArgumentException("provider must be a string");
                    provider = second.GetString();
                }
                if (method == "getModelOfType")
                {
                    if (provider is null || args.ElementAtOrDefault(2).ValueKind != JsonValueKind.String)
                        throw new ArgumentException("models.getModelOfType(type, provider, id) expects three strings. The provider and the id are separate arguments.");
                    return _runner.GetCodeModeModel(type, provider, args[2].GetString()!);
                }
                var request = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = type, ["provider"] = provider });
                var models = method == "getAvailableOfType"
                    ? await _commands!.GetCodeModeAvailableModelsAsync(_runner, request, token).ConfigureAwait(false)
                    : _runner.GetCodeModeModels(type, provider);
                return JsonSerializer.SerializeToElement(new JsonArray(models.Select(model => (JsonNode)ModelInfo(model)).ToArray()));
            }, true, true);
        }
    }

    /// <summary>【CodingAgent】【脚本模型执行】按目录重新解析模型并验证上下文，最多四个并发请求，记录用量而不记录输入正文。</summary>
    /// <param name="method">能力方法。</param><param name="model">脚本提供的模型引用。</param><param name="context">能力输入。</param>
    /// <param name="parentId">父工具标识。</param><param name="calls">调用详情。</param><param name="gate">详情锁。</param>
    /// <param name="updates">进度锁。</param><param name="onUpdate">进度回调。</param><param name="state">脚本统计。</param><param name="token">取消信号。</param><returns>完整原生结果。</returns>
    private async Task<JsonElement?> CallModelAsync(string method, JsonElement model, JsonElement context, string parentId,
        List<JsonObject> calls, object gate, SemaphoreSlim updates, Func<ToolUpdate, Task>? onUpdate, CodeModeModelState state, CancellationToken token)
    {
        var type = method == "classify" ? ModelTypes.Classifier : ModelTypes.Image;
        var article = type == ModelTypes.Image ? "an image" : "a classifier";
        var hint = $"List the {type} models you can use with models.getAvailableOfType(\"{type}\").";
        if (model.ValueKind != JsonValueKind.Object || StringField(model, "provider") is not { } provider || StringField(model, "id") is not { } id)
            throw new ArgumentException($"models.{method}() expects {article} model as its first argument. models.getModelOfType() returns undefined for an unknown provider or id. {hint}");
        // 1. 【CodingAgent】【模型身份】仅采用 provider/id，不转发脚本修改的 baseUrl、headers 或 apiKey
        if (_runner.GetCodeModeModel(type, provider, id) is null)
        {
            var other = new[] { ModelTypes.Chat, ModelTypes.Image, ModelTypes.Classifier }.FirstOrDefault(candidate => candidate != type && _runner.GetCodeModeModel(candidate, provider, id) is not null);
            throw new ArgumentException(other is null ? $"Unknown {type} model \"{provider}/{id}\". {hint}" : $"\"{provider}/{id}\" is {(other == ModelTypes.Image ? "an" : "a")} {other} model, not {article} model. {hint}");
        }
        ValidateModelContext(type, context);
        var record = new JsonObject { ["id"] = parentId + "/models." + method + "/" + Interlocked.Increment(ref state.Calls),
            ["name"] = "models." + method, ["args"] = provider + "/" + id, ["status"] = "running" };
        lock (gate) calls.Add(record);
        var started = Stopwatch.GetTimestamp(); var acquired = false;
        try
        {
            await PublishAsync(calls, gate, updates, onUpdate).ConfigureAwait(false);
            await state.Limiter.WaitAsync(token).ConfigureAwait(false); acquired = true;
            var request = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = type, ["provider"] = provider, ["modelId"] = id, ["context"] = JsonNode.Parse(context.GetRawText()),
                ["options"] = new JsonObject(), ["callbacks"] = new JsonObject(), ["filePath"] = "builtin:codemode", ["callId"] = Guid.NewGuid().ToString("N")
            });
            var result = await _commands!.CallCodeModeModelAsync(_runner, request, token).ConfigureAwait(false);
            var usage = CodingAgentCapabilityJson.ReadMessage(null, result).Usage;
            lock (gate)
            {
                record["status"] = StringField(result, "stopReason") switch { "stop" => "ok", "aborted" => "cancelled", _ => "error" };
                if (StringField(result, "errorMessage") is { Length: > 0 } error) record["error"] = Preview(error, 500);
                if (usage is { } value) { record["cost"] = value.Cost?.Total; state.Usage = RuntimeCodingAgentRunner.CombineToolUsage(state.Usage, value); }
                if (type == ModelTypes.Image && result.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                    state.GeneratedImages += output.EnumerateArray().Count(block => StringField(block, "type") == "image");
            }
            return result;
        }
        catch (Exception error)
        {
            lock (gate) { record["status"] = token.IsCancellationRequested ? "cancelled" : "error"; record["error"] = Preview(error.Message, 500); }
            throw;
        }
        finally
        {
            if (acquired) state.Limiter.Release();
            lock (gate) record["durationMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await PublishAsync(calls, gate, updates, onUpdate).ConfigureAwait(false);
        }
    }

    /// <summary>【CodingAgent】【能力输入校验】分类问题和图像输入在发起请求前验证，错误明确指出预期结构。</summary>
    /// <param name="type">能力类型。</param><param name="context">原始上下文。</param>
    private static void ValidateModelContext(string type, JsonElement context)
    {
        var image = type == ModelTypes.Image;
        var prefix = image ? "models.generateImages()" : "models.classify()";
        var shape = image ? "{ input: [{ type: \"text\", text: <prompt> }, ...optional { type: \"image\", data: <base64>, mimeType }] }" :
            "{ state: { ... }, questions: { <id>: { type: \"choice\" | \"score\" | \"bool\", instructions, criteria } } }";
        if (context.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{prefix} expects a context object as its second argument. Expected context: {shape}");
        if (image)
        {
            if (!context.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
                throw new ArgumentException($"{prefix} context.input must be a non-empty array of blocks. Expected context: {shape}");
            var index = 0;
            foreach (var block in input.EnumerateArray())
            {
                if (!(StringField(block, "type") == "text" && StringField(block, "text") is not null ||
                    StringField(block, "type") == "image" && StringField(block, "data") is not null && StringField(block, "mimeType") is not null))
                    throw new ArgumentException($"{prefix} context.input[{index}] must be a text or image block. Expected context: {shape}");
                index++;
            }
            return;
        }
        if (!context.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{prefix} context.state must be an object. Expected context: {shape}");
        if (!context.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Object || !questions.EnumerateObject().Any())
            throw new ArgumentException($"{prefix} context.questions must map question IDs to questions. Expected context: {shape}");
        foreach (var pair in questions.EnumerateObject())
        {
            var question = pair.Value;
            if (question.ValueKind != JsonValueKind.Object || StringField(question, "instructions") is null)
                throw new ArgumentException($"{prefix} context.questions.{pair.Name}.instructions must be a string. Expected context: {shape}");
            var criteria = question.TryGetProperty("criteria", out var value) ? value : default;
            var valid = StringField(question, "type") switch
            {
                "choice" => criteria.ValueKind == JsonValueKind.Object && criteria.EnumerateObject().Any() && criteria.EnumerateObject().All(item => item.Value.ValueKind == JsonValueKind.String),
                "score" => criteria.ValueKind == JsonValueKind.Array && criteria.GetArrayLength() > 0 && criteria.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                "bool" => StringField(criteria, "true") is not null && StringField(criteria, "false") is not null,
                _ => false
            };
            if (!valid) throw new ArgumentException($"{prefix} context.questions.{pair.Name} must use choice (label-to-meaning criteria), score (lowest-first string array), or bool (true/false string criteria). Expected context: {shape}");
        }
    }

    /// <summary>【CodingAgent】【模型类型】只接受公开的三种能力类型。</summary><param name="value">类型参数。</param><returns>规范类型。</returns>
    private static string ModelType(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is ModelTypes.Chat or ModelTypes.Image or ModelTypes.Classifier
        ? value.GetString()! : throw new ArgumentException("Unknown model type. Use \"chat\", \"image\", or \"classifier\".");
    /// <summary>【CodingAgent】【模型脱敏】目录副本去除可能携带凭据的请求头。</summary><param name="model">原生模型对象。</param><returns>去除请求头的副本。</returns>
    internal static JsonObject ModelInfo(JsonElement model) { var value = JsonNode.Parse(model.GetRawText())!.AsObject(); value.Remove("headers"); return value; }
    /// <summary>【CodingAgent】【能力字段】安全读取对象字符串字段。</summary><param name="value">对象。</param><param name="key">字段。</param><returns>字符串或空值。</returns>
    private static string? StringField(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    /// <summary>【CodingAgent】【脚本模型统计】保存并发额度、生成图像数及仅属于模型调用的用量。</summary>
    private sealed class CodeModeModelState : IDisposable
    {
        internal readonly SemaphoreSlim Limiter = new(4, 4);
        internal int Calls; internal int GeneratedImages; internal Usage? Usage;
        /// <summary>【CodingAgent】【额度释放】脚本的全部宿主调用结束后释放并发限制器。</summary>
        public void Dispose() => Limiter.Dispose();
    }
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【脚本模型目录】按能力读取当前目录，不返回认证信息。</summary><param name="type">能力。</param><param name="provider">可选提供方。</param><returns>模型列表。</returns>
    internal IReadOnlyList<JsonElement> GetCodeModeModels(string type, string? provider) => _modelCatalog.GetModelsOfType(type, provider).Select(CodingAgentExtensionSessionBridge.SerializeExtensionModel).ToArray();
    /// <summary>【CodingAgent】【脚本模型查找】解析 provider/id 并移除请求头，未知模型返回 undefined。</summary><param name="type">能力。</param><param name="provider">提供方。</param><param name="id">标识。</param><returns>模型副本或空值。</returns>
    internal JsonElement? GetCodeModeModel(string type, string provider, string id) => _modelCatalog.GetModelOfType(type, provider, id) is { } model
        ? JsonSerializer.SerializeToElement(CodingAgentCodeModeTool.ModelInfo(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model))) : null;
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【脚本可用目录】复用当前扩展提供方的认证检查及模型过滤。</summary><param name="runner">会话。</param><param name="request">查询条件。</param><param name="token">取消。</param><returns>可用模型。</returns>
    internal Task<IReadOnlyList<JsonElement>> GetCodeModeAvailableModelsAsync(RuntimeCodingAgentRunner runner, JsonElement request, CancellationToken token) =>
        runner.GetAvailableRegistryModelsAsync(request, _javaScriptRuntime, token);
    /// <summary>【CodingAgent】【脚本能力入口】复用当前会话的完整认证与实际能力实现。</summary><param name="runner">会话。</param><param name="request">规范请求。</param><param name="token">取消。</param><returns>原生结果。</returns>
    internal Task<JsonElement> CallCodeModeModelAsync(RuntimeCodingAgentRunner runner, JsonElement request, CancellationToken token) => runner.CallRegistryCapabilityAsync(request, _javaScriptRuntime, token);
}
