namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCommandResult(bool Handled, bool IsError, string? Message)
{
    public static CodingAgentCommandResult NotCommand { get; } = new(false, false, null);

    public static CodingAgentCommandResult Status(string message) => new(true, false, message);

    public static CodingAgentCommandResult Error(string message) => new(true, true, message);
}
