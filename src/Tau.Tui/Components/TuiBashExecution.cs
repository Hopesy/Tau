using System.Text.RegularExpressions;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed partial class TuiBashExecution : ITuiComponent
{
    public const int DefaultPreviewLines = 20;
    private readonly string _command;
    private readonly bool _excludeFromContext;
    private readonly int _previewLines;
    private readonly List<string> _outputLines = [];
    private bool _expanded;
    private BashExecutionStatus _status = BashExecutionStatus.Running;
    private int? _exitCode;
    private bool _truncated;
    private string? _fullOutputPath;
    private string? _expandKeyHint;

    public TuiBashExecution(
        string command,
        bool excludeFromContext = false,
        int previewLines = DefaultPreviewLines)
    {
        _command = command ?? string.Empty;
        _excludeFromContext = excludeFromContext;
        _previewLines = Math.Max(1, previewLines);
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
            lines.Add(FormatLine(status, width));
        }

        return lines;
    }

    private string FormatCommandHeader()
    {
        var prefix = _excludeFromContext ? "!! $" : "$";
        return $"{prefix} {_command}";
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
