// 作者：xxx
using System.Text;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class SystemCodingAgentClipboard
{
    /// <summary>【CodingAgent】【剪贴板文本】按平台读取 UTF-8 文本，保留空白；命令不可用时尝试后续工具。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>原始文本，无内容时为空引用。</returns>
    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform == CodingAgentClipboardPlatform.Windows)
            return await ReadWindowsTextAsync(cancellationToken).ConfigureAwait(false);
        if (_platform == CodingAgentClipboardPlatform.MacOS)
        {
            var text = await ReadTextCommandAsync("pbpaste", [], cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        // 1. 【CodingAgent】【Linux 文本】保持 Termux、Wayland、X11 的环境优先级，成功但为空时停止回退
        var commands = new List<(string Name, string[] Arguments)>();
        if (HasEnv("TERMUX_VERSION")) commands.Add(("termux-clipboard-get", []));
        if (HasEnv("WAYLAND_DISPLAY")) commands.Add(("wl-paste", ["--no-newline", "--type", "text"]));
        if (HasEnv("DISPLAY"))
        {
            commands.Add(("xclip", ["-selection", "clipboard", "-out"]));
            commands.Add(("xsel", ["--clipboard", "--output"]));
        }
        foreach (var (name, arguments) in commands)
        {
            var text = await ReadTextCommandAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            if (text is not null) return text.Length == 0 ? null : text;
        }
        return IsWsl() ? await ReadWindowsTextAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>【CodingAgent】【剪贴板文件】读取 Windows 文件列表、macOS 文件 URL 或 Linux URI 列表。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>本地路径列表，无文件时为空引用。</returns>
    public async Task<IReadOnlyList<string>?> ReadFilePathsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform == CodingAgentClipboardPlatform.Windows || _platform == CodingAgentClipboardPlatform.Linux && IsWsl())
        {
            var json = await ReadTextCommandAsync("powershell.exe", ["-NoProfile", "-STA", "-NonInteractive", "-Command",
                "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); Add-Type -AssemblyName System.Windows.Forms; $files=@([System.Windows.Forms.Clipboard]::GetFileDropList()); [Console]::Write((ConvertTo-Json -InputObject $files -Compress))"], cancellationToken).ConfigureAwait(false);
            var files = ParseFileArray(json);
            if (_platform != CodingAgentClipboardPlatform.Linux || files is null) return files;
            // 2. 【CodingAgent】【WSL 路径】逐条通过参数数组转换，路径不会作为脚本执行
            var converted = new List<string>();
            foreach (var file in files)
            {
                var path = await ReadTextCommandAsync("wslpath", ["-u", file], cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path)) return null;
                converted.Add(path.TrimEnd('\r', '\n'));
            }
            return converted;
        }
        if (_platform == CodingAgentClipboardPlatform.MacOS)
        {
            var json = await ReadTextCommandAsync("osascript", ["-l", "JavaScript", "-e",
                "ObjC.import('AppKit'); ObjC.import('Foundation'); const items=$.NSPasteboard.generalPasteboard.readObjectsForClassesOptions([$.NSURL],null); const paths=[]; if(items) for(let i=0;i<items.count;i++){ const item=items.objectAtIndex(i); if(item.isFileURL) paths.push(ObjC.unwrap(item.path)); } JSON.stringify(paths);"], cancellationToken).ConfigureAwait(false);
            return ParseFileArray(json);
        }
        if (HasEnv("TERMUX_VERSION")) return null;
        if (HasEnv("WAYLAND_DISPLAY"))
        {
            var text = await ReadTextCommandAsync("wl-paste", ["--no-newline", "--type", "text/uri-list"], cancellationToken).ConfigureAwait(false);
            if (ParseFileUris(text) is { } files) return files;
        }
        if (HasEnv("DISPLAY"))
        {
            var text = await ReadTextCommandAsync("xclip", ["-selection", "clipboard", "-t", "text/uri-list", "-o"], cancellationToken).ConfigureAwait(false);
            return ParseFileUris(text);
        }
        return null;
    }

    /// <summary>【CodingAgent】【Windows 文本】使用 UTF-8 输出避免控制台代码页损坏中文，不额外添加换行。</summary>
    /// <param name="token">取消信号。</param><returns>文本或空引用。</returns>
    private async Task<string?> ReadWindowsTextAsync(CancellationToken token)
    {
        var text = await ReadTextCommandAsync("powershell.exe", ["-NoProfile", "-STA", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $text=Get-Clipboard -Raw; if($null -ne $text){[Console]::Write($text)}"], token).ConfigureAwait(false);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>【CodingAgent】【有界文本命令】调用可测试的命令执行器，取消不得伪装为空剪贴板。</summary>
    /// <param name="command">程序名称。</param><param name="arguments">独立参数。</param><param name="token">取消信号。</param>
    /// <returns>成功输出，包括空字符串；工具失败时为空引用。</returns>
    private async Task<string?> ReadTextCommandAsync(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _runner.RunAsync(command, arguments, null, PowerShellTimeoutMs, MaxBufferBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result.Ok ? Encoding.UTF8.GetString(result.Stdout) : null;
    }

    /// <summary>【CodingAgent】【文件列表解析】只接受完整字符串数组，空列表或无效输出不视为复制文件。</summary>
    /// <param name="json">平台输出。</param><returns>原始文件路径列表。</returns>
    private static IReadOnlyList<string>? ParseFileArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0 ||
                document.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) return null;
            return document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>【CodingAgent】【文件 URI】忽略注释与非本机 URL，解码百分号转义并保持文件顺序。</summary>
    /// <param name="text">URI 列表。</param><returns>本地路径或空引用。</returns>
    private static IReadOnlyList<string>? ParseFileUris(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var files = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (!line.StartsWith('#') && Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile &&
                (uri.Host.Length == 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                files.Add(Uri.UnescapeDataString(uri.AbsolutePath));
        return files.Count == 0 ? null : files;
    }
}
