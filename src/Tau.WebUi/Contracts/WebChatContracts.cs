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
