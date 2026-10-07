// 作者：xxx
using System.Diagnostics;
using System.Globalization;
using Tau.Ai.Auth.OAuth;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【控制台认证】在普通控制台或管道中展示登录交互，所有提示均支持取消。</summary>
internal sealed class ConsoleOAuthLoginCallbacks : IOAuthLoginCallbacks, IOAuthManualCodeInputController
{
    private readonly TextWriter _output;
    private readonly Func<bool, CancellationToken, Task<string?>> _readLine;
    private readonly Action<string> _openBrowser;
    private CancellationTokenSource? _manualCancellation;

    /// <summary>【CodingAgent】【控制台认证】使用进程标准输入输出创建交互。</summary>
    public ConsoleOAuthLoginCallbacks() : this(Console.In, Console.Out) { }

    /// <summary>【CodingAgent】【控制台认证】注入输入输出，允许测试捕获浏览器与秘密输入而不修改全局 Console。</summary>
    /// <param name="input">文本来源。</param><param name="output">输出。</param><param name="openBrowser">浏览器打开动作。</param>
    /// <param name="readLine">可选终端读取，布尔值表示禁止回显。</param>
    internal ConsoleOAuthLoginCallbacks(TextReader input, TextWriter output, Action<string>? openBrowser = null,
        Func<bool, CancellationToken, Task<string?>>? readLine = null)
    {
        _output = output;
        _readLine = readLine ?? ((secret, token) => ReferenceEquals(input, Console.In)
            ? TuiConsoleInput.ReadLineAsync(secret, token) : TuiCancelableTextReader.ReadLineAsync(input, token));
        _openBrowser = openBrowser ?? TryOpenBrowser;
    }

    /// <summary>【CodingAgent】【浏览器授权】显示地址与说明并尝试打开浏览器。</summary>
    /// <param name="url">授权地址。</param><param name="instructions">可选说明。</param>
    public void OnAuth(string url, string? instructions = null)
    {
        _output.WriteLine();
        if (!string.IsNullOrWhiteSpace(instructions)) _output.WriteLine(instructions);
        _output.WriteLine($"Open this URL to authenticate:\n  {url}");
        _openBrowser(url); _output.WriteLine();
    }

    /// <summary>【CodingAgent】【文本提示】兼容无取消信号的旧回调。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">允许空值。</param><returns>输入。</returns>
    public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) =>
        OnPromptAsync(message, placeholder, allowEmpty, default);

    /// <summary>【CodingAgent】【文本提示】异步等待普通输入，取消和 EOF 不会转换成成功授权。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">允许空值。</param><param name="token">本次提示取消。</param><returns>输入。</returns>
    public Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token) =>
        ReadPromptAsync(message, placeholder, allowEmpty, secret: false, token);

    /// <summary>【CodingAgent】【秘密提示】不回显秘密、不保存普通编辑历史。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位。</param><param name="token">取消信号。</param><returns>秘密输入。</returns>
    public Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token) =>
        ReadPromptAsync(message, placeholder, allowEmpty: true, secret: true, token);

    /// <summary>【CodingAgent】【手工授权】向可独立取消的输入通道传递授权码提示。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位。</param><param name="token">单次提示取消。</param><returns>授权输入。</returns>
    public Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token) =>
        ReadPromptAsync(message, placeholder, allowEmpty: true, secret: false, token);

    /// <summary>【CodingAgent】【选择登录方式】兼容无取消信号的选择接口。</summary>
    /// <param name="message">提示。</param><param name="options">候选项。</param><returns>候选 ID。</returns>
    public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options) => OnSelectAsync(message, options, default);

    /// <summary>【CodingAgent】【选择登录方式】允许候选 ID、序号或回车默认首项；无效值重新提示。</summary>
    /// <param name="message">提示。</param><param name="options">候选项及说明。</param><param name="token">取消信号。</param><returns>候选 ID，空列表为空。</returns>
    public async Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.Count == 0) return null;
        _output.WriteLine(message);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            _output.WriteLine($"  {index + 1}. {option.Label} ({option.Id})" + (option.Description is { Length: > 0 } detail ? " — " + detail : ""));
        }
        while (true)
        {
            var input = (await ReadPromptAsync($"Enter number or ID (default 1):", null, true, false, token).ConfigureAwait(false)).Trim();
            if (input.Length == 0) return options[0].Id;
            if (options.FirstOrDefault(option => option.Id == input) is { } selected) return selected.Id;
            if (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && number <= options.Count) return options[number - 1].Id;
            _output.WriteLine("Invalid selection. Enter one of the listed numbers or IDs.");
        }
    }

    /// <summary>【CodingAgent】【认证进度】输出非秘密进度消息。</summary><param name="message">消息。</param>
    public void OnProgress(string message) => _output.WriteLine(message);

    /// <summary>【CodingAgent】【设备授权】显示验证地址与设备代码，设备模式由用户在所选浏览器打开链接。</summary>
    /// <param name="notification">类型化设备码通知。</param>
    public void OnDeviceCode(OAuthDeviceCodeNotification notification)
    {
        _output.WriteLine(notification.VerificationUri);
        _output.WriteLine("Enter code: " + notification.UserCode);
    }

    /// <summary>【CodingAgent】【认证读取】先检查取消再显示提示，空值校验与秘密输入共用有界等待。</summary>
    /// <param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">允许空值。</param>
    /// <param name="secret">禁止回显。</param><param name="token">取消。</param><returns>文本。</returns>
    private async Task<string> ReadPromptAsync(string message, string? placeholder, bool allowEmpty, bool secret, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _output.Write(message + (string.IsNullOrEmpty(placeholder) ? "" : " [" + placeholder + "]") + " ");
        var input = await _readLine(secret, token).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (input is null || !allowEmpty && string.IsNullOrWhiteSpace(input)) throw new OperationCanceledException("Login cancelled (empty input).");
        return input;
    }

    /// <summary>【CodingAgent】【旧手工授权】为旧提供方保留可由浏览器胜出后主动取消的输入。</summary><returns>输入任务。</returns>
    public Task<string>? OnManualCodeInputAsync()
    {
        CancelManualCodeInput();
        var source = new CancellationTokenSource(); _manualCancellation = source;
        return ReadLegacyManualAsync(source);
    }

    /// <summary>【CodingAgent】【旧手工授权】浏览器完成后结束手工输入，不残留争抢后续输入的独立读取。</summary>
    public void CancelManualCodeInput()
    {
        try { _manualCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>【CodingAgent】【旧手工授权】输入结束时只释放属于本次调用的取消源。</summary>
    /// <param name="source">本次取消源。</param><returns>授权输入。</returns>
    private async Task<string> ReadLegacyManualAsync(CancellationTokenSource source)
    {
        try { return await ReadPromptAsync("Paste the authorization code or full redirect URL:", null, false, false, source.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { return ""; }
        finally
        {
            if (ReferenceEquals(_manualCancellation, source)) _manualCancellation = null;
            source.Dispose();
        }
    }

    /// <summary>【CodingAgent】【打开浏览器】调用系统 URL 处理器，失败时用户仍可复制已经显示的地址。</summary><param name="url">授权 URL。</param>
    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
            {
                var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
                info.ArgumentList.Add(url); Process.Start(info);
            }
        }
        catch { }
    }
}
