// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【登录交互】将统一认证输入路由到宿主现有登录交互。</summary>
internal sealed class CodingAgentCallbackAuthInteraction(IOAuthLoginCallbacks callbacks, CancellationToken token) : AuthInteraction
{
    public CancellationToken CancellationToken => token;
    /// <summary>读取普通登录文本。</summary><param name="prompt">提示。</param><returns>输入。</returns>
    public Task<string> PromptAsync(string prompt) => callbacks.OnPromptAsync(prompt, null, false, token);
    /// <summary>按类型和独立取消信号读取输入。</summary><param name="prompt">提示。</param><returns>输入或选中 ID。</returns>
    public async Task<string> PromptAsync(ProviderAuthPrompt prompt)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token, prompt.Signal);
        return prompt.Type switch
        {
            "secret" => await callbacks.OnSecretPromptAsync(prompt.Message, prompt.Placeholder, source.Token).ConfigureAwait(false),
            "manual_code" => await callbacks.OnManualCodeInputAsync(prompt.Message, prompt.Placeholder, source.Token).ConfigureAwait(false),
            "select" => await callbacks.OnSelectAsync(prompt.Message, (prompt.Options ?? []).Select(option => new OAuthSelectOption(option.Id, option.Label, option.Description)).ToArray(), source.Token).WaitAsync(source.Token).ConfigureAwait(false)
                ?? throw new OperationCanceledException("Login cancelled."),
            _ => await callbacks.OnPromptAsync(prompt.Message, prompt.Placeholder, prompt.AllowEmpty, source.Token).ConfigureAwait(false)
        };
    }
    /// <summary>展示文本进度。</summary><param name="message">消息。</param>
    public void Notify(string message) => callbacks.OnProgress(message);
    /// <summary>展示授权、设备码或普通通知。</summary><param name="notification">通知。</param>
    public void Notify(ProviderAuthNotification notification)
    {
        if (notification.Type == "auth_url") callbacks.OnAuth(notification.Url!, notification.Instructions);
        else if (notification.Type == "info")
        {
            // 1. 【CodingAgent】【说明兼容】旧通知使用 Url/Instructions 字段，也要保留到新的说明与链接集合
            var links = notification.Url is { } url ? new List<ProviderAuthInfoLink>(notification.Links ?? []) { new(url) } : notification.Links;
            callbacks.OnInfo(string.Join("\n", new[] { notification.Message, notification.Instructions }.Where(value => !string.IsNullOrEmpty(value))), links);
        }
        else if (notification.Type == "device_code") callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(notification.UserCode!, notification.VerificationUri!, notification.IntervalSeconds, notification.ExpiresInSeconds));
        else callbacks.OnProgress(string.Join("\n", new[] { notification.Message, notification.Instructions, notification.Url }
            .Where(value => !string.IsNullOrEmpty(value))));
    }
}

/// <summary>【CodingAgent】【认证桥接】把工作进程登录回调转成公开的统一认证交互。</summary>
internal sealed class CodingAgentInteractionOAuthCallbacks(AuthInteraction interaction) : IOAuthLoginCallbacks
{
    /// <summary>【CodingAgent】【登录说明】保留 info 类型与说明链接。</summary><param name="message">说明。</param><param name="links">链接集合。</param>
    public void OnInfo(string message, IReadOnlyList<ProviderAuthInfoLink>? links = null) => interaction.Notify(new ProviderAuthNotification("info", message, Links: links));
    /// <summary>发布授权链接。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
    public void OnAuth(string url, string? instructions = null) => interaction.Notify(new ProviderAuthNotification("auth_url", Url: url, Instructions: instructions));
    /// <summary>发布设备码。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">有效期。</param>
    public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null) => interaction.Notify(new ProviderAuthNotification("device_code", UserCode: userCode, VerificationUri: verificationUri, IntervalSeconds: intervalSeconds, ExpiresInSeconds: expiresInSeconds));
    /// <summary>【CodingAgent】【精确设备码】传递原始小数及大数秒值。</summary><param name="notification">设备码通知。</param>
    public void OnDeviceCode(OAuthDeviceCodeNotification notification) => interaction.Notify(new ProviderAuthNotification("device_code",
        UserCode: notification.UserCode, VerificationUri: notification.VerificationUri, IntervalSeconds: notification.IntervalSeconds, ExpiresInSeconds: notification.ExpiresInSeconds));
    /// <summary>读取文本。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">兼容选项。</param><returns>输入。</returns>
    public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => OnPromptAsync(message, placeholder, allowEmpty, interaction.CancellationToken);
    /// <summary>读取可独立取消的文本。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">兼容选项。</param><param name="token">信号。</param><returns>输入。</returns>
    public Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token) => interaction.PromptAsync(new ProviderAuthPrompt("text", message, placeholder, Signal: token) { AllowEmpty = allowEmpty });
    /// <summary>读取秘密输入。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">信号。</param><returns>输入。</returns>
    public Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token) => interaction.PromptAsync(new ProviderAuthPrompt("secret", message, placeholder, Signal: token));
    /// <summary>读取选择结果。</summary><param name="message">提示。</param><param name="options">候选。</param><returns>选中 ID。</returns>
    public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options) => OnSelectAsync(message, options, interaction.CancellationToken);
    /// <summary>【CodingAgent】【选择提示取消】传递独立提示信号，使 Node 取消选择时关闭宿主输入。</summary>
    /// <param name="message">提示。</param><param name="options">候选项。</param><param name="token">输入信号。</param><returns>选中 ID。</returns>
    public async Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token) => await interaction.PromptAsync(new ProviderAuthPrompt("select", message,
        Options: options.Select(option => new ProviderAuthSelectOption(option.Id, option.Label, option.Description)).ToArray(), Signal: token)).WaitAsync(token).ConfigureAwait(false);
    /// <summary>发布进度。</summary><param name="message">消息。</param>
    public void OnProgress(string message) => interaction.Notify(new ProviderAuthNotification("progress", message));
    /// <summary>读取手动代码。</summary><returns>输入。</returns>
    public Task<string>? OnManualCodeInputAsync() => interaction.PromptAsync(new ProviderAuthPrompt("manual_code", "Paste the authorization code", Signal: interaction.CancellationToken));
    /// <summary>【CodingAgent】【手动授权输入】保留手动代码提示类型、占位符及独立取消信号。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">输入信号。</param><returns>输入文本。</returns>
    public Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token) => interaction.PromptAsync(new ProviderAuthPrompt("manual_code", message, placeholder, Signal: token));
}
