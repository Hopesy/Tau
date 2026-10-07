// 作者：xxx
using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed partial class SystemCodingAgentClipboard
{
    private const int MaxOsc52EncodedLength = 100_000;
    internal TextWriter TerminalOutput { get; set; } = Console.Out;
    internal Func<string> KernelVersionReader { get; set; } = static () => File.ReadAllText("/proc/version");
    internal Func<string> TempTextPathFactory { get; set; } = static () => Path.Combine(Path.GetTempPath(), $"tau-wsl-clip-{Guid.NewGuid():N}.txt");

    /// <summary>【CodingAgent】【复制文本】优先更新本机剪贴板，再按远程或无桌面环境发送有大小上限的 OSC 52。</summary>
    /// <param name="text">完整文本。</param><param name="cancellationToken">取消信号。</param><returns>完成复制的任务。</returns>
    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(text);
        var copied = false;
        // 1. 【CodingAgent】【平台写入】Windows 用 ASCII 封装 UTF-8 避免代码页损坏，其他平台按环境选择原生工具
        if (_platform == CodingAgentClipboardPlatform.Windows)
        {
            copied = await TryWriteCommandAsync("powershell.exe", ["-NoProfile", "-STA", "-NonInteractive", "-Command",
                "Set-Clipboard -Value ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String([Console]::In.ReadToEnd())))"],
                Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes)), cancellationToken).ConfigureAwait(false);
            if (!copied) copied = await TryWriteCommandAsync("clip.exe", [], [0xff, 0xfe, .. Encoding.Unicode.GetBytes(text)], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var commands = new List<(string Name, string[] Arguments)>();
            if (_platform == CodingAgentClipboardPlatform.MacOS) commands.Add(("pbcopy", []));
            else
            {
                if (HasEnv("TERMUX_VERSION")) commands.Add(("termux-clipboard-set", []));
                if (HasEnv("WAYLAND_DISPLAY")) commands.Add(("wl-copy", []));
                if (HasEnv("DISPLAY")) { commands.Add(("xclip", ["-selection", "clipboard"])); commands.Add(("xsel", ["--clipboard", "--input"])); }
            }
            foreach (var (name, arguments) in commands)
                if (await TryWriteCommandAsync(name, arguments, bytes, cancellationToken).ConfigureAwait(false)) { copied = true; break; }
        }

        // 2. 【CodingAgent】【WSL 回退】Windows Terminal 优先走 OSC 52，其他情况用 UTF-8 临时文件跨代码页传递
        var emitted = false;
        if (!copied && _platform == CodingAgentClipboardPlatform.Linux && IsWsl())
        {
            if (HasEnv("WT_SESSION")) emitted = EmitOsc52(bytes, cancellationToken);
            copied = emitted || await TryCopyViaWindowsFileAsync(text, cancellationToken).ConfigureAwait(false);
        }
        var remote = HasEnv("SSH_CONNECTION") || HasEnv("SSH_CLIENT") || HasEnv("MOSH_CONNECTION");
        var headless = _platform == CodingAgentClipboardPlatform.Linux && !HasEnv("DISPLAY") && !HasEnv("WAYLAND_DISPLAY") && !HasEnv("TERMUX_VERSION");
        var oversized = false;
        if (!emitted && (remote || !copied && headless))
        {
            if (EmitOsc52(bytes, cancellationToken)) copied = true;
            else oversized = true;
        }
        if (copied) return;
        if (oversized) throw new InvalidOperationException("Clipboard unavailable: text exceeds the OSC 52 size limit");
        if (_platform == CodingAgentClipboardPlatform.Linux)
        {
            if (HasEnv("TERMUX_VERSION")) throw new InvalidOperationException("Clipboard unavailable: install the Termux:API app and `termux-api` package");
            if (HasEnv("WAYLAND_DISPLAY")) throw new InvalidOperationException("Clipboard unavailable: install `wl-clipboard` (`wl-copy`) or check Wayland access");
            if (HasEnv("DISPLAY")) throw new InvalidOperationException("Clipboard unavailable: install `xclip` or `xsel`, or check X11 access");
        }
        throw new InvalidOperationException("Clipboard unavailable");
    }

    /// <summary>【CodingAgent】【复制命令】把字节作为 stdin 传入有界执行器，不将文本放入命令参数。</summary>
    /// <param name="command">程序名称。</param><param name="arguments">参数。</param><param name="input">输入字节。</param>
    /// <param name="token">取消信号。</param><returns>是否复制成功。</returns>
    private async Task<bool> TryWriteCommandAsync(string command, IReadOnlyList<string> arguments, byte[] input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _runner.RunAsync(command, arguments, input, PowerShellTimeoutMs, MaxBufferBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result.Ok;
    }

    /// <summary>【CodingAgent】【终端复制】在 Base64 编码前检查大小，再向终端发送一次完整 OSC 52 序列。</summary>
    /// <param name="bytes">UTF-8 文本字节。</param><param name="token">取消信号。</param><returns>是否发送，超限时不输出。</returns>
    private bool EmitOsc52(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (((long)bytes.Length + 2) / 3 * 4 > MaxOsc52EncodedLength) return false;
        TerminalOutput.Write($"\u001b]52;c;{Convert.ToBase64String(bytes)}\u0007");
        return true;
    }

    /// <summary>【CodingAgent】【WSL Unicode 复制】通过仅当前用户可读的临时文件传递 UTF-8，成功、失败和取消均删除本次文件。</summary>
    /// <param name="text">完整文本。</param><param name="token">取消信号。</param><returns>Windows 剪贴板是否写入成功。</returns>
    private async Task<bool> TryCopyViaWindowsFileAsync(string text, CancellationToken token)
    {
        var path = TempTextPathFactory();
        var created = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(path, options))
            {
                created = true;
                await file.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
            }
            var converted = await _runner.RunAsync("wslpath", ["-w", path], null, ListTimeoutMs, MaxBufferBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var windowsPath = converted.Ok ? Encoding.UTF8.GetString(converted.Stdout).Trim() : string.Empty;
            if (windowsPath.Length == 0) return false;
            var script = "Set-Clipboard -Value ([System.IO.File]::ReadAllText('" + windowsPath.Replace("'", "''", StringComparison.Ordinal) + "', [System.Text.Encoding]::UTF8))";
            var result = await _runner.RunAsync("powershell.exe", ["-NoProfile", "-Command", script], null, PowerShellTimeoutMs, MaxBufferBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { token.ThrowIfCancellationRequested(); return false; }
        finally
        {
            if (created)
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
