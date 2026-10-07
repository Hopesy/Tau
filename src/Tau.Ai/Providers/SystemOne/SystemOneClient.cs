// 作者：xxx
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.SystemOne;

/// <summary>【AI】【System One】共享分类答案解析、回调、重试及超时逻辑。</summary>
internal sealed class SystemOneClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _cloudflare;
    private string Label => _cloudflare ? "Cloudflare Workers AI" : "System One API";

    /// <summary>创建分类协议实现。</summary>
    /// <param name="httpClient">可选共享 HTTP 客户端，由调用方管理其生命周期。</param>
    /// <param name="cloudflare">是否使用 Cloudflare REST 请求和响应信封。</param>
    public SystemOneClient(HttpClient? httpClient = null, bool cloudflare = false)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
        _cloudflare = cloudflare;
    }

    /// <summary>当前传输对应的分类协议标识。</summary>
    public string Api => _cloudflare ? "cloudflare-workers-ai-system-one" : "typesafe-system-one";

    /// <summary>【AI】【System One】执行请求并将传输答案映射为公共分类结果。</summary>
    /// <param name="model">分类模型。</param>
    /// <param name="context">状态与问题。</param>
    /// <param name="options">认证、回调及重试选项。</param>
    /// <returns>分类答案；失败时返回错误或中止结果。</returns>
    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options)
    {
        var output = new ClassifierResult { Api = model.Api, Provider = model.Provider, Model = model.Id };
        try
        {
            // 1. 【AI】【System One】校验协议并只执行一次请求体回调，重试复用相同请求体
            options.Signal.ThrowIfCancellationRequested();
            if (model.Api != Api) throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            if (_cloudflare) model = ResolveCloudflareModel(model, options.Env);
            var payload = BuildPayload(model, context);
            if (options.OnPayload is not null)
                payload = await options.OnPayload(payload, model).ConfigureAwait(false) ?? payload;

            // 2. 【AI】【System One】每次尝试单独计时，并在读取完整响应后通知回调
            var (body, response) = await SendWithRetryAsync(model, payload, options).ConfigureAwait(false);
            if (options.OnResponse is not null) await options.OnResponse(response, model).ConfigureAwait(false);
            var content = UnwrapResponse(body);

            // 3. 【AI】【System One】先记录已计费用量，即使答案损坏也保留消耗
            output = output with { Usage = ParseUsage(content, model) };
            return output with { Answers = ParseAnswers(content, context) };
        }
        catch (Exception ex)
        {
            return output with
            {
                StopReason = options.Signal.IsCancellationRequested ? ClassifierStopReason.Aborted : ClassifierStopReason.Error,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>将公共 bool 问题转换为线协议 noul，同时保留状态和其他问题字段。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">状态与问题。</param>
    /// <returns>独立持有内存的 JSON 请求体。</returns>
    private JsonElement BuildPayload(ClassifierModel model, ClassifierContext context)
    {
        if (context.State.ValueKind != JsonValueKind.Object) throw new ArgumentException("Classifier state must be a JSON object.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.Id);
            if (_cloudflare) writer.WriteStartObject("input");
            writer.WritePropertyName("state");
            context.State.WriteTo(writer);
            writer.WriteStartObject("questions");
            foreach (var (id, question) in context.Questions)
            {
                writer.WriteStartObject(id);
                var json = JsonSerializer.SerializeToElement(question, TauAiJsonContext.Default.ClassifierQuestion);
                foreach (var property in json.EnumerateObject())
                {
                    if (property.NameEquals("type") && question is ClassifierBoolQuestion) writer.WriteString("type", "noul");
                    else property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            if (_cloudflare) writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>按 HTTP 状态和异常重试，并遵守服务端退避时间。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="payload">转换完成的请求体。</param>
    /// <param name="options">请求选项。</param>
    /// <returns>完整响应 JSON 及响应元数据。</returns>
    private async Task<(JsonElement Body, ProviderResponse Response)> SendWithRetryAsync(ClassifierModel model, JsonElement payload, ClassifierOptions options)
    {
        for (var attempt = 0; ; attempt++)
        {
            options.Signal.ThrowIfCancellationRequested();
            try
            {
                return await SendAttemptAsync(model, payload, options).ConfigureAwait(false);
            }
            catch (Exception ex) when (!options.Signal.IsCancellationRequested && attempt < Math.Max(0, options.MaxRetries ?? 2) &&
                (ProviderRetry.ShouldRetry(ex) || ex is HttpRequestException { StatusCode: null }))
            {
                var delay = ProviderRetry.ResolveDelay(ex.Data["headers"] as HttpResponseHeaders,
                    TimeSpan.FromMilliseconds(Math.Min(8_000, 250 * Math.Pow(2, attempt))), options.MaxRetryDelay ?? TimeSpan.FromSeconds(30));
                await Task.Delay(delay, options.Signal).ConfigureAwait(false);
            }
        }
    }

    /// <summary>发送一次带独立超时的 HTTP 请求，并释放消息及响应资源。</summary>
    /// <param name="model">请求模型。</param>
    /// <param name="payload">JSON 请求体。</param>
    /// <param name="options">请求选项。</param>
    /// <returns>响应 JSON 及元数据。</returns>
    private async Task<(JsonElement Body, ProviderResponse Response)> SendAttemptAsync(ClassifierModel model, JsonElement payload, ClassifierOptions options)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.Signal);
        if (options.Timeout is { } duration) timeout.CancelAfter(duration);
        var path = _cloudflare ? "run" : "systemone";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl?.TrimEnd('/')}/{path}")
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = new HttpRequestException($"{Label} returned {(int)response.StatusCode}: {text}", null, response.StatusCode);
                error.Data["headers"] = response.Headers;
                throw error;
            }
            using var document = JsonDocument.Parse(text);
            return (document.RootElement.Clone(), new ProviderResponse((int)response.StatusCode, AiHeaderUtilities.ToDictionary(response)));
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !options.Signal.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {options.Timeout?.TotalMilliseconds}ms", ex);
        }
    }

    /// <summary>按不区分大小写的名称覆盖请求头和内容头。</summary>
    /// <param name="request">待发送请求。</param>
    /// <param name="headers">覆盖字典。</param>
    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (name, value) in headers)
        {
            if (request.Headers.TryGetValues(name, out _)) request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                if (request.Content!.Headers.TryGetValues(name, out _)) request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <summary>按请求问题逐一验证答案，并忽略未请求的额外答案。</summary>
    /// <param name="body">完整响应对象。</param>
    /// <param name="context">原始问题。</param>
    /// <returns>公共类型的答案字典。</returns>
    private IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(JsonElement body, ClassifierContext context)
    {
        if (!body.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{Label} returned an unexpected response");
        var result = new Dictionary<string, ClassifierAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in context.Questions)
        {
            if (!answers.TryGetProperty(id, out var answer) || answer.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{Label} did not return an answer for {id}");
            var type = answer.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : null;
            result[id] = question switch
            {
                ClassifierChoiceQuestion when type == "choice" => ParseChoice(answer, id),
                ClassifierScoreQuestion when type == "score" => new ClassifierScoreAnswer(RequiredNumber(answer, "score"), RequiredNumber(answer, "confidence")),
                ClassifierBoolQuestion when type == "noul" => new ClassifierBoolAnswer(RequiredNumber(answer, "noul")),
                _ => throw new InvalidOperationException($"{Label} returned an invalid answer type for {id}")
            };
        }
        return result;
    }

    /// <summary>解析选择答案并验证所有概率是有限数值。</summary>
    /// <param name="answer">答案 JSON。</param>
    /// <param name="id">问题标识。</param>
    /// <returns>选择答案。</returns>
    private ClassifierChoiceAnswer ParseChoice(JsonElement answer, string id)
    {
        if (!answer.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.String ||
            !answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{Label} returned an invalid choice answer for {id}");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var probability in probabilities.EnumerateObject()) values[probability.Name] = RequiredNumber(probabilities, probability.Name);
        return new ClassifierChoiceAnswer(choice.GetString()!, values, RequiredNumber(answer, "confidence"));
    }

    /// <summary>提取必须存在的有限数值。</summary>
    /// <param name="value">包含数值的对象。</param>
    /// <param name="field">字段名称。</param>
    /// <returns>有限数值；格式错误时抛出异常。</returns>
    private double RequiredNumber(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var result) || !double.IsFinite(result))
            throw new InvalidOperationException($"{Label} returned an invalid {field}");
        return result;
    }

    /// <summary>读取可选用量，并按模型目录价格计算成本。</summary>
    /// <param name="body">完整响应。</param>
    /// <param name="model">包含价格的模型。</param>
    /// <returns>有效用量；没有计数字段时返回 null。</returns>
    private static Usage? ParseUsage(JsonElement body, ClassifierModel model)
    {
        if (!body.TryGetProperty("usage", out var value) || value.ValueKind != JsonValueKind.Object ||
            (!value.TryGetProperty("input_tokens", out _) && !value.TryGetProperty("output_tokens", out _))) return null;
        var input = TokenCount(value, "input_tokens");
        var output = TokenCount(value, "output_tokens");
        var usage = new Usage(input, output, 0, 0) { TotalTokens = (int)Math.Min(int.MaxValue, (long)input + output) };
        return usage with { Cost = ModelCatalog.CalculateCost(model, usage) };
    }

    /// <summary>将有限正数转换为 .NET 用量计数，非法值归零。</summary>
    /// <param name="value">用量对象。</param>
    /// <param name="field">计数字段。</param>
    /// <returns>非负且不溢出的整数。</returns>
    private static int TokenCount(JsonElement value, string field) =>
        value.TryGetProperty(field, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetDouble(out var number) && double.IsFinite(number) && number > 0
            ? (int)Math.Min(int.MaxValue, number) : 0;

    /// <summary>【AI】【Cloudflare 响应】兼容直接答案与第三方任务结果，并拒绝未完成状态。</summary>
    /// <param name="body">HTTP 响应对象。</param>
    /// <returns>包含 answers 和 usage 的模型输出。</returns>
    private JsonElement UnwrapResponse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{Label} returned an unexpected response");
        if (!_cloudflare) return body;
        if (body.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var messages = body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? errors.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("message").GetString()).ToArray() : [];
            throw new InvalidOperationException(messages.Length > 0 ? $"{Label} error: {string.Join("; ", messages)}" : $"{Label} request failed");
        }
        if (!body.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{Label} returned an unexpected response");
        if (result.TryGetProperty("answers", out _)) return result;
        if (!result.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "Completed")
            throw new InvalidOperationException($"{Label} run did not complete (state: {(state.ValueKind == JsonValueKind.Undefined ? "undefined" : state.ToString())})");
        if (!result.TryGetProperty("result", out var output) || output.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{Label} returned an unexpected response");
        return output;
    }

    /// <summary>【AI】【Cloudflare 地址】用已解析环境替换账户占位符，防止未解析地址进入 HTTP。</summary>
    /// <param name="model">分类模型。</param>
    /// <param name="environment">认证阶段解析的环境。</param>
    /// <returns>替换地址后的模型副本。</returns>
    private static ClassifierModel ResolveCloudflareModel(ClassifierModel model, IReadOnlyDictionary<string, string>? environment)
    {
        var baseUrl = model.BaseUrl;
        foreach (var name in new[] { "CLOUDFLARE_ACCOUNT_ID", "CLOUDFLARE_GATEWAY_ID" })
        {
            if (baseUrl?.Contains("{" + name + "}", StringComparison.Ordinal) != true) continue;
            var value = environment?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required for {model.Provider}.");
            baseUrl = baseUrl.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }
        return model with { BaseUrl = baseUrl };
    }
}
