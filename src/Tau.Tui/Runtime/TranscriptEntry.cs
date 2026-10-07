namespace Tau.Tui.Runtime;

public sealed record TranscriptEntry(TranscriptEntryKind Kind, string Text, string? Key = null)
{
    public bool ApplyMarkdownTransform { get; init; } = true;
}

public enum TranscriptEntryKind
{
    System,
    User,
    Assistant,
    Thinking,
    Tool,
    BranchSummary,
    CompactionSummary,
    Custom,
    Skill,
    Error,
    Status
}
