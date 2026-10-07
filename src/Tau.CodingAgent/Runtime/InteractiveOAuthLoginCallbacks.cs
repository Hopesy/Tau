using System.Diagnostics;
using Tau.Ai.Auth.OAuth;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

internal sealed class InteractiveOAuthLoginCallbacks(InteractiveConsoleSession ui,
    Func<string, IReadOnlyList<OAuthSelectOption>, CancellationToken, Task<string?>>? selector = null) : IOAuthLoginCallbacks, IOAuthManualCodeInputController
{
    private const string OAuthPrompt = "oauth> ";
    private const ConsoleColor OAuthPromptColor = ConsoleColor.Cyan;
    private CancellationTokenSource? _manualCodeCancellation;
    private bool _manualCodeCancelledByController;

    public void OnAuth(string url, string? instructions = null)
    {
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            ui.WriteStatus(instructions);
        }

        ui.WriteStatus($"Open this URL to authenticate: {url}");
        TryOpenBrowser(url);
    }

    public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => OnPromptAsync(message, placeholder, allowEmpty, default);

    /// <summary>【CodingAgent】【认证输入】在原输入组件中读取文本，接受单次提示取消。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">是否允许空值。</param>
    /// <param name="token">提示取消信号。</param><returns>输入文本。</returns>
    public async Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token)
    {
        var prompt = string.IsNullOrWhiteSpace(placeholder)
            ? message
            : $"{message} [{placeholder}]";
        ui.WriteStatus(prompt);

        var result = await ui.ReadInputResultAsync(OAuthPrompt, OAuthPromptColor, token).ConfigureAwait(false);
        if (result.Kind != InputResultKind.Submitted)
        {
            throw new OperationCanceledException("Login cancelled.");
        }

        var input = result.Text ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(input))
        {
            throw new OperationCanceledException("Login cancelled (empty input).");
        }

        return input;
    }

    public void OnProgress(string message)
    {
        ui.WriteStatus(message);
    }

    /// <summary>【CodingAgent】【设备授权】显示设备链接与代码，不强制在当前机器启动浏览器。</summary>
    /// <param name="notification">设备授权通知。</param>
    public void OnDeviceCode(OAuthDeviceCodeNotification notification)
    {
        ui.WriteStatus(notification.VerificationUri);
        ui.WriteStatus("Enter code: " + notification.UserCode);
    }

    /// <summary>【CodingAgent】【选择提示取消】展示候选说明，并将单次取消信号送到交互编辑器。</summary>
    /// <param name="message">选择提示。</param><param name="options">候选项。</param><param name="token">本次提示信号。</param><returns>选中 ID 或空值。</returns>
    public async Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.Count == 0) return null;
        if (selector is not null)
        {
            var selected = await selector(message, options, token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return selected ?? throw new OperationCanceledException("Login cancelled", token);
        }
        var choices = string.Join("\n", options.Select(option => option.Id + ": " + option.Label
            + (option.Description is { Length: > 0 } description ? " — " + description : "")));
        var answer = await OnPromptAsync(message + "\n" + choices, null, true, token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(answer) ? options[0].Id : answer;
    }

    /// <summary>【CodingAgent】【认证输入】通过独立输入通道读取秘密值，避免进入编辑器历史。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位符。</param><param name="token">取消信号。</param><returns>秘密值。</returns>
    public Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token)
    {
        ui.WriteStatus(message);
        return CodingAgentSecretInput.ReadAsync(token);
    }

    public Task<string>? OnManualCodeInputAsync()
    {
        CancelManualCodeInput();
        _manualCodeCancelledByController = false;
        _manualCodeCancellation = new CancellationTokenSource();
        return ReadManualCodeInputAsync(_manualCodeCancellation.Token);
    }

    public void CancelManualCodeInput()
    {
        if (_manualCodeCancellation is null)
        {
            return;
        }

        try
        {
            _manualCodeCancelledByController = true;
            _manualCodeCancellation.Cancel();
        }
        catch
        {
        }
        finally
        {
            _manualCodeCancellation.Dispose();
            _manualCodeCancellation = null;
        }
    }

    private async Task<string> ReadManualCodeInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ui.WriteStatus("Paste the authorization code or full redirect URL here to complete login manually.");
                var result = await ui.ReadInputResultAsync(OAuthPrompt, OAuthPromptColor, cancellationToken).ConfigureAwait(false);
                if (result.Kind != InputResultKind.Submitted)
                {
                    throw new OperationCanceledException("Login cancelled.");
                }

                var input = result.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(input))
                {
                    return input;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_manualCodeCancelledByController)
            {
                return string.Empty;
            }

            throw;
        }
        finally
        {
            if (_manualCodeCancellation?.Token == cancellationToken)
            {
                _manualCodeCancellation.Dispose();
                _manualCodeCancellation = null;
            }

            _manualCodeCancelledByController = false;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
        }
    }
}
