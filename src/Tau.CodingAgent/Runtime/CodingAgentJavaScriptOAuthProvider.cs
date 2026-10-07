// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展认证】将扩展登录、刷新和密钥派生接入会话认证解析器。</summary>
internal sealed class CodingAgentJavaScriptOAuthProvider(
    CodingAgentJavaScriptExtensionRuntime runtime, string filePath, string id, string name, int version, long generation,
    bool usesCallbackServer, bool isSubscription, string? loginLabel) : IOAuthProvider
{
    public string Id => id;
    public string Name => name;
    public bool UsesCallbackServer => usesCallbackServer;
    public bool IsSubscription => isSubscription;
    public string? LoginLabel => loginLabel;

    /// <summary>【CodingAgent】【OAuth 登录】执行扩展登录并转发所有交互回调。</summary>
    /// <param name="callbacks">宿主交互。</param><param name="cancellationToken">取消信号。</param><returns>完整凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
        OAuthCredentialJson.Read(await runtime.InvokeProviderAuthAsync(filePath, id, version, generation, "login", null, callbacks, cancellationToken).ConfigureAwait(false));

    /// <summary>【CodingAgent】【OAuth 登录选项】把同步惰性安装身份查询传给原生或兼容扩展。</summary>
    /// <param name="callbacks">宿主交互。</param><param name="options">可选安装标识回调。</param><param name="cancellationToken">取消信号。</param><returns>凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default) =>
        OAuthCredentialJson.Read(await runtime.InvokeProviderAuthAsync(filePath, id, version, generation, "login", null, callbacks, cancellationToken, loginOptions: options).ConfigureAwait(false));

    /// <summary>【CodingAgent】【OAuth 刷新】刷新令牌并保留扩展附加字段。</summary>
    /// <param name="credentials">旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>新凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
        OAuthCredentialJson.Read(await runtime.InvokeProviderAuthAsync(filePath, id, version, generation, "refreshToken", credentials, null, cancellationToken).ConfigureAwait(false));

    /// <summary>【CodingAgent】【OAuth 密钥】通过扩展从完整凭据派生请求密钥。</summary>
    /// <param name="credentials">完整凭据。</param><returns>请求密钥。</returns>
    public string GetApiKey(OAuthCredentials credentials) => runtime.InvokeProviderAuthAsync(filePath, id, version, generation, "getApiKey", credentials, null, default)
        .GetAwaiter().GetResult().GetString() ?? throw new InvalidOperationException("OAuth getApiKey must return a string.");

    /// <summary>【CodingAgent】【OAuth 请求】派生动态地址及请求头，支持没有 API key 的认证形式。</summary>
    /// <param name="credentials">凭据。</param><param name="token">取消信号。</param><returns>完整请求认证。</returns>
    public ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default)
    {
        var value = runtime.InvokeProviderAuthAsync(filePath, id, version, generation, "toAuth", credentials, null, token).GetAwaiter().GetResult();
        return new(value.TryGetProperty("apiKey", out var key) ? key.GetString() : null,
            value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                ? headers.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.OrdinalIgnoreCase) : null,
            value.TryGetProperty("baseUrl", out var url) ? url.GetString() : null);
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    private readonly ConcurrentDictionary<string, IOAuthLoginCallbacks> _providerAuthCallbacks = new();
    private readonly ConcurrentDictionary<string, CodingAgentOAuthDeviceReply> _providerAuthDeviceReplies = new();

    /// <summary>【CodingAgent】【OAuth 桥接】执行认证回调，使用不包含模型快照的通道避免认证递归。</summary>
    /// <param name="filePath">扩展文件。</param><param name="provider">提供方。</param><param name="version">注册版本。</param>
    /// <param name="generation">运行时代次。</param><param name="operation">认证动作。</param><param name="credentials">输入凭据。</param>
    /// <param name="callbacks">可选登录交互。</param><param name="token">取消信号。</param><param name="fields">附加协议字段。</param>
    /// <param name="loginOptions">可选惰性安装标识。</param><returns>认证返回值。</returns>
    internal async Task<JsonElement> InvokeProviderAuthAsync(string filePath, string provider, int version, long generation, string operation,
        OAuthCredentials? credentials, IOAuthLoginCallbacks? callbacks, CancellationToken token, JsonObject? fields = null, OAuthLoginOptions? loginOptions = null)
    {
        var callId = Guid.NewGuid().ToString("N");
        if (callbacks is not null) _providerAuthCallbacks[callId] = callbacks;
        using var deviceReply = loginOptions?.GetDeviceId is { } getDeviceId ? new CodingAgentOAuthDeviceReply(getDeviceId) : null;
        if (deviceReply is not null) _providerAuthDeviceReplies[callId] = deviceReply;
        try
        {
            var payload = new JsonObject { ["providerId"] = provider, ["version"] = version, ["callId"] = callId, ["operation"] = operation,
                ["credentials"] = credentials is null ? null : JsonNode.Parse(OAuthCredentialJson.Write(credentials).GetRawText()) };
            if (deviceReply is not null) payload["deviceIdReplyPath"] = deviceReply.ReplyPath;
            if (fields is not null) foreach (var (key, value) in fields) payload[key] = value?.DeepClone();
            using var request = JsonDocument.Parse(payload.ToJsonString());
            var response = await ExecuteAsync(BuildPayload("providerAuth", filePath, _cwd, toolArgs: request.RootElement), token,
                expectedGeneration: generation, timeout: operation is "getApiKey" or "filterModels" ? _timeout : Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!response.Success) throw new InvalidOperationException(response.Error);
            using var result = JsonDocument.Parse(response.ResultJson);
            if (!ReadBool(result.RootElement, "ok")) throw new InvalidOperationException(ReadString(result.RootElement, "error"));
            return result.RootElement.GetProperty("value").Clone();
        }
        finally
        {
            _providerAuthCallbacks.TryRemove(callId, out _);
            _providerAuthDeviceReplies.TryRemove(callId, out _);
            if (callbacks is IOAuthManualCodeInputController controller) controller.CancelManualCodeInput();
        }
    }

    /// <summary>【CodingAgent】【OAuth 同步身份】仅发布宿主为当前登录分配的路径，不接受扩展指定写入地址。</summary>
    /// <param name="request">身份读取请求。</param><param name="token">登录取消信号。</param><returns>普通通道确认。</returns>
    private string HandleProviderDeviceId(JsonElement request, CancellationToken token)
    {
        var id = ReadString(request, "id");
        if (_providerAuthDeviceReplies.TryGetValue(ReadString(request, "callId") ?? "", out var reply))
        {
            reply.Publish(token);
            return JsonSerializer.Serialize(new { id, ok = true });
        }
        return JsonSerializer.Serialize(new { id, ok = false, error = "OAuth login is no longer active." });
    }

    /// <summary>【CodingAgent】【OAuth 交互】转发授权链接、设备码、输入、选择及进度，响应中不附带会话凭据。</summary>
    /// <param name="request">内部交互请求。</param><param name="token">取消信号。</param><returns>最小宿主响应。</returns>
    private async Task<string> HandleProviderAuthCallbackAsync(JsonElement request, CancellationToken token)
    {
        var id = ReadString(request, "id");
        try
        {
            if (!_providerAuthCallbacks.TryGetValue(request.GetProperty("callId").GetString()!, out var callbacks)) throw new InvalidOperationException("OAuth login is no longer active.");
            token.ThrowIfCancellationRequested();
            var value = request.GetProperty("value");
            string? result = null;
            switch (ReadString(request, "kind"))
            {
                case "auth": callbacks.OnAuth(value.GetProperty("url").GetString()!, ReadString(value, "instructions")); break;
                case "deviceCode": callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(value.GetProperty("userCode").GetString()!, value.GetProperty("verificationUri").GetString()!,
                    value.TryGetProperty("intervalSeconds", out var interval) && interval.ValueKind == JsonValueKind.Number ? interval.GetDouble() : null,
                    value.TryGetProperty("expiresInSeconds", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetDouble() : null)); break;
                case "progress": callbacks.OnProgress(value.GetString()!); break;
                case "info": callbacks.OnInfo(value.GetProperty("message").GetString()!,
                    value.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
                        ? links.EnumerateArray().Select(link => new ProviderAuthInfoLink(link.GetProperty("url").GetString()!, ReadString(link, "label"))).ToArray() : null); break;
                case "prompt": result = await callbacks.OnPromptAsync(value.GetProperty("message").GetString()!, ReadString(value, "placeholder"), ReadBool(value, "allowEmpty"), token).ConfigureAwait(false); break;
                case "secret": result = await callbacks.OnSecretPromptAsync(value.GetProperty("message").GetString()!, ReadString(value, "placeholder"), token).WaitAsync(token).ConfigureAwait(false); break;
                case "manual": result = await (callbacks.OnManualCodeInputAsync() ?? Task.FromResult(string.Empty)).WaitAsync(token).ConfigureAwait(false); break;
                case "manualTyped": result = await callbacks.OnManualCodeInputAsync(value.GetProperty("message").GetString()!, ReadString(value, "placeholder"), token).WaitAsync(token).ConfigureAwait(false); break;
                case "select": result = await callbacks.OnSelectAsync(value.GetProperty("message").GetString()!, value.GetProperty("options").EnumerateArray()
                    .Select(option => new OAuthSelectOption(option.GetProperty("id").GetString()!, option.GetProperty("label").GetString()!, ReadString(option, "description"))).ToArray(), token).WaitAsync(token).ConfigureAwait(false); break;
                default: throw new InvalidOperationException("Unknown OAuth callback.");
            }
            return JsonSerializer.Serialize(new { id, ok = true, value = result });
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { id, ok = false, error = error.Message }); }
    }
}
