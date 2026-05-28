using System.Diagnostics;
using Tau.Ai.Auth.OAuth;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

internal sealed class InteractiveOAuthLoginCallbacks(InteractiveConsoleSession ui) : IOAuthLoginCallbacks, IOAuthManualCodeInputController
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

    public async Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false)
    {
        var prompt = string.IsNullOrWhiteSpace(placeholder)
            ? message
            : $"{message} [{placeholder}]";
        ui.WriteStatus(prompt);

        var result = await ui.ReadInputResultAsync(OAuthPrompt, OAuthPromptColor).ConfigureAwait(false);
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
