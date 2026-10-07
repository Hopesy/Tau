namespace Tau.Ai.Auth.OAuth;

public interface IOAuthLoginCallbacks
{
    void OnAuth(string url, string? instructions = null);
    Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false);
    void OnProgress(string message);
    Task<string>? OnManualCodeInputAsync();

    /// <summary>【AI】【登录说明】向类型化宿主传递说明链接，旧宿主默认显示完整文本。</summary>
    /// <param name="message">说明文字。</param><param name="links">带可选标签的链接。</param>
    void OnInfo(string message, IReadOnlyList<ProviderAuthInfoLink>? links = null) => OnProgress(new ProviderAuthNotification("info", message, Links: links).ToDisplayText());

    /// <summary>【AI】【OAuth 输入】为手动回调 URL 提供可独立取消的类型化提示。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">输入取消信号。</param><returns>输入文本。</returns>
    Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token) => OnPromptAsync(message, placeholder, true, token);

    /// <summary>【AI】【OAuth 输入】接收可独立取消的普通输入，旧实现默认取消等待。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">允许空值。</param>
    /// <param name="token">单次输入取消信号。</param><returns>输入文本。</returns>
    Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token) => OnPromptAsync(message, placeholder, allowEmpty).WaitAsync(token);

    /// <summary>【AI】【OAuth 输入】接收不应显示或保存到输入历史的秘密文本。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">取消信号。</param><returns>秘密文本。</returns>
    Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token) =>
        throw new NotSupportedException("The login callback must implement secret input.");

    /// <summary>【AI】【OAuth 登录】展示设备代码；旧回调实现默认通过进度及授权链接展示。</summary>
    /// <param name="userCode">设备代码。</param><param name="verificationUri">验证地址。</param>
    /// <param name="intervalSeconds">轮询间隔。</param><param name="expiresInSeconds">有效秒数。</param>
    void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null)
    {
        OnProgress(userCode);
        OnAuth(verificationUri);
    }

    /// <summary>【AI】【精确设备码通知】保留原生秒数；旧宿主回调使用向上取整且不会溢出的兼容投影。</summary>
    /// <param name="notification">设备码、授权地址及原始秒数。</param>
    void OnDeviceCode(OAuthDeviceCodeNotification notification) => OnDeviceCode(notification.UserCode, notification.VerificationUri,
        OAuthDeviceCodeNotification.ToLegacySeconds(notification.IntervalSeconds), OAuthDeviceCodeNotification.ToLegacySeconds(notification.ExpiresInSeconds));

    /// <summary>【AI】【OAuth 登录】选择登录目标，默认使用文本提示并允许取消。</summary>
    /// <param name="message">提示。</param><param name="options">候选 ID 与名称。</param><returns>选中 ID，取消为空。</returns>
    async Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options)
    {
        var answer = await OnPromptAsync(message + "\n" + string.Join("\n", options.Select(option => $"{option.Id}: {option.Label}" + (option.Description is { Length: > 0 } description ? " — " + description : ""))), allowEmpty: true).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }

    /// <summary>【AI】【选择提示取消】为单个选择提示传递取消信号，默认保留旧选择回调实现。</summary>
    /// <param name="message">选择提示。</param><param name="options">候选项。</param><param name="token">本次提示信号。</param><returns>选中 ID 或空值。</returns>
    async Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await OnSelectAsync(message, options).WaitAsync(token).ConfigureAwait(false);
    }
}

/// <summary>【AI】【OAuth 登录】可选择的目标标识及名称。</summary>
public sealed record OAuthSelectOption(string Id, string Label, string? Description = null);

/// <summary>【AI】【设备码通知】允许 JavaScript 数字精度的轮询间隔和有效期。</summary>
/// <param name="UserCode">设备代码。</param><param name="VerificationUri">验证地址。</param>
/// <param name="IntervalSeconds">轮询间隔。</param><param name="ExpiresInSeconds">有效秒数。</param>
public sealed record OAuthDeviceCodeNotification(string UserCode, string VerificationUri, double? IntervalSeconds = null, double? ExpiresInSeconds = null)
{
    /// <summary>【AI】【兼容秒数】向上取整为旧接口整数，超出范围或非有限值不传递。</summary>
    /// <param name="seconds">原始秒数。</param><returns>兼容整数或空值。</returns>
    internal static int? ToLegacySeconds(double? seconds) => seconds is >= int.MinValue and <= int.MaxValue ? (int)Math.Ceiling(seconds.Value) : null;
}

public interface IOAuthManualCodeInputController
{
    void CancelManualCodeInput();
}
