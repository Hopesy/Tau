namespace Tau.Tui.Runtime;

public sealed record TranscriptEntry(TranscriptEntryKind Kind, string Text);

public enum TranscriptEntryKind
{
    System,
    User,
    Assistant,
    Thinking,
    Tool,
    Error,
    Status
}
