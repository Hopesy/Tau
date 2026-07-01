using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【外部编辑器】表示一次外部编辑器调用的结果。
/// </summary>
/// <param name="EditorConfigured">是否找到了可用的外部编辑器配置。</param>
/// <param name="Edited">外部编辑器是否成功返回了编辑后的文本。</param>
/// <param name="Text">编辑后的文本；没有成功编辑时为 <see langword="null"/>。</param>
/// <param name="ErrorMessage">外部编辑器启动、退出或读取失败时的错误消息。</param>
public sealed record CodingAgentExternalEditorResult(
    bool EditorConfigured,
    bool Edited,
    string? Text,
    string? ErrorMessage = null);

/// <summary>
/// 【CodingAgent】【外部编辑器】定义把当前输入交给外部编辑器修改的抽象。
/// </summary>
public interface ICodingAgentExternalEditor
{
    /// <summary>
    /// 打开外部编辑器编辑当前输入文本。
    /// </summary>
    /// <param name="currentText">进入外部编辑器前的当前输入文本。</param>
    /// <param name="cancellationToken">取消等待外部编辑器退出的令牌。</param>
    /// <returns>外部编辑器调用结果，包含是否配置、是否编辑成功、编辑后的文本和错误消息。</returns>
    Task<CodingAgentExternalEditorResult> EditAsync(string currentText, CancellationToken cancellationToken = default);
}

/// <summary>
/// 【CodingAgent】【外部编辑器】通过 <c>VISUAL</c> 或 <c>EDITOR</c> 环境变量启动系统外部编辑器。
/// </summary>
public sealed class SystemCodingAgentExternalEditor : ICodingAgentExternalEditor
{
    private readonly Func<string?> _visualProvider;
    private readonly Func<string?> _editorProvider;
    private readonly Func<string> _tempFileFactory;

    /// <summary>
    /// 创建使用真实环境变量和系统临时文件路径的外部编辑器适配器。
    /// </summary>
    public SystemCodingAgentExternalEditor()
        : this(
            () => Environment.GetEnvironmentVariable("VISUAL"),
            () => Environment.GetEnvironmentVariable("EDITOR"),
            static () => Path.Combine(Path.GetTempPath(), $"tau-editor-{Guid.NewGuid():N}.md"))
    {
    }

    /// <summary>
    /// 创建可注入环境变量读取器和临时文件工厂的外部编辑器适配器，供测试验证优先级和文件交互。
    /// </summary>
    /// <param name="visualProvider">读取 <c>VISUAL</c> 编辑器命令的委托。</param>
    /// <param name="editorProvider">读取 <c>EDITOR</c> 编辑器命令的委托。</param>
    /// <param name="tempFileFactory">生成临时编辑文件路径的委托。</param>
    internal SystemCodingAgentExternalEditor(
        Func<string?> visualProvider,
        Func<string?> editorProvider,
        Func<string> tempFileFactory)
    {
        _visualProvider = visualProvider;
        _editorProvider = editorProvider;
        _tempFileFactory = tempFileFactory;
    }

    /// <summary>
    /// 把当前输入写入临时文件，启动外部编辑器，读取编辑后的文本并清理临时文件。
    /// </summary>
    /// <param name="currentText">进入外部编辑器前的当前输入文本。</param>
    /// <param name="cancellationToken">取消写入、等待或读取操作的令牌。</param>
    /// <returns>外部编辑器调用结果；未配置编辑器、编辑器失败或成功编辑都会通过结果对象表达。</returns>
    public async Task<CodingAgentExternalEditorResult> EditAsync(
        string currentText,
        CancellationToken cancellationToken = default)
    {
        // 1. 按上游优先级选择 VISUAL，再回退到 EDITOR
        var editorCommand = FirstNonEmpty(_visualProvider(), _editorProvider());
        if (editorCommand is null)
        {
            return new CodingAgentExternalEditorResult(
                EditorConfigured: false,
                Edited: false,
                Text: null);
        }

        var tempFile = _tempFileFactory();
        try
        {
            // 2. 把当前输入写入临时文件，并把临时文件路径交给外部编辑器
            await File.WriteAllTextAsync(tempFile, currentText, cancellationToken).ConfigureAwait(false);
            var exitCode = await RunEditorAsync(editorCommand, tempFile, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return new CodingAgentExternalEditorResult(
                    EditorConfigured: true,
                    Edited: false,
                    Text: null,
                    ErrorMessage: $"external editor exited with code {exitCode}");
            }

            // 3. 编辑器正常退出后读取文件内容，并裁剪一个尾部换行以匹配交互输入语义
            var edited = await File.ReadAllTextAsync(tempFile, cancellationToken).ConfigureAwait(false);
            return new CodingAgentExternalEditorResult(
                EditorConfigured: true,
                Edited: true,
                Text: TrimSingleTrailingNewline(edited));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CodingAgentExternalEditorResult(
                EditorConfigured: true,
                Edited: false,
                Text: null,
                ErrorMessage: ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // 4. 临时文件清理是尽力而为，不能覆盖编辑器本身的返回结果
            }
        }
    }

    /// <summary>
    /// 启动外部编辑器进程并等待其退出。
    /// </summary>
    /// <param name="editorCommand">外部编辑器命令。</param>
    /// <param name="tempFile">传给外部编辑器的临时文件路径。</param>
    /// <param name="cancellationToken">取消等待进程退出的令牌。</param>
    /// <returns>外部编辑器进程退出码；进程无法启动时返回 -1。</returns>
    private static async Task<int> RunEditorAsync(
        string editorCommand,
        string tempFile,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStartInfo(editorCommand, tempFile));
        if (process is null)
        {
            return -1;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// 根据当前操作系统构造外部编辑器启动参数。
    /// </summary>
    /// <param name="editorCommand">外部编辑器命令。</param>
    /// <param name="tempFile">传给外部编辑器的临时文件路径。</param>
    /// <returns>可交给 <see cref="Process.Start(ProcessStartInfo)"/> 的启动信息。</returns>
    private static ProcessStartInfo CreateStartInfo(string editorCommand, string tempFile)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c {editorCommand} {QuoteForCommandLine(tempFile)}",
                UseShellExecute = false
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-lc {QuoteForCommandLine($"{editorCommand} {QuoteForCommandLine(tempFile)}")}",
            UseShellExecute = false
        };
    }

    /// <summary>
    /// 在两个候选字符串中返回第一个非空白值。
    /// </summary>
    /// <param name="first">第一候选值。</param>
    /// <param name="second">第二候选值。</param>
    /// <returns>第一个非空白候选值；两个候选都为空白时返回 <see langword="null"/>。</returns>
    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? null : second;
    }

    /// <summary>
    /// 删除文本末尾最多一个换行符，保留其他内容不变。
    /// </summary>
    /// <param name="text">需要裁剪的编辑器输出文本。</param>
    /// <returns>裁剪单个尾部换行后的文本。</returns>
    private static string TrimSingleTrailingNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    /// <summary>
    /// 为 shell 命令行参数添加双引号并转义内部双引号。
    /// </summary>
    /// <param name="value">需要转义的命令行参数值。</param>
    /// <returns>可嵌入命令行的加引号参数。</returns>
    private static string QuoteForCommandLine(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
