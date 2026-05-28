namespace Tau.Mom;

public sealed record MomSandboxValidationResult(
    string Sandbox,
    bool Succeeded,
    string Message);

public sealed record MomSandboxValidationJsonResult(
    bool Succeeded,
    string Message,
    string Sandbox,
    string? Error);

public sealed record MomSlackValidationResult(
    bool Succeeded,
    string Message,
    bool SlackSocketModeEnabled,
    string? BotUserId,
    string? SocketHost,
    string? Error);

public sealed record MomSlackValidationJsonResult(
    bool Succeeded,
    string Message,
    bool SlackSocketModeEnabled,
    string? BotUserId,
    string? SocketHost,
    string? Error);
