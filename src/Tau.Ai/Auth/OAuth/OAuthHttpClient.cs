// 作者：xxx
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【OAuth HTTP】已释放 HTTP 响应对应的独立状态、原始文本与 JSON 快照。</summary>
/// <param name="Status">HTTP 状态。</param><param name="Text">响应文本。</param><param name="Body">可选 JSON，解析失败为 Undefined。</param><param name="StatusText">HTTP 状态说明。</param>
internal sealed record OAuthHttpResponse(int Status, string Text, JsonElement Body, string? StatusText = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;
    public string JsonText => Body.ValueKind == JsonValueKind.Undefined ? "null" : Body.GetRawText();
}

/// <summary>【AI】【OAuth HTTP】设备码认证共用的有界 HTTP 请求与响应读取。</summary>
/// <param name="client">HTTP 客户端。</param><param name="timeout">单次请求期限。</param>
internal sealed class OAuthHttpClient(HttpClient client, TimeSpan timeout)
{
    /// <summary>【AI】【OAuth HTTP】在独立期限内读取响应体，不让解析失败覆盖提供方错误状态。</summary>
    /// <param name="request">由调用方释放的请求。</param><param name="token">流程取消信号。</param><returns>独立响应快照。</returns>
    internal async Task<OAuthHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        JsonElement json = default;
        try { using var document = JsonDocument.Parse(text); json = document.RootElement.Clone(); }
        catch (JsonException) { }
        return new((int)response.StatusCode, text, json, response.ReasonPhrase);
    }

    /// <summary>【AI】【OAuth HTTP】发送表单请求并保留 OAuth 错误状态。</summary>
    /// <param name="url">地址。</param><param name="fields">表单字段。</param><param name="token">取消信号。</param><returns>响应。</returns>
    internal async Task<OAuthHttpResponse> PostFormAsync(string url, IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        return await SendAsync(request, token).ConfigureAwait(false);
    }

    /// <summary>【AI】【OAuth JSON】安全读取字符串字段。</summary><param name="body">响应。</param><param name="field">字段。</param><returns>字符串或空。</returns>
    internal static string? String(JsonElement body, string field) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>【AI】【OAuth JSON】只接收有限正数。</summary><param name="body">响应。</param><param name="field">字段。</param><returns>数值或空。</returns>
    internal static double? PositiveNumber(JsonElement body, string field) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0 ? number : null;

    /// <summary>【AI】【OAuth 地址】浏览器验证地址只接受 HTTP 或 HTTPS。</summary><param name="value">地址。</param><returns>规范地址或空。</returns>
    internal static string? TrustedHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
}
