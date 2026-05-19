using System.Diagnostics;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Runtime;

internal sealed class ConsoleOAuthLoginCallbacks : IOAuthLoginCallbacks
{
    public void OnAuth(string url, string? instructions = null)
    {
        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            Console.WriteLine(instructions);
        }

        Console.WriteLine($"Open this URL to authenticate:\n  {url}");
        TryOpenBrowser(url);
        Console.WriteLine();
    }

    public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false)
    {
        Console.Write($"{message} ");
        var input = Console.ReadLine() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(input))
        {
            throw new OperationCanceledException("Login cancelled (empty input).");
        }

        return Task.FromResult(input);
    }

    public void OnProgress(string message)
    {
        Console.WriteLine(message);
    }

    public Task<string>? OnManualCodeInputAsync() => null;

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
