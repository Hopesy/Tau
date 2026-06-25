using System.Diagnostics;
using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed class SystemCodingAgentClipboard : ICodingAgentClipboard
{
    private const int ListTimeoutMs = 1_000;
    private const int ReadTimeoutMs = 3_000;
    private const int PowerShellTimeoutMs = 5_000;
    private const int MaxBufferBytes = 50 * 1024 * 1024;

    private static readonly string[] SupportedImageMimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    private readonly ICodingAgentClipboardCommandRunner _runner;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly CodingAgentClipboardPlatform _platform;
    private readonly Func<string> _tempPngPathFactory;

    public SystemCodingAgentClipboard()
        : this(
            new SystemCodingAgentClipboardCommandRunner(),
            CaptureEnvironment(),
            GetCurrentPlatform(),
            static () => Path.Combine(Path.GetTempPath(), $"tau-clipboard-{Guid.NewGuid():N}.png"))
    {
    }

    internal SystemCodingAgentClipboard(
        ICodingAgentClipboardCommandRunner runner,
        IReadOnlyDictionary<string, string?> environment,
        CodingAgentClipboardPlatform platform,
        Func<string>? tempPngPathFactory = null)
    {
        _runner = runner;
        _environment = NormalizeEnvironment(environment);
        _platform = platform;
        _tempPngPathFactory = tempPngPathFactory ??
            (static () => Path.Combine(Path.GetTempPath(), $"tau-clipboard-{Guid.NewGuid():N}.png"));
    }

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

    public async Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default)
    {
        if (HasEnv("TERMUX_VERSION"))
        {
            return null;
        }

        CodingAgentClipboardImage? image = null;
        if (_platform == CodingAgentClipboardPlatform.Linux)
        {
            var wsl = IsWsl();
            var wayland = IsWaylandSession(_environment);

            if (wayland || wsl)
            {
                image = await ReadImageViaWlPasteAsync(cancellationToken).ConfigureAwait(false) ??
                    await ReadImageViaXclipAsync(cancellationToken).ConfigureAwait(false);
            }

            if (image is null && wsl)
            {
                image = await ReadImageViaPowerShellAsync(wsl: true, cancellationToken).ConfigureAwait(false);
            }

            if (image is null && !wayland)
            {
                image = await ReadImageViaXclipAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (_platform == CodingAgentClipboardPlatform.Windows)
        {
            image = await ReadImageViaPowerShellAsync(wsl: false, cancellationToken).ConfigureAwait(false);
        }

        if (image is null)
        {
            return null;
        }

        if (IsSupportedImageMimeType(image.MimeType))
        {
            return new CodingAgentClipboardImage(image.Bytes, BaseMimeType(image.MimeType));
        }

        if (!BaseMimeType(image.MimeType).StartsWith("image/", StringComparison.Ordinal))
        {
            return null;
        }

        var converted = CodingAgentImageConverter.ConvertToPng(Convert.ToBase64String(image.Bytes), image.MimeType);
        return converted is null
            ? null
            : new CodingAgentClipboardImage(Convert.FromBase64String(converted.Data), converted.MimeType);
    }

    internal static bool IsWaylandSession(IReadOnlyDictionary<string, string?> environment) =>
        HasEnvironmentValue(environment, "WAYLAND_DISPLAY") ||
        string.Equals(GetEnvironmentValue(environment, "XDG_SESSION_TYPE"), "wayland", StringComparison.Ordinal);

    internal static string? ExtensionForImageMimeType(string mimeType) =>
        BaseMimeType(mimeType) switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => null
        };

    private async Task<CodingAgentClipboardImage?> ReadImageViaWlPasteAsync(CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(
            "wl-paste",
            ["--list-types"],
            stdin: null,
            ListTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);
        if (!list.Ok)
        {
            return null;
        }

        var selectedType = SelectPreferredImageMimeType(SplitLines(list.Stdout));
        if (selectedType is null)
        {
            return null;
        }

        var data = await _runner.RunAsync(
            "wl-paste",
            ["--type", selectedType, "--no-newline"],
            stdin: null,
            ReadTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);
        return data.Ok && data.Stdout.Length > 0
            ? new CodingAgentClipboardImage(data.Stdout, BaseMimeType(selectedType))
            : null;
    }

    private async Task<CodingAgentClipboardImage?> ReadImageViaXclipAsync(CancellationToken cancellationToken)
    {
        var targets = await _runner.RunAsync(
            "xclip",
            ["-selection", "clipboard", "-t", "TARGETS", "-o"],
            stdin: null,
            ListTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);

        var candidateTypes = targets.Ok ? SplitLines(targets.Stdout) : [];
        var preferred = candidateTypes.Count > 0 ? SelectPreferredImageMimeType(candidateTypes) : null;
        var tryTypes = CreateDistinctTypes(preferred is null ? SupportedImageMimeTypes : [preferred, .. SupportedImageMimeTypes]);

        foreach (var mimeType in tryTypes)
        {
            var data = await _runner.RunAsync(
                "xclip",
                ["-selection", "clipboard", "-t", mimeType, "-o"],
                stdin: null,
                ReadTimeoutMs,
                MaxBufferBytes,
                cancellationToken).ConfigureAwait(false);
            if (data.Ok && data.Stdout.Length > 0)
            {
                return new CodingAgentClipboardImage(data.Stdout, BaseMimeType(mimeType));
            }
        }

        return null;
    }

    private async Task<CodingAgentClipboardImage?> ReadImageViaPowerShellAsync(
        bool wsl,
        CancellationToken cancellationToken)
    {
        var tmpFile = _tempPngPathFactory();
        var pathForPowerShell = tmpFile;

        try
        {
            if (wsl)
            {
                var winPath = await _runner.RunAsync(
                    "wslpath",
                    ["-w", tmpFile],
                    stdin: null,
                    ListTimeoutMs,
                    MaxBufferBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!winPath.Ok)
                {
                    return null;
                }

                pathForPowerShell = Encoding.UTF8.GetString(winPath.Stdout).Trim();
                if (string.IsNullOrWhiteSpace(pathForPowerShell))
                {
                    return null;
                }
            }

            var script = CreatePowerShellImageReadScript(pathForPowerShell);
            var result = await _runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-STA", "-Command", script],
                stdin: null,
                PowerShellTimeoutMs,
                MaxBufferBytes,
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok || !string.Equals(Encoding.UTF8.GetString(result.Stdout).Trim(), "ok", StringComparison.Ordinal))
            {
                return null;
            }

            if (!File.Exists(tmpFile))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(tmpFile, cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0 ? null : new CodingAgentClipboardImage(bytes, "image/png");
        }
        finally
        {
            try
            {
                File.Delete(tmpFile);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private bool IsWsl() => HasEnv("WSL_DISTRO_NAME") || HasEnv("WSLENV");

    private bool HasEnv(string name) => HasEnvironmentValue(_environment, name);

    private static string CreatePowerShellImageReadScript(string path)
    {
        var quotedPath = path.Replace("'", "''", StringComparison.Ordinal);
        return string.Join(
            "; ",
            "Add-Type -AssemblyName System.Windows.Forms",
            "Add-Type -AssemblyName System.Drawing",
            $"$path = '{quotedPath}'",
            "$img = [System.Windows.Forms.Clipboard]::GetImage()",
            "if ($img) { $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); Write-Output 'ok' } else { Write-Output 'empty' }");
    }

    private static IReadOnlyList<string> SplitLines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? SelectPreferredImageMimeType(IEnumerable<string> mimeTypes)
    {
        var normalized = mimeTypes
            .Select(static raw => new { Raw = raw.Trim(), Base = BaseMimeType(raw) })
            .Where(static item => item.Raw.Length > 0)
            .ToList();

        foreach (var preferred in SupportedImageMimeTypes)
        {
            var match = normalized.FirstOrDefault(item => item.Base == preferred);
            if (match is not null)
            {
                return match.Raw;
            }
        }

        return normalized.FirstOrDefault(static item => item.Base.StartsWith("image/", StringComparison.Ordinal))?.Raw;
    }

    private static IReadOnlyList<string> CreateDistinctTypes(IEnumerable<string> mimeTypes) =>
        mimeTypes
            .Where(static mimeType => !string.IsNullOrWhiteSpace(mimeType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsSupportedImageMimeType(string mimeType) =>
        SupportedImageMimeTypes.Contains(BaseMimeType(mimeType), StringComparer.Ordinal);

    private static string BaseMimeType(string mimeType)
    {
        var separator = mimeType.IndexOf(';', StringComparison.Ordinal);
        var value = separator >= 0 ? mimeType[..separator] : mimeType;
        return value.Trim().ToLowerInvariant();
    }

    private static bool HasEnvironmentValue(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value);

    private static string GetEnvironmentValue(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;

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

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            values[(string)entry.Key] = entry.Value?.ToString();
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string?> NormalizeEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            values[pair.Key] = pair.Value;
        }

        return values;
    }

    private static CodingAgentClipboardPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CodingAgentClipboardPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return CodingAgentClipboardPlatform.MacOS;
        }

        return CodingAgentClipboardPlatform.Linux;
    }
}

internal enum CodingAgentClipboardPlatform
{
    Windows,
    MacOS,
    Linux
}

internal sealed record CodingAgentClipboardCommandResult(bool Ok, byte[] Stdout, string Stderr);

internal interface ICodingAgentClipboardCommandRunner
{
    Task<CodingAgentClipboardCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? stdin,
        int timeoutMs,
        int maxBufferBytes,
        CancellationToken cancellationToken);
}

internal sealed class SystemCodingAgentClipboardCommandRunner : ICodingAgentClipboardCommandRunner
{
    public async Task<CodingAgentClipboardCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? stdin,
        int timeoutMs,
        int maxBufferBytes,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CodingAgentClipboardCommandResult(false, [], ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, timeout.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var output = new MemoryStream();
        var copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var readError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await copyOutput.ConfigureAwait(false);
            var stderr = await readError.ConfigureAwait(false);
            if (output.Length > maxBufferBytes)
            {
                return new CodingAgentClipboardCommandResult(false, [], "output exceeded max buffer");
            }

            return new CodingAgentClipboardCommandResult(process.ExitCode == 0, output.ToArray(), stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new CodingAgentClipboardCommandResult(false, [], "command timed out");
        }
    }
}
