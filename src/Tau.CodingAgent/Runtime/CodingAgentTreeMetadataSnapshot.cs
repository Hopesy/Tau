namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentTreeMetadataSnapshot(
    string FilePath,
    string SessionId,
    string? LeafId,
    string Cwd,
    string? ParentSession,
    int EntryCount,
    int BranchEntryCount,
    int MessageCount,
    int BranchMessageCount,
    int BranchCount,
    int LabelCount,
    string? FocusEntryId,
    IReadOnlyList<string> VisibleEntryIds,
    IReadOnlyDictionary<string, CodingAgentTreeMetadataEntrySnapshot> EntriesById);

public sealed record CodingAgentTreeMetadataEntrySnapshot(
    string EntryId,
    string SummaryLine,
    IReadOnlyList<string> OverviewLines,
    IReadOnlyList<CodingAgentTreeMetadataRelationSnapshot> Relations,
    IReadOnlyList<CodingAgentTreeMetadataSectionSnapshot> Sections);

public sealed record CodingAgentTreeMetadataRelationSnapshot(
    string Label,
    string EntryId);

public sealed record CodingAgentTreeMetadataSectionSnapshot(
    string Title,
    IReadOnlyList<string> Lines);
