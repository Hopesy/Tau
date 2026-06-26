using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public enum TuiMessageRole
{
    User,
    Assistant,
    Thinking,
    System,
    Tool,
    BranchSummary,
    CompactionSummary,
    Custom,
    Skill,
    Error,
    Status,
}

public sealed record TuiMessage(TuiMessageRole Role, string Text);

public sealed class TuiMessageArea : ITuiComponent
{
    private readonly List<TuiMessage> _messages;
    private readonly int? _maxVisibleLines;

    public TuiMessageArea(IEnumerable<TuiMessage>? messages = null, int? maxVisibleLines = null)
    {
        _messages = messages?.ToList() ?? [];
        _maxVisibleLines = maxVisibleLines is null ? null : Math.Max(1, maxVisibleLines.Value);
    }

    public IReadOnlyList<TuiMessage> Messages => _messages;
    public int? MaxVisibleLines => _maxVisibleLines;

    public void Add(TuiMessage message) => _messages.Add(message);

    public void SetMessages(IEnumerable<TuiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.Clear();
        _messages.AddRange(messages);
    }

    public void Clear() => _messages.Clear();

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width) =>
        RenderMessages(_messages, width, _maxVisibleLines);

    public static IReadOnlyList<string> RenderMessages(
        IEnumerable<TuiMessage> messages,
        int width,
        int? maxVisibleLines = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (var message in messages)
        {
            AddMessageLines(lines, message, width);
        }

        if (maxVisibleLines is not { } requestedVisibleLines || lines.Count <= requestedVisibleLines)
        {
            return lines;
        }

        var visibleLines = Math.Max(1, requestedVisibleLines);
        return lines.Skip(lines.Count - visibleLines).ToArray();
    }

    private static void AddMessageLines(List<string> output, TuiMessage message, int width)
    {
        var prefix = PrefixFor(message.Role);
        var prefixWidth = TuiText.VisibleWidth(prefix);
        var text = message.Text ?? string.Empty;

        if (width <= prefixWidth)
        {
            foreach (var line in TuiText.Wrap(prefix + text, width))
            {
                output.Add(TuiText.TruncateToWidth(line, width, string.Empty, pad: true));
            }

            return;
        }

        var contentWidth = Math.Max(1, width - prefixWidth);
        var wrapped = TuiText.Wrap(text, contentWidth);
        for (var i = 0; i < wrapped.Count; i++)
        {
            var lead = i == 0 ? prefix : new string(' ', prefixWidth);
            output.Add(TuiText.TruncateToWidth(lead + wrapped[i], width, string.Empty, pad: true));
        }
    }

    private static string PrefixFor(TuiMessageRole role) =>
        role switch
        {
            TuiMessageRole.User => "you> ",
            TuiMessageRole.Assistant => "tau> ",
            TuiMessageRole.Thinking => "thinking> ",
            TuiMessageRole.System => "system> ",
            TuiMessageRole.Tool => "tool> ",
            TuiMessageRole.BranchSummary => "branch> ",
            TuiMessageRole.CompactionSummary => "compaction> ",
            TuiMessageRole.Custom => "custom> ",
            TuiMessageRole.Skill => "skill> ",
            TuiMessageRole.Error => "error> ",
            TuiMessageRole.Status => "status> ",
            _ => "msg> ",
        };
}
