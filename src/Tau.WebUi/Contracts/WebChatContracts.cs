namespace Tau.WebUi.Contracts;

public sealed record CreateSessionRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null);

public sealed record SendMessageRequest(
    string? Text,
    IReadOnlyList<WebChatAttachmentDto>? Attachments = null);

public sealed record UpdateSessionSettingsRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null);

public sealed record WebChatMessageDto(
    string Role,
    string Text,
    DateTimeOffset Timestamp,
    string? Thinking = null,
    IReadOnlyList<string>? ToolEvents = null,
    string? Error = null,
    IReadOnlyList<WebChatAttachmentDto>? Attachments = null,
    IReadOnlyList<WebChatToolCallDto>? ToolCalls = null);

public sealed record WebChatAttachmentDto(
    string Id,
    string Type,
    string FileName,
    string MimeType,
    long Size,
    string Content,
    string? ExtractedText = null,
    string? Preview = null);

public sealed record WebChatToolCallDto(
    string Id,
    string ToolName,
    string Status,
    string? Arguments = null,
    string? Output = null,
    bool IsError = false,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    IReadOnlyList<string>? Updates = null);

public sealed record WebChatStreamEventDto(
    string Type,
    string? Role = null,
    string? Text = null,
    string? Thinking = null,
    string? ToolEvent = null,
    WebChatToolCallDto? ToolCall = null,
    string? Error = null,
    DateTimeOffset? Timestamp = null,
    WebChatSessionDto? Session = null,
    IReadOnlyList<WebChatAttachmentDto>? Attachments = null);

public sealed record WebChatSessionDto(
    string Id,
    string Title,
    string Provider,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Persisted,
    IReadOnlyList<WebChatMessageDto> Messages);

public sealed record CodingAgentJsonlSessionPreviewDto(
    string SessionId,
    int Version,
    DateTimeOffset Timestamp,
    string Cwd,
    string? ParentSession,
    string? FilePath,
    int EntryCount,
    int MessageCount,
    CodingAgentJsonlPreviewFilterDto Filter,
    CodingAgentJsonlTreeMetadataDto Tree,
    CodingAgentJsonlImportAuditDto Audit,
    IReadOnlyList<CodingAgentJsonlTimelineMessageDto> Messages);

public sealed record CodingAgentJsonlPreviewFilterDto(
    string? Search,
    bool CurrentBranchOnly,
    int TotalMessageCount,
    int MatchedMessageCount,
    IReadOnlyList<string> MatchedEntryIds);

public sealed record CodingAgentJsonlTreeMetadataDto(
    string? LeafEntryId,
    int RootEntryCount,
    int BranchPointCount,
    int BranchEntryCount,
    int BranchMessageCount,
    int LabelCount,
    IReadOnlyDictionary<string, int> EntryTypes,
    IReadOnlyList<string> CurrentBranchEntryIds,
    IReadOnlyList<CodingAgentJsonlEntryMetadataDto> Entries);

public sealed record CodingAgentJsonlEntryMetadataDto(
    string EntryId,
    string Type,
    string? ParentEntryId,
    DateTimeOffset Timestamp,
    int Depth,
    int ChildCount,
    bool IsCurrentLeaf,
    bool IsOnCurrentBranch,
    string? Label = null,
    DateTimeOffset? LabelTimestamp = null);

public sealed record CodingAgentJsonlTimelineMessageDto(
    string EntryId,
    string? ParentEntryId,
    DateTimeOffset Timestamp,
    string Role,
    string TextPreview,
    int TextLength,
    int ContentPartCount,
    bool HasThinking,
    int ToolCallCount,
    int ImageCount,
    string? ToolCallId = null,
    bool? IsError = null);

public sealed record CodingAgentJsonlImportAuditDto(
    bool IsBranched,
    bool WillImportTimelineMessagesOnly,
    bool WillImportCurrentBranchOnly,
    int ImportedMessageCount,
    int NonImportedEntryCount,
    int CurrentBranchMessageCount,
    int OffBranchMessageCount,
    IReadOnlyList<CodingAgentJsonlBranchTimelineEntryDto> CurrentBranchTimeline,
    IReadOnlyList<CodingAgentJsonlBranchLabelDto> BranchLabels,
    IReadOnlyList<CodingAgentJsonlAuditWarningDto> Warnings);

public sealed record CodingAgentJsonlBranchTimelineEntryDto(
    string EntryId,
    string Type,
    DateTimeOffset Timestamp,
    string? Role,
    string? TextPreview,
    string? Label,
    bool IsCurrentLeaf,
    bool WillImportAsMessage);

public sealed record CodingAgentJsonlBranchLabelDto(
    string EntryId,
    string Label,
    DateTimeOffset Timestamp,
    bool IsOnCurrentBranch);

public sealed record CodingAgentJsonlAuditWarningDto(
    string Code,
    string Message,
    string? EntryId = null);

public sealed record CodingAgentJsonlImportResultDto(
    WebChatSessionDto Session,
    CodingAgentJsonlTreeMetadataDto SourceTree,
    CodingAgentJsonlImportAuditDto SourceAudit);

public sealed record WebUiModelOptionDto(
    string Id,
    string Name,
    bool Reasoning,
    int ContextWindow,
    int MaxOutputTokens,
    bool RequiresAuth,
    string? Api,
    string? BaseUrl);

public sealed record WebUiProviderOptionDto(
    string Id,
    IReadOnlyList<WebUiModelOptionDto> Models);

public sealed record WebUiCatalogDto(
    IReadOnlyList<WebUiProviderOptionDto> Providers);

public sealed record WebUiAuthStatusDto(
    string Provider,
    bool IsConfigured,
    string Source,
    bool UsesOAuth,
    bool CanLogin,
    string Message);

public sealed record WebUiStatusDto(
    string Product,
    string DefaultProvider,
    string DefaultModel,
    bool HasOpenAiApiKey,
    int SessionCount,
    string Mode,
    bool PersistenceEnabled,
    string SessionsPath);
