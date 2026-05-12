namespace Tau.Mom;

public static class MomChannelCommands
{
    public static bool IsStopCommand(string? text)
    {
        return string.Equals(text?.Trim(), "stop", StringComparison.OrdinalIgnoreCase);
    }
}
