using System.Text.RegularExpressions;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

/// <summary>
/// 定义 Bash 执行标题和状态行的显示格式化函数。
/// </summary>
/// <param name="CommandFormatter">命令标题的格式化函数。</param>
/// <param name="StatusFormatter">命令状态行的格式化函数。</param>
/// <param name="PanelRenderer">可选的 Bash Panel 渲染函数。</param>
public sealed record TuiBashExecutionTheme(
    Func<string, BashExecutionStatus, string>? CommandFormatter = null,
    Func<string, BashExecutionStatus, string>? StatusFormatter = null,
    Func<string, string, int, IReadOnlyList<string>>? PanelRenderer = null)
{
    /// <summary>
    /// 使用状态符号和 ANSI 颜色显示命令执行生命周期。
    /// </summary>
    public static TuiBashExecutionTheme Agent { get; } = new(
        CommandFormatter: static (value, status) =>
            $"\u001b[1;38;5;214m{status switch
            {
                BashExecutionStatus.Running => "⏳",
                BashExecutionStatus.Complete => "✓",
                BashExecutionStatus.Cancelled => "!",
            BashExecutionStatus.Error => "✗",
                _ => "•",
            }}\u001b[22;39m {value}",
        StatusFormatter: static (value, status) => status switch
        {
            BashExecutionStatus.Running => $"\u001b[38;5;245m{value}\u001b[39m",
            BashExecutionStatus.Complete => $"\u001b[32m{value}\u001b[39m",
            BashExecutionStatus.Cancelled => $"\u001b[33m{value}\u001b[39m",
            BashExecutionStatus.Error => $"\u001b[31m{value}\u001b[39m",
            _ => value,
        },
        PanelRenderer: static (title, content, width) =>
            TuiSpectreRenderer.Default.RenderPanel(title, content, width));
}

public sealed partial class TuiBashExecution : ITuiComponent
{
    public const int DefaultPreviewLines = 20;
    private readonly string _command;
    private readonly bool _excludeFromContext;
    private readonly int _previewLines;
    private readonly TuiBashExecutionTheme _theme;
    private readonly List<string> _outputLines = [];
    private bool _expanded;
    private BashExecutionStatus _status = BashExecutionStatus.Running;
    private int? _exitCode;
    private bool _truncated;
    private string? _fullOutputPath;
    private string? _expandKeyHint;

    /// <summary>
    /// 创建 Bash 执行显示组件。
    /// </summary>
    /// <param name="command">需要显示和执行的命令文本。</param>
    /// <param name="excludeFromContext">是否将命令标记为不纳入上下文。</param>
    /// <param name="previewLines">折叠状态下保留的预览行数。</param>
    /// <param name="theme">命令标题和状态行的显示主题。</param>
    public TuiBashExecution(
        string command,
        bool excludeFromContext = false,
        int previewLines = DefaultPreviewLines,
        TuiBashExecutionTheme? theme = null)
    {
        _command = command ?? string.Empty;
        _excludeFromContext = excludeFromContext;
        _previewLines = Math.Max(1, previewLines);
        _theme = theme ?? new TuiBashExecutionTheme();
    }

    public string Command => _command;
    public bool Expanded => _expanded;
    public BashExecutionStatus Status => _status;
    public IReadOnlyList<string> OutputLines => _outputLines;

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
    }

    /// <summary>
    /// 设置展开/收起提示中显示的快捷键文本，例如 <c>Ctrl+O</c>；为空时回退到默认提示文案。
    /// </summary>
    public void SetExpandKeyHint(string? keyText)
    {
        _expandKeyHint = string.IsNullOrWhiteSpace(keyText) ? null : keyText.Trim();
    }

    public void AppendOutput(string? chunk)
    {
        var normalized = NormalizeOutput(chunk);
        if (normalized.Length == 0)
        {
            return;
        }

        var lines = normalized.Split('\n');
        if (_outputLines.Count > 0 && lines.Length > 0)
        {
            _outputLines[^1] += lines[0];
            _outputLines.AddRange(lines.Skip(1));
            return;
        }

        _outputLines.AddRange(lines);
    }

    /// <summary>【TUI】【命令快照】替换命令输出快照，避免把重复快照作为增量追加。</summary>
    /// <param name="text">完整的当前可见输出。</param>
    public void SetOutput(string? text)
    {
        _outputLines.Clear();
        AppendOutput(text);
    }

    public void SetComplete(
        int? exitCode,
        bool cancelled = false,
        bool truncated = false,
        string? fullOutputPath = null)
    {
        _exitCode = exitCode;
        _status = cancelled
            ? BashExecutionStatus.Cancelled
            : exitCode is not null and not 0
                ? BashExecutionStatus.Error
                : BashExecutionStatus.Complete;
        _truncated = truncated;
        _fullOutputPath = string.IsNullOrWhiteSpace(fullOutputPath) ? null : fullOutputPath.Trim();
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        if (_theme.PanelRenderer is { } panelRenderer)
        {
            return panelRenderer(FormatPanelTitle(), FormatPanelBody(width), width);
        }

        var lines = new List<string>
        {
            FormatLine(FormatCommandHeader(), width)
        };

        var availableOutput = GetAvailableOutput();
        if (availableOutput.Length > 0)
        {
            if (_expanded)
            {
                foreach (var outputLine in TuiText.WrapTextWithAnsi(availableOutput, Math.Max(1, width - 2)))
                {
                    lines.Add(FormatLine(" " + outputLine, width));
                }
            }
            else
            {
                var preview = TuiText.TruncateToVisualLines(
                    availableOutput,
                    _previewLines,
                    width,
                    paddingX: 1);
                lines.AddRange(preview.VisualLines);

                if (preview.SkippedCount > 0)
                {
                    lines.Add(FormatLine($"... {preview.SkippedCount} more visual lines ({ExpandHintText()})", width));
                }
            }
        }

        foreach (var status in StatusLines())
        {
            var formattedStatus = _theme.StatusFormatter?.Invoke(status, _status) ?? status;
            lines.Add(FormatLine(formattedStatus, width));
        }

        return lines;
    }

    /// <summary>
    /// 获取 Panel 标题中的 Bash 生命周期标识和命令文本。
    /// </summary>
    /// <returns>不包含 ANSI 控制序列的 Panel 标题。</returns>
    private string FormatPanelTitle()
    {
        var marker = _status switch
        {
            BashExecutionStatus.Running => "⏳",
            BashExecutionStatus.Complete => "✓",
            BashExecutionStatus.Cancelled => "!",
            BashExecutionStatus.Error => "✗",
            _ => "•",
        };
        var prefix = _excludeFromContext ? "!! $" : "$";
        return $"{marker} {prefix} {_command}";
    }

    /// <summary>
    /// 组装 Bash Panel 的输出预览和状态文本。
    /// </summary>
    /// <param name="width">Panel 可用的终端列数。</param>
    /// <returns>Panel 内容文本。</returns>
    private string FormatPanelBody(int width)
    {
        var sections = new List<string>();
        var availableOutput = GetAvailableOutput();
        if (availableOutput.Length > 0)
        {
            if (_expanded)
            {
                sections.Add(availableOutput);
            }
            else
            {
                var preview = TuiText.TruncateToVisualLines(
                    availableOutput,
                    _previewLines,
                    Math.Max(1, width - 4),
                    paddingX: 0);
                sections.Add(string.Join('\n', preview.VisualLines.Select(static line => line.TrimEnd())));
                if (preview.SkippedCount > 0)
                {
                    sections.Add($"... {preview.SkippedCount} more visual lines ({ExpandHintText()})");
                }
            }
        }

        sections.AddRange(StatusLines());
        return string.Join('\n', sections.Where(static section => section.Length > 0));
    }

    private string FormatCommandHeader()
    {
        var prefix = _excludeFromContext ? "!! $" : "$";
        var value = $"{prefix} {_command}";
        return _theme.CommandFormatter?.Invoke(value, _status) ?? value;
    }

    private string ExpandHintText() =>
        _expandKeyHint is null ? "expand to view" : $"{_expandKeyHint} to expand";

    private string CollapseHintText() =>
        _expandKeyHint is null ? "collapse to preview" : $"{_expandKeyHint} to collapse";

    private string GetAvailableOutput()
    {
        if (_outputLines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join('\n', _outputLines).TrimEnd('\n');
    }

    private IEnumerable<string> StatusLines()
    {
        if (_status == BashExecutionStatus.Running)
        {
            yield return "Running... (Esc to cancel)";
            yield break;
        }

        if (_expanded && _outputLines.Count > _previewLines)
        {
            yield return $"({CollapseHintText()})";
        }

        if (_status == BashExecutionStatus.Cancelled)
        {
            yield return "(cancelled)";
        }
        else if (_status == BashExecutionStatus.Error)
        {
            yield return $"(exit {_exitCode})";
        }

        if (_truncated && _fullOutputPath is not null)
        {
            yield return $"Output truncated. Full output: {_fullOutputPath}";
        }
    }

    private static string NormalizeOutput(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return AnsiRegex()
            .Replace(value, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string FormatLine(string line, int width) =>
        TuiText.TruncateToWidth(line, width, string.Empty, pad: true);

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiRegex();
}

public enum BashExecutionStatus
{
    Running,
    Complete,
    Cancelled,
    Error
}
