// 作者：xxx
using System.Globalization;
using System.Text;
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

/// <summary>【TUI】【控制台输入】普通输入和秘密输入共用可取消的终端或管道入口。</summary>
public static class TuiConsoleInput
{
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    /// <summary>【TUI】【控制台输入】终端使用可取消按键，重定向输入复用未结束的单次读取。</summary>
    /// <param name="secret">是否禁止回显和编辑历史。</param><param name="token">提示取消。</param><returns>提交文本，EOF 为空。</returns>
    public static async Task<string?> ReadLineAsync(bool secret = false, CancellationToken token = default)
    {
        if (Console.IsInputRedirected) return await TuiCancelableTextReader.ReadLineAsync(Console.In, token).ConfigureAwait(false);
        await InputGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var reader = new SystemConsoleKeyReader();
            if (secret)
            {
                var text = await ReadSecretAsync(reader, token).ConfigureAwait(false);
                Console.WriteLine(); return text;
            }
            var editor = new InteractiveInputEditor(reader, new SystemConsoleInteractiveRenderer(continueCurrentLine: true));
            var result = await editor.ReadLineAsync("", cancellationToken: token).ConfigureAwait(false);
            if (result.Kind != InputResultKind.Submitted) throw new OperationCanceledException("Input cancelled", token);
            return result.Text;
        }
        finally { InputGate.Release(); }
    }

    /// <summary>【TUI】【秘密输入】不使用编辑历史或 renderer，退格按字素删除，Escape 和 Ctrl+C 取消。</summary>
    /// <param name="reader">可取消按键来源。</param><param name="token">提示取消。</param><returns>未回显的输入。</returns>
    internal static async Task<string> ReadSecretAsync(IConsoleKeyReader reader, CancellationToken token)
    {
        var value = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var key = await reader.ReadKeyAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) throw new OperationCanceledException("Input cancelled", token);
            if (key.Key == ConsoleKey.Enter) return value.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length = StringInfo.ParseCombiningCharacters(value.ToString())[^1];
            }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
