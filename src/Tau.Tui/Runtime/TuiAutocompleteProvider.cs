using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

public sealed record TuiAutocompleteItem(
    string Value,
    string Label,
    string? Description = null);

public sealed record TuiAutocompleteSuggestions(
    IReadOnlyList<TuiAutocompleteItem> Items,
    string Prefix);

public sealed record TuiCompletionResult(
    string Text,
    int CursorIndex);

public sealed record TuiSlashCommand(
    string Name,
    string? Description = null,
    string? ArgumentHint = null,
    Func<string, CancellationToken, ValueTask<IReadOnlyList<TuiAutocompleteItem>?>>? GetArgumentCompletionsAsync = null);

public interface ITuiAutocompleteProvider
{
    ValueTask<TuiAutocompleteSuggestions?> GetSuggestionsAsync(
        string text,
        int cursorIndex,
        bool force = false,
        CancellationToken cancellationToken = default);

    TuiCompletionResult ApplyCompletion(
        string text,
        int cursorIndex,
        TuiAutocompleteItem item,
        string prefix);
}

public sealed class TuiCombinedAutocompleteProvider : ITuiAutocompleteProvider
{
    private static readonly HashSet<char> PathDelimiters = [' ', '\t', '"', '\'', '='];

    private readonly IReadOnlyList<TuiSlashCommand> _commands;
    private readonly string _basePath;

    public TuiCombinedAutocompleteProvider(
        IEnumerable<TuiSlashCommand>? commands = null,
        string? basePath = null)
    {
        _commands = (commands ?? []).ToArray();
        _basePath = string.IsNullOrWhiteSpace(basePath)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(basePath);
    }

    public async ValueTask<TuiAutocompleteSuggestions?> GetSuggestionsAsync(
        string text,
        int cursorIndex,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        text ??= string.Empty;
        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var textBeforeCursor = text[..cursorIndex];
        var currentLine = CurrentLineBeforeCursor(textBeforeCursor);

        var atPrefix = ExtractAtPrefix(currentLine);
        if (atPrefix is not null)
        {
            var suggestions = GetFileSuggestions(atPrefix);
            return suggestions.Count == 0 ? null : new TuiAutocompleteSuggestions(suggestions, atPrefix);
        }

        if (currentLine.StartsWith("/", StringComparison.Ordinal) && !force)
        {
            var spaceIndex = currentLine.IndexOf(' ', StringComparison.Ordinal);
            if (spaceIndex < 0)
            {
                var commandPrefix = currentLine[1..];
                var commands = TuiFuzzyMatcher
                    .Filter(_commands, commandPrefix, static command => command.Name)
                    .Select(ToCommandItem)
                    .ToArray();

                return commands.Length == 0
                    ? null
                    : new TuiAutocompleteSuggestions(commands, currentLine);
            }

            var commandName = currentLine[1..spaceIndex];
            var argumentPrefix = currentLine[(spaceIndex + 1)..];
            var command = _commands.FirstOrDefault(
                item => string.Equals(item.Name, commandName, StringComparison.Ordinal));
            if (command?.GetArgumentCompletionsAsync is null)
            {
                return null;
            }

            var argumentSuggestions = await command.GetArgumentCompletionsAsync(argumentPrefix, cancellationToken)
                .ConfigureAwait(false);
            return argumentSuggestions is null || argumentSuggestions.Count == 0
                ? null
                : new TuiAutocompleteSuggestions(argumentSuggestions, argumentPrefix);
        }

        var pathPrefix = ExtractPathPrefix(currentLine, force);
        if (pathPrefix is null)
        {
            return null;
        }

        var pathSuggestions = GetFileSuggestions(pathPrefix);
        return pathSuggestions.Count == 0 ? null : new TuiAutocompleteSuggestions(pathSuggestions, pathPrefix);
    }

    public TuiCompletionResult ApplyCompletion(
        string text,
        int cursorIndex,
        TuiAutocompleteItem item,
        string prefix)
    {
        text ??= string.Empty;
        prefix ??= string.Empty;
        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var prefixStart = Math.Max(0, cursorIndex - prefix.Length);
        var beforePrefix = text[..prefixStart];
        var afterCursor = text[cursorIndex..];
        var isDirectory = item.Label.EndsWith("/", StringComparison.Ordinal);
        var isQuotedPrefix = prefix.StartsWith("\"", StringComparison.Ordinal) ||
            prefix.StartsWith("@\"", StringComparison.Ordinal);
        var adjustedAfterCursor = isQuotedPrefix &&
            item.Value.EndsWith("\"", StringComparison.Ordinal) &&
            afterCursor.StartsWith("\"", StringComparison.Ordinal)
                ? afterCursor[1..]
                : afterCursor;

        var isSlashCommand = prefix.StartsWith("/", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(beforePrefix) &&
            !prefix[1..].Contains('/', StringComparison.Ordinal);
        if (isSlashCommand)
        {
            var replacement = "/" + item.Value + " ";
            return new TuiCompletionResult(
                beforePrefix + replacement + adjustedAfterCursor,
                beforePrefix.Length + replacement.Length);
        }

        var suffix = prefix.StartsWith("@", StringComparison.Ordinal) && !isDirectory ? " " : string.Empty;
        var newText = beforePrefix + item.Value + suffix + adjustedAfterCursor;
        var cursorOffset = isDirectory && item.Value.EndsWith("\"", StringComparison.Ordinal)
            ? item.Value.Length - 1
            : item.Value.Length;
        return new TuiCompletionResult(newText, beforePrefix.Length + cursorOffset + suffix.Length);
    }

    private static TuiAutocompleteItem ToCommandItem(TuiSlashCommand command)
    {
        var description = command.ArgumentHint is null
            ? command.Description
            : string.IsNullOrWhiteSpace(command.Description)
                ? command.ArgumentHint
                : $"{command.ArgumentHint} - {command.Description}";
        return new TuiAutocompleteItem(command.Name, command.Name, description);
    }

    private IReadOnlyList<TuiAutocompleteItem> GetFileSuggestions(string prefix)
    {
        var parsed = ParsePathPrefix(prefix);
        var rawPrefix = parsed.RawPrefix;
        var expandedPrefix = ExpandHomePath(rawPrefix);

        string searchDirectory;
        string searchPrefix;
        if (IsRootPrefix(rawPrefix, parsed.IsAtPrefix))
        {
            searchDirectory = ResolveSearchDirectory(expandedPrefix);
            searchPrefix = string.Empty;
        }
        else if (rawPrefix.EndsWith("/", StringComparison.Ordinal) ||
                 rawPrefix.EndsWith("\\", StringComparison.Ordinal))
        {
            searchDirectory = ResolveSearchDirectory(expandedPrefix);
            searchPrefix = string.Empty;
        }
        else
        {
            var directoryPart = Path.GetDirectoryName(expandedPrefix);
            searchDirectory = string.IsNullOrEmpty(directoryPart)
                ? _basePath
                : ResolveSearchDirectory(directoryPart);
            searchPrefix = Path.GetFileName(expandedPrefix);
        }

        if (!Directory.Exists(searchDirectory))
        {
            return [];
        }

        var items = new List<TuiAutocompleteItem>();
        foreach (var path in Directory.EnumerateFileSystemEntries(searchDirectory))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDirectory = Directory.Exists(path);
            var displayPath = BuildDisplayPath(rawPrefix, name);
            if (isDirectory)
            {
                displayPath = EnsureForwardSlash(displayPath.TrimEnd('/', '\\') + "/");
            }
            else
            {
                displayPath = EnsureForwardSlash(displayPath);
            }

            var value = BuildCompletionValue(
                displayPath,
                parsed.IsAtPrefix,
                parsed.IsQuotedPrefix);
            items.Add(new TuiAutocompleteItem(
                value,
                name + (isDirectory ? "/" : string.Empty),
                EnsureForwardSlash(displayPath)));
        }

        return items
            .OrderByDescending(static item => item.Label.EndsWith("/", StringComparison.Ordinal))
            .ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string ResolveSearchDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return _basePath;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(path, _basePath);
    }

    private static string CurrentLineBeforeCursor(string textBeforeCursor)
    {
        var newline = textBeforeCursor.LastIndexOf('\n');
        return newline < 0 ? textBeforeCursor : textBeforeCursor[(newline + 1)..];
    }

    private static string? ExtractAtPrefix(string text)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix?.StartsWith("@\"", StringComparison.Ordinal) == true)
        {
            return quotedPrefix;
        }

        var tokenStart = FindTokenStart(text);
        return tokenStart < text.Length && text[tokenStart] == '@'
            ? text[tokenStart..]
            : null;
    }

    private static string? ExtractPathPrefix(string text, bool force)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix is not null)
        {
            return quotedPrefix;
        }

        var tokenStart = FindTokenStart(text);
        var token = text[tokenStart..];
        if (force)
        {
            return token;
        }

        if (token.Contains('/', StringComparison.Ordinal) ||
            token.Contains('\\', StringComparison.Ordinal) ||
            token.StartsWith(".", StringComparison.Ordinal) ||
            token.StartsWith("~/", StringComparison.Ordinal))
        {
            return token;
        }

        return token.Length == 0 && text.EndsWith(' ') ? token : null;
    }

    private static string? ExtractQuotedPrefix(string text)
    {
        var inQuotes = false;
        var quoteStart = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"')
            {
                continue;
            }

            inQuotes = !inQuotes;
            if (inQuotes)
            {
                quoteStart = index;
            }
        }

        if (!inQuotes || quoteStart < 0)
        {
            return null;
        }

        if (quoteStart > 0 && text[quoteStart - 1] == '@')
        {
            return IsTokenStart(text, quoteStart - 1) ? text[(quoteStart - 1)..] : null;
        }

        return IsTokenStart(text, quoteStart) ? text[quoteStart..] : null;
    }

    private static int FindTokenStart(string text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (PathDelimiters.Contains(text[index]))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static bool IsTokenStart(string text, int index) =>
        index == 0 || PathDelimiters.Contains(text[index - 1]);

    private static ParsedPathPrefix ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal))
        {
            return new ParsedPathPrefix(prefix[2..], IsAtPrefix: true, IsQuotedPrefix: true);
        }

        if (prefix.StartsWith("\"", StringComparison.Ordinal))
        {
            return new ParsedPathPrefix(prefix[1..], IsAtPrefix: false, IsQuotedPrefix: true);
        }

        return prefix.StartsWith("@", StringComparison.Ordinal)
            ? new ParsedPathPrefix(prefix[1..], IsAtPrefix: true, IsQuotedPrefix: false)
            : new ParsedPathPrefix(prefix, IsAtPrefix: false, IsQuotedPrefix: false);
    }

    private static bool IsRootPrefix(string rawPrefix, bool isAtPrefix) =>
        rawPrefix.Length == 0 ||
        rawPrefix is "." or "./" or ".." or "../" or "~" or "~/" or "/" ||
        (isAtPrefix && rawPrefix.Length == 0);

    private static string ExpandHomePath(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (!path.StartsWith("~/", StringComparison.Ordinal))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path[2..]);
    }

    private static string BuildDisplayPath(string rawPrefix, string name)
    {
        if (rawPrefix.EndsWith("/", StringComparison.Ordinal) ||
            rawPrefix.EndsWith("\\", StringComparison.Ordinal))
        {
            return rawPrefix + name;
        }

        if (rawPrefix.StartsWith("~/", StringComparison.Ordinal))
        {
            var relative = rawPrefix[2..];
            var directory = Path.GetDirectoryName(relative);
            return "~/" + (string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name));
        }

        if (rawPrefix.Contains('/', StringComparison.Ordinal) ||
            rawPrefix.Contains('\\', StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(rawPrefix);
            if (string.IsNullOrEmpty(directory))
            {
                return name;
            }

            if (directory == ".")
            {
                return "./" + name;
            }

            var combined = Path.Combine(directory, name);
            return rawPrefix.StartsWith("./", StringComparison.Ordinal) &&
                !combined.StartsWith("./", StringComparison.Ordinal)
                    ? "./" + combined
                    : combined;
        }

        return name;
    }

    private static string BuildCompletionValue(string path, bool isAtPrefix, bool isQuotedPrefix)
    {
        var needsQuotes = isQuotedPrefix || path.Contains(' ', StringComparison.Ordinal);
        var atPrefix = isAtPrefix ? "@" : string.Empty;
        return needsQuotes ? $"{atPrefix}\"{path}\"" : atPrefix + path;
    }

    private static string EnsureForwardSlash(string path) => path.Replace('\\', '/');

    private sealed record ParsedPathPrefix(string RawPrefix, bool IsAtPrefix, bool IsQuotedPrefix);
}
