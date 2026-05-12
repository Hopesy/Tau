namespace Tau.Mom;

public static class MomChannelWorkspace
{
    public static string ResolveWorkingDirectory(string defaultWorkingDirectory, string? channelId)
    {
        var safeChannelId = MakeSafePathSegment(channelId);
        return Path.GetFullPath(safeChannelId, Path.GetFullPath(defaultWorkingDirectory));
    }

    public static string MakeSafePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "channel";
        }

        var chars = value.Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "channel" : safe;
    }
}
