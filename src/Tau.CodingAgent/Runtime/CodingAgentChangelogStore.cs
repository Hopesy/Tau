using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentChangelogEntry(
    string Date,
    string Area,
    string UserValue,
    string Summary);

public sealed record CodingAgentChangelogSnapshot(
    string FilePath,
    IReadOnlyList<CodingAgentChangelogEntry> Entries,
    string? Error);

public sealed class CodingAgentChangelogStore
{
    public const string ChangelogFileEnvironmentVariable = "TAU_CODING_AGENT_CHANGELOG_FILE";

    private readonly string _path;

    public CodingAgentChangelogStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public static string GetDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable(ChangelogFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        var current = Environment.CurrentDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = System.IO.Path.Combine(current, "docs", "releases", "feature-release-notes.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, "docs", "releases", "feature-release-notes.md");
    }

    public CodingAgentChangelogSnapshot Load()
    {
        if (!File.Exists(_path))
        {
            return new CodingAgentChangelogSnapshot(_path, [], null);
        }

        try
        {
            var text = File.ReadAllText(_path);
            return new CodingAgentChangelogSnapshot(_path, ParseReleaseNotes(text), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodingAgentChangelogSnapshot(_path, [], ex.Message);
        }
    }

    public static string Format(CodingAgentChangelogSnapshot snapshot, int maxEntries)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            return $"changelog: failed to load {snapshot.FilePath}: {snapshot.Error}";
        }

        if (snapshot.Entries.Count == 0)
        {
            return $"changelog: no release notes found at {snapshot.FilePath}";
        }

        var limit = maxEntries == int.MaxValue ? snapshot.Entries.Count : Math.Min(maxEntries, snapshot.Entries.Count);
        var builder = new StringBuilder();
        builder.Append("changelog: ")
            .Append(limit)
            .Append('/')
            .Append(snapshot.Entries.Count)
            .Append(" entries from ")
            .AppendLine(snapshot.FilePath);

        for (var i = 0; i < limit; i++)
        {
            var entry = snapshot.Entries[i];
            builder.Append("  [")
                .Append(i + 1)
                .Append("] ")
                .Append(entry.Date)
                .Append(' ')
                .AppendLine(entry.Area);
            builder.Append("      用户价值: ")
                .AppendLine(entry.UserValue);
            builder.Append("      变更摘要: ")
                .AppendLine(entry.Summary);
        }

        if (limit < snapshot.Entries.Count)
        {
            builder.Append("Use /changelog all to show all entries.");
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<CodingAgentChangelogEntry> ParseReleaseNotes(string text)
    {
        var entries = new List<CodingAgentChangelogEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|'))
            {
                continue;
            }

            var cells = SplitMarkdownTableRow(line);
            if (cells.Count < 4 || IsHeaderRow(cells) || IsSeparatorRow(cells))
            {
                continue;
            }

            entries.Add(new CodingAgentChangelogEntry(
                NormalizeCell(cells[0]),
                NormalizeCell(cells[1]),
                NormalizeCell(cells[2]),
                NormalizeCell(cells[3])));
        }

        return entries;
    }

    private static IReadOnlyList<string> SplitMarkdownTableRow(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        for (var i = 1; i < line.Length - 1; i++)
        {
            var ch = line[i];
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool IsHeaderRow(IReadOnlyList<string> cells) =>
        cells[0].Equals("日期", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeparatorRow(IReadOnlyList<string> cells) =>
        cells.All(static cell => cell.Length > 0 && cell.All(static ch => ch is '-' or ':' or ' '));

    private static string NormalizeCell(string value) =>
        value.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();
}
