namespace Tau.WebUi.Contracts;

public sealed record CreateSessionRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null);

public sealed record SendMessageRequest(string Text);

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
    string? Error = null);

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

public sealed record WebUiStatusDto(
    string Product,
    string DefaultProvider,
    string DefaultModel,
    bool HasOpenAiApiKey,
    int SessionCount,
    string Mode,
    bool PersistenceEnabled,
    string SessionsPath);
