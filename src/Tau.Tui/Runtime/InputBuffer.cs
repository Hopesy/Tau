namespace Tau.Tui.Runtime;

public sealed class InputBuffer
{
    public string Draft { get; private set; } = string.Empty;

    public bool HasDraft => !string.IsNullOrWhiteSpace(Draft);

    public void SetDraft(string? value)
    {
        Draft = value ?? string.Empty;
    }

    public string Commit()
    {
        var committed = Draft;
        Draft = string.Empty;
        return committed;
    }

    public void Clear()
    {
        Draft = string.Empty;
    }
}
