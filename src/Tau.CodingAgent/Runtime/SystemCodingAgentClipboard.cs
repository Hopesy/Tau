using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

public sealed class SystemCodingAgentClipboard : ICodingAgentClipboard
{
    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunClipboardCommandAsync("clip.exe", null, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            await RunClipboardCommandAsync("pbcopy", null, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        var failures = new List<string>();
        foreach (var candidate in new[] { ("wl-copy", (string?)null), ("xclip", "-selection clipboard") })
        {
            try
            {
                await RunClipboardCommandAsync(candidate.Item1, candidate.Item2, text, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                failures.Add($"{candidate.Item1}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"No clipboard command succeeded. Install wl-copy or xclip. {string.Join("; ", failures)}");
    }

    private static async Task RunClipboardCommandAsync(
        string fileName,
        string? arguments,
        string text,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"{fileName} is not available", ex);
        }

        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
