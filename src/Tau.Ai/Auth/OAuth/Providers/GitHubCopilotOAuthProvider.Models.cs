// 作者：xxx
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed partial class GitHubCopilotOAuthProvider
{
    /// <summary>【AI】【Copilot 账户目录】已允许的模型与可以尝试启用 policy 的已知模型。</summary>
    internal sealed record AccountCatalog(IReadOnlyList<string> AvailableModelIds, IReadOnlyList<string> PolicyModelIds);

    /// <summary>【AI】【账户目录解析】按 picker、工具能力和 policy 状态过滤，仅个人端点允许 policy 回退。</summary>
    /// <param name="raw">响应 JSON。</param><param name="allowPolicyFallback">是否个人端点。</param><param name="knownModels">本地可启用模型。</param><returns>可用与待启用模型 ID。</returns>
    internal static AccountCatalog ParseCatalog(JsonElement raw, bool allowPolicyFallback, IReadOnlySet<string> knownModels)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Invalid Copilot models response");
        var accounts = new List<(string Id, bool Picker, string? Policy)>();
        foreach (var item in data.EnumerateArray())
        {
            if (OAuthHttpClient.String(item, "id") is not { } id) continue;
            if (item.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("supports", out var supports) && supports.ValueKind == JsonValueKind.Object
                && supports.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.False) continue;
            var picker = item.TryGetProperty("model_picker_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            var policy = item.TryGetProperty("policy", out var field) ? OAuthHttpClient.String(field, "state") : null;
            accounts.Add((id, picker, policy));
        }
        var picked = accounts.Where(model => model.Picker && model.Policy != "disabled").Select(model => model.Id).ToArray();
        var fallback = allowPolicyFallback && picked.Length == 0;
        return new(fallback ? accounts.Where(model => model.Policy == "enabled").Select(model => model.Id).ToArray() : picked,
            accounts.Where(model => model.Policy == "unconfigured" && knownModels.Contains(model.Id) && (model.Picker || fallback)).Select(model => model.Id).ToArray());
    }

    /// <summary>【AI】【账户目录请求】读取带固定 API 版本的账户目录，刷新路径不重试 429。</summary>
    /// <param name="access">Copilot access。</param><param name="enterprise">企业域名。</param><param name="token">取消。</param><param name="maxRetries">最多重试次数。</param><returns>账户目录。</returns>
    private async Task<AccountCatalog> FetchModelsAsync(string access, string? enterprise, CancellationToken token, int maxRetries)
    {
        var baseUrl = GetBaseUrl(access, enterprise);
        var response = await FetchCatalogWithRetryAsync(baseUrl + "/models", access, false, token, maxRetries).ConfigureAwait(false);
        if (!response.IsSuccess) throw HttpError(response);
        return ParseCatalog(response.Body, baseUrl == "https://api.individual.githubcopilot.com", _knownModelIds);
    }

    /// <summary>【AI】【模型启用】普通失败跳过该模型，耗尽限速重试后停止整个启用批次，取消继续传播。</summary>
    /// <param name="access">访问令牌。</param><param name="ids">待启用模型。</param><param name="enterprise">企业域名。</param><param name="token">取消。</param><returns>成功启用模型。</returns>
    private async Task<List<string>> EnableModelsAsync(string access, IReadOnlyList<string> ids, string? enterprise, CancellationToken token)
    {
        var enabled = new List<string>();
        foreach (var id in ids)
        {
            try
            {
                var response = await FetchCatalogWithRetryAsync(GetBaseUrl(access, enterprise) + "/models/" + id + "/policy", access, true, token, 2).ConfigureAwait(false);
                if (response.Status == 429) break;
                if (response.IsSuccess) enabled.Add(id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { token.ThrowIfCancellationRequested(); }
        }
        return enabled;
    }

    /// <summary>【AI】【Copilot 限速】每次请求最多五秒；可重试流程共享五秒预算，遵守 Retry-After 和指数等待。</summary>
    /// <param name="url">目录或 policy 地址。</param><param name="access">访问令牌。</param><param name="policy">是否 policy POST。</param>
    /// <param name="token">调用方取消。</param><param name="maxRetries">最多重试次数。</param><returns>最后一次 HTTP 响应快照。</returns>
    private async Task<OAuthHttpResponse> FetchCatalogWithRetryAsync(string url, string access, bool policy, CancellationToken token, int maxRetries)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (maxRetries > 0) budget.CancelAfter(TimeSpan.FromSeconds(5));
        var deadline = _clock.NowMilliseconds() + 5000;
        for (var retry = 0; ; retry++)
        {
            using var request = new HttpRequestMessage(policy ? HttpMethod.Post : HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            foreach (var header in CopilotHeaders) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (policy)
            {
                request.Content = new StringContent("{\"state\":\"enabled\"}"); request.Content.Headers.ContentType = new("application/json");
                request.Headers.TryAddWithoutValidation("openai-intent", "chat-policy"); request.Headers.TryAddWithoutValidation("x-interaction-type", "chat-policy");
            }
            else { request.Headers.Accept.ParseAdd("application/json"); request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-06-01"); }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false);
            JsonElement json = default;
            try { using var document = JsonDocument.Parse(body); json = document.RootElement.Clone(); } catch (JsonException) { }
            var result = new OAuthHttpResponse((int)response.StatusCode, body, json, response.ReasonPhrase);
            if (result.Status != 429 || retry == maxRetries) return result;
            var retryAfter = response.Headers.TryGetValues("retry-after", out var values) ? string.Join(", ", values) : null;
            var delay = ParseRetryDelay(retryAfter, 500 * Math.Pow(2, retry), _clock.NowMilliseconds());
            if (delay is null || delay >= deadline - _clock.NowMilliseconds()) return result;
            // 1. 【AI】【重试资源】释放上次响应后等待，网络连接不随轮询间隔占用
            response.Dispose();
            await _clock.SleepAsync(delay.Value, budget.Token).ConfigureAwait(false);
        }
    }

    /// <summary>【AI】【Retry-After】支持浮点秒数前缀及 HTTP 日期，负等待归零，非法日期停止重试。</summary>
    /// <param name="value">响应头。</param><param name="fallback">指数等待。</param><param name="now">当前毫秒时间。</param><returns>等待毫秒，非法值为空。</returns>
    internal static double? ParseRetryDelay(string? value, double fallback, double now)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        var numeric = Regex.Match(value.TrimStart(), @"^[+-]?(?:Infinity|(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)");
        double delay;
        if (numeric.Success && double.TryParse(numeric.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) delay = seconds * 1000;
        else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) delay = date.ToUnixTimeMilliseconds() - now;
        else return null;
        return double.IsFinite(delay) ? Math.Max(0, delay) : null;
    }

    /// <summary>【AI】【模型权限保存】将可用模型 ID 保存为原生 JSON 数组，不退化为字符串元数据。</summary>
    /// <param name="credentials">基础凭据。</param><param name="ids">账户可用 ID。</param><returns>带目录权限的凭据。</returns>
    private static OAuthCredentials WithAvailableModels(OAuthCredentials credentials, IEnumerable<string> ids)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartArray(); foreach (var id in ids) writer.WriteStringValue(id); writer.WriteEndArray(); }
        using var document = JsonDocument.Parse(stream.ToArray());
        var properties = credentials.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties["availableModelIds"] = document.RootElement.Clone();
        return credentials with { Properties = properties };
    }

    /// <summary>【AI】【目录 HTTP 错误】保留状态及服务端诊断。</summary><param name="response">响应。</param><returns>错误。</returns>
    private static InvalidOperationException HttpError(OAuthHttpResponse response) => new($"{response.Status} {response.StatusText}: {response.Text}");
}
