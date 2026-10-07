// 作者：xxx
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed partial class OpenAICodexOAuthProvider
{
    /// <summary>【AI】【Codex 设备码】申请代码后立即轮询，十五分钟内成功则用设备专用回调兑换令牌。</summary>
    /// <param name="callbacks">设备通知交互。</param><param name="token">取消信号。</param><returns>账户凭据。</returns>
    private async Task<OAuthCredentials> LoginDeviceCodeAsync(IOAuthLoginCallbacks callbacks, CancellationToken token)
    {
        var response = await PostDeviceAsync("https://auth.openai.com/api/accounts/deviceauth/usercode", new JsonObject { ["client_id"] = ClientId }, token, [404]).ConfigureAwait(false);
        if (response.Status == 404) throw new InvalidOperationException("OpenAI Codex device code login is not enabled for this server. Use browser login or verify the server URL.");
        if (!response.IsSuccess) throw new InvalidOperationException($"OpenAI Codex device code request failed with status {response.Status}{(response.Text.Length > 0 ? ": " + response.Text : "")}");
        var body = RequireJson(response); var device = OAuthHttpClient.String(body, "device_auth_id"); var user = OAuthHttpClient.String(body, "user_code");
        var interval = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("interval", out var value) ? ParseInterval(value) : null;
        if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(user) || interval is null)
            throw new InvalidOperationException("Invalid OpenAI Codex device code response: " + response.JsonText);
        callbacks.OnDeviceCode(new(user, "https://auth.openai.com/codex/device", interval, 900));
        token.ThrowIfCancellationRequested();
        // 1. 【AI】【Codex 设备轮询】首轮立即请求，403/404 等待，slow_down 增加五秒
        var code = await OAuthDeviceCodePoller.PollAsync(() => PollDeviceAsync(device, user, token), token,
            intervalSeconds: interval, expiresInSeconds: 900, clock: _clock).ConfigureAwait(false);
        return await ExchangeAsync(code.Code, code.Verifier, "https://auth.openai.com/deviceauth/callback", token).ConfigureAwait(false);
    }

    /// <summary>【AI】【Codex 设备轮询】识别 HTTP 等待状态以及字符串或嵌套对象错误码。</summary>
    /// <param name="device">设备授权 ID。</param><param name="user">用户代码。</param><param name="token">取消信号。</param><returns>轮询状态。</returns>
    private async Task<OAuthDeviceCodePollResult<DeviceAuthorization>> PollDeviceAsync(string device, string user, CancellationToken token)
    {
        var response = await PostDeviceAsync("https://auth.openai.com/api/accounts/deviceauth/token",
            new JsonObject { ["device_auth_id"] = device, ["user_code"] = user }, token, [403, 404]).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            var body = RequireJson(response); var code = OAuthHttpClient.String(body, "authorization_code"); var verifier = OAuthHttpClient.String(body, "code_verifier");
            return !string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(verifier) ? new("complete", new(code, verifier))
                : new("failed", Message: "Invalid OpenAI Codex device auth token response: " + response.JsonText);
        }
        if (response.Status is 403 or 404) return new("pending");
        var error = OAuthHttpClient.String(response.Body, "error") ?? (response.Body.ValueKind == JsonValueKind.Object && response.Body.TryGetProperty("error", out var nested)
            ? OAuthHttpClient.String(nested, "code") : null);
        return error switch
        {
            "deviceauth_authorization_pending" => new("pending"),
            "slow_down" => new("slow_down"),
            _ => new("failed", Message: $"OpenAI Codex device auth failed with status {response.Status}{(response.Text.Length > 0 ? ": " + response.Text : "")}")
        };
    }

    /// <summary>【AI】【Codex 间隔】按 JavaScript Number 语义接受非负数及数字字符串，包括空串和进制前缀。</summary>
    /// <param name="value">原始间隔。</param><returns>有限非负秒数，其他值为空。</returns>
    internal static double? ParseInterval(JsonElement value)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number) { if (!value.TryGetDouble(out number)) return null; }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (text.Length == 0) return 0;
            if (text.Length > 2 && text[0] == '0' && char.ToLowerInvariant(text[1]) is 'x' or 'o' or 'b')
            {
                var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'o' => 8, _ => 2 }; number = 0;
                foreach (var character in text.AsSpan(2))
                {
                    var digit = character is >= '0' and <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
                    if (digit < 0 || digit >= radix) return null;
                    number = number * radix + digit;
                }
            }
            else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        }
        else return null;
        return double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>【AI】【设备授权结果】服务端发出的授权码与 PKCE 校验器。</summary><param name="Code">授权码。</param><param name="Verifier">校验器。</param>
    private sealed record DeviceAuthorization(string Code, string Verifier);
}
