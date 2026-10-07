using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed partial class SystemCodingAgentClipboard : ICodingAgentClipboard
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

    /// <summary>【CodingAgent】【图片读取】区分后端不可用与空剪贴板，仅在允许的分支回退，最后转换不支持的图片格式。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>图片或空引用。</returns>
    public async Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasEnv("TERMUX_VERSION"))
        {
            return null;
        }

        CodingAgentClipboardImage? image = null;
        if (_platform == CodingAgentClipboardPlatform.Linux)
        {
            var wsl = IsWsl();
            var result = new ClipboardImageReadResult(false, null);
            if (IsWaylandSession(_environment) || wsl)
                result = await ReadImageViaWlPasteAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Available) result = await ReadImageViaXclipAsync(cancellationToken).ConfigureAwait(false);
            image = result.Image;
            if (image is null && wsl) image = await ReadImageViaPowerShellAsync(wsl: true, cancellationToken).ConfigureAwait(false);
            if (image is null && !result.Available) image = await ReadNativeImageAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_platform == CodingAgentClipboardPlatform.Windows)
        {
            image = await ReadImageViaPowerShellAsync(wsl: false, cancellationToken).ConfigureAwait(false);
        }
        else image = await ReadNativeImageAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (image is null)
        {
            return null;
        }

        if (IsSupportedImageMimeType(image.MimeType))
        {
            return new CodingAgentClipboardImage(image.Bytes, BaseMimeType(image.MimeType));
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

    /// <summary>【CodingAgent】【Wayland 图片】工具失败才允许回退，成功列出但没有图片或数据为空表示空剪贴板。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>可用性及图片。</returns>
    private async Task<ClipboardImageReadResult> ReadImageViaWlPasteAsync(CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(
            "wl-paste",
            ["--list-types"],
            stdin: null,
            ListTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!list.Ok)
        {
            return new(false, null);
        }

        var selectedType = SelectPreferredImageMimeType(SplitLines(list.Stdout));
        if (selectedType is null)
        {
            return new(true, null);
        }

        var data = await _runner.RunAsync(
            "wl-paste",
            ["--type", selectedType, "--no-newline"],
            stdin: null,
            ReadTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(data.Ok, data.Ok && data.Stdout.Length > 0 ? new(data.Stdout, BaseMimeType(selectedType)) : null);
    }

    /// <summary>【CodingAgent】【X11 图片】只读取 TARGETS 声明的首选图片，不探测未声明格式或旧剪贴板内容。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>可用性及图片。</returns>
    private async Task<ClipboardImageReadResult> ReadImageViaXclipAsync(CancellationToken cancellationToken)
    {
        var targets = await _runner.RunAsync(
            "xclip",
            ["-selection", "clipboard", "-t", "TARGETS", "-o"],
            stdin: null,
            ListTimeoutMs,
            MaxBufferBytes,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!targets.Ok) return new(false, null);
        var preferred = SelectPreferredImageMimeType(SplitLines(targets.Stdout));
        if (preferred is null) return new(true, null);
        var data = await _runner.RunAsync("xclip", ["-selection", "clipboard", "-t", preferred, "-o"], null,
            ReadTimeoutMs, MaxBufferBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(data.Ok, data.Ok && data.Stdout.Length > 0 ? new(data.Stdout, BaseMimeType(preferred)) : null);
    }

    /// <summary>【CodingAgent】【后端结果】显式保留不可用与内容为空的区别。</summary>
    private sealed record ClipboardImageReadResult(bool Available, CodingAgentClipboardImage? Image);

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

    /// <summary>【CodingAgent】【WSL 识别】优先读取环境标识，缺失时检查内核版本。</summary><returns>是否运行在 WSL。</returns>
    private bool IsWsl()
    {
        if (HasEnv("WSL_DISTRO_NAME") || HasEnv("WSLENV")) return true;
        try
        {
            var version = KernelVersionReader();
            return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase) || version.Contains("wsl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

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
