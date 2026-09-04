namespace Tau.Tui.Rendering;

public sealed class TuiAnsiRenderSurface : ITuiRenderSurface
{
    private const string BeginSynchronizedOutput = "\u001b[?2026h";
    private const string EndSynchronizedOutput = "\u001b[?2026l";
    private const string DisableAutoWrap = "\u001b[?7l";
    private const string EnableAutoWrap = "\u001b[?7h";
    private const string ClearScreenAndHome = "\u001b[2J\u001b[H";
    private const string ClearLine = "\u001b[2K";

    private readonly TextWriter _writer;
    private readonly Func<int> _widthProvider;
    private readonly Func<int> _heightProvider;
    private readonly bool _synchronizedOutput;
    private readonly bool _avoidLastColumnWrap;

    /// <summary>
    /// 创建 ANSI 渲染表面。
    /// </summary>
    /// <param name="writer">写入 ANSI 序列的目标。</param>
    /// <param name="widthProvider">获取终端列数的函数。</param>
    /// <param name="heightProvider">获取终端行数的函数。</param>
    /// <param name="synchronizedOutput">是否使用同步输出扩展。</param>
    /// <param name="avoidLastColumnWrap">是否临时关闭末列自动换行。</param>
    public TuiAnsiRenderSurface(
        TextWriter writer,
        Func<int> widthProvider,
        Func<int> heightProvider,
        bool synchronizedOutput = true,
        bool avoidLastColumnWrap = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _widthProvider = widthProvider ?? throw new ArgumentNullException(nameof(widthProvider));
        _heightProvider = heightProvider ?? throw new ArgumentNullException(nameof(heightProvider));
        _synchronizedOutput = synchronizedOutput;
        _avoidLastColumnWrap = avoidLastColumnWrap;
    }

    public int Width => Math.Max(1, _widthProvider());
    public int Height => Math.Max(1, _heightProvider());

    /// <summary>
    /// 创建绑定当前 Windows/VT 控制台的 ANSI 渲染表面。
    /// </summary>
    /// <param name="synchronizedOutput">是否使用同步输出扩展。</param>
    /// <returns>绑定控制台尺寸和输出流的渲染表面。</returns>
    public static TuiAnsiRenderSurface ForConsole(bool synchronizedOutput = true) =>
        new(Console.Out, SafeGetWindowWidth, SafeGetWindowHeight, synchronizedOutput, avoidLastColumnWrap: true);

    public void Apply(TuiRenderDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (!diff.RequiresFullRedraw && diff.Operations.Count == 0)
        {
            return;
        }

        var buffer = new StringWriter();
        if (_synchronizedOutput)
        {
            buffer.Write(BeginSynchronizedOutput);
        }

        if (_avoidLastColumnWrap)
        {
            buffer.Write(DisableAutoWrap);
        }

        if (diff.RequiresFullRedraw)
        {
            WriteFullRedraw(buffer, diff.Operations);
        }
        else
        {
            WriteLineOperations(buffer, diff.Operations);
        }

        if (_avoidLastColumnWrap)
        {
            buffer.Write(EnableAutoWrap);
        }

        if (_synchronizedOutput)
        {
            buffer.Write(EndSynchronizedOutput);
        }

        _writer.Write(buffer.ToString());
    }

    private static void WriteFullRedraw(TextWriter writer, IReadOnlyList<TuiRenderOperation> operations)
    {
        writer.Write(ClearScreenAndHome);
        var ordered = operations
            .Where(static operation => operation.Kind == TuiRenderOperationKind.ReplaceLine)
            .OrderBy(static operation => operation.Row)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
            {
                writer.Write("\r\n");
            }

            writer.Write(ordered[i].Text);
        }
    }

    private static void WriteLineOperations(TextWriter writer, IReadOnlyList<TuiRenderOperation> operations)
    {
        foreach (var operation in operations.OrderBy(static operation => operation.Row))
        {
            writer.Write(CursorPosition(operation.Row, column: 0));
            writer.Write(ClearLine);
            if (operation.Kind == TuiRenderOperationKind.ReplaceLine)
            {
                writer.Write(operation.Text);
            }
        }
    }

    private static string CursorPosition(int row, int column) =>
        $"\u001b[{Math.Max(0, row) + 1};{Math.Max(0, column) + 1}H";

    private static int SafeGetWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }

    private static int SafeGetWindowHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch
        {
            return 24;
        }
    }
}
