// 作者：xxx
/*
 * 【CodingAgent】【第三方许可】Windows 参数转义规则改编自 cross-spawn
 * Copyright (c) 2018 Made With MOXY Lda <hello@moxy.studio>
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP Windows 启动】解析 PATH/PATHEXT、脚本解释器和命令包装器，保持独立参数的原始内容。</summary>
/// <remarks>转义规则参考 cross-spawn 的 MIT 实现：https://github.com/moxystudio/node-cross-spawn/blob/master/lib/util/escape.js</remarks>
internal static class CodingAgentMcpWindowsCommand
{
    /// <summary>【CodingAgent】【Windows 进程准备】原生程序直接启动，批处理和命令包装器使用受控转义的 cmd 参数。</summary>
    /// <param name="start">包含工作目录、参数及最终环境的启动信息。</param>
    internal static void Prepare(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) return;
        var command = start.FileName; var args = start.ArgumentList.ToList();
        var resolved = Resolve(command, start);
        // 1. 【CodingAgent】【脚本解释器】识别无原生可执行扩展名的 shebang，不修改宿主进程工作目录
        if (resolved is not null && !Native(resolved) && ReadShebang(resolved) is { Length: > 0 } interpreter)
        {
            command = interpreter[0]; args.Insert(0, resolved); args.InsertRange(0, interpreter.Skip(1));
            resolved = Resolve(command, start);
        }
        start.ArgumentList.Clear();
        if (resolved is not null && Native(resolved))
        {
            start.FileName = resolved;
            foreach (var argument in args) start.ArgumentList.Add(argument);
            return;
        }
        // 2. 【CodingAgent】【命令包装器】npm 的 .bin 包装器会再解释一次参数，需要双重转义元字符
        var doubleEscape = resolved is not null && Regex.IsMatch(resolved, @"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var executable = (resolved ?? command).Replace('/', '\\');
        if (executable.Contains('\r') || executable.Contains('\n') || args.Any(argument => argument.Contains('\r') || argument.Contains('\n')))
            throw new ArgumentException("Windows command wrappers cannot receive arguments containing line breaks.");
        var shellCommand = Escape(executable) + (args.Count == 0 ? "" : " " + string.Join(' ', args.Select(argument => Argument(argument, doubleEscape))));
        start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        start.Arguments = "/d /s /c \"" + shellCommand + "\"";
    }

    /// <summary>【CodingAgent】【Windows 文件定位】采用子进程环境搜索可执行扩展，支持相对路径、带引号 PATH 和无扩展脚本。</summary>
    /// <param name="command">命令。</param><param name="start">目标目录与环境。</param><returns>绝对路径，未找到返回空值。</returns>
    private static string? Resolve(string command, ProcessStartInfo start)
    {
        var cwd = Path.GetFullPath(string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory);
        var directories = command.IndexOfAny(['/', '\\', ':']) >= 0 ? [cwd] : new[] { cwd }.Concat(
            (EnvironmentValue(start, "PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim('"'))).ToArray();
        var extensions = (EnvironmentValue(start, "PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var fallback in new[] { false, true })
            foreach (var directory in directories)
            {
                string file;
                try { file = Path.GetFullPath(command, Path.GetFullPath(directory, cwd)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                var suffixes = fallback ? [""] : Path.HasExtension(command) ? new[] { "" }.Concat(extensions) : extensions;
                foreach (var suffix in suffixes) if (File.Exists(file + suffix)) return file + suffix;
            }
        return null;
    }

    /// <summary>【CodingAgent】【Windows 环境键】大小写不敏感地读取子进程环境，不从宿主补回显式删除的变量。</summary>
    /// <param name="start">目标环境。</param><param name="name">变量名。</param><returns>值或空值。</returns>
    private static string? EnvironmentValue(ProcessStartInfo start, string name) => start.Environment.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    /// <summary>【CodingAgent】【原生程序】判断可直接由 Windows 加载的扩展名。</summary><param name="path">路径。</param><returns>是否为 exe/com。</returns>
    private static bool Native(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".com", StringComparison.OrdinalIgnoreCase);

    /// <summary>【CodingAgent】【解释器行】最多读取 150 字节，支持常用 env/node 及解释器选项。</summary>
    /// <param name="path">脚本路径。</param><returns>解释器与选项，非 shebang 文件返回空值。</returns>
    private static string[]? ReadShebang(string path)
    {
        try
        {
            using var file = File.OpenRead(path); Span<byte> buffer = stackalloc byte[150]; var count = file.Read(buffer);
            var first = Encoding.UTF8.GetString(buffer[..count]).Split('\n')[0].TrimEnd('\r');
            if (!first.StartsWith("#!", StringComparison.Ordinal)) return null;
            var parts = first[2..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0) return null;
            if (Path.GetFileName(parts[0]) == "env") { parts.RemoveAt(0); if (parts.FirstOrDefault() == "-S") parts.RemoveAt(0); }
            if (parts.Count == 0) return null;
            parts[0] = Path.GetFileName(parts[0]); return parts.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>【CodingAgent】【cmd 元字符】逐字符添加转义，线性处理长反斜杠与引号输入。</summary><param name="value">文本。</param><returns>转义文本。</returns>
    private static string Escape(string value)
    {
        const string meta = "()[]%!^\"\u0060<>&|;, *?";
        var text = new StringBuilder(value.Length * 2);
        foreach (var character in value) { if (meta.Contains(character)) text.Append('^'); text.Append(character); }
        return text.ToString();
    }

    /// <summary>【CodingAgent】【cmd 参数】先按程序参数规则处理引号和末尾反斜杠，再按包装器层数转义 shell 元字符。</summary>
    /// <param name="value">原始参数。</param><param name="twice">是否经过 npm 包装器的第二次解释。</param><returns>可放入原样 cmd 命令行的参数。</returns>
    private static string Argument(string value, bool twice)
    {
        var quoted = new StringBuilder().Append('"'); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); slashes = 0; quoted.Append(character);
        }
        quoted.Append('\\', slashes * 2).Append('"');
        var escaped = Escape(quoted.ToString()); return twice ? Escape(escaped) : escaped;
    }
}
