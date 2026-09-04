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

/// <summary>
/// 定义可选的交互式补全列表渲染能力。
/// </summary>
public interface IInteractiveAutocompleteRenderer
{
    /// <summary>
    /// 更新当前输入区下方显示的补全候选项。
    /// </summary>
    /// <param name="items">需要显示的候选项集合。</param>
    /// <param name="selectedIndex">当前选中项索引；没有选中项时为负数。</param>
    void RenderAutocomplete(IReadOnlyList<TuiAutocompleteItem> items, int selectedIndex);
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
            var suggestions = GetFuzzyFileSuggestions(atPrefix);
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
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFileSystemEntries(
                searchDirectory,
                "*",
                SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var path in candidates)
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
            var displayName = name;
            var displayPath = BuildDisplayPath(rawPrefix, displayName);
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
                displayName + (isDirectory ? "/" : string.Empty),
                EnsureForwardSlash(displayPath)));

            if (items.Count >= 200)
            {
                break;
            }
        }

        return items
            .OrderByDescending(static item => item.Label.EndsWith("/", StringComparison.Ordinal))
            .ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 为 @ 文件附件执行递归模糊搜索。
    /// 参数 prefix 是包含 @、可选引号和路径片段的当前补全前缀，返回值是按匹配分数排序的补全项。
    /// </summary>
    private IReadOnlyList<TuiAutocompleteItem> GetFuzzyFileSuggestions(string prefix)
    {
        var parsed = ParsePathPrefix(prefix);
        var rawPrefix = EnsureForwardSlash(parsed.RawPrefix);
        var searchDirectory = _basePath;
        var query = rawPrefix;
        string? displayBase = null;

        // 1. 路径包含目录段且目录存在时，把搜索范围限定在该目录
        var slashIndex = rawPrefix.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            var candidateDisplayBase = rawPrefix[..(slashIndex + 1)];
            var candidateDirectory = ResolveSearchDirectory(ExpandHomePath(candidateDisplayBase));
            if (Directory.Exists(candidateDirectory))
            {
                searchDirectory = candidateDirectory;
                query = rawPrefix[(slashIndex + 1)..];
                displayBase = candidateDisplayBase;
            }
        }

        IEnumerable<string> candidates;
        try
        {
            // 2. 一次性物化枚举，确保遍历期间的 IO 异常也能被统一处理
            candidates = Directory.EnumerateFileSystemEntries(
                searchDirectory,
                "*",
                SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var scored = new List<(string Path, bool IsDirectory, int Score)>();
        foreach (var candidate in candidates)
        {
            var relativePath = EnsureForwardSlash(Path.GetRelativePath(searchDirectory, candidate));
            if (IsGitPath(relativePath))
            {
                continue;
            }

            var isDirectory = Directory.Exists(candidate);
            var score = ScoreFuzzyPath(relativePath, query, isDirectory);
            if (score > 0)
            {
                scored.Add((relativePath, isDirectory, score));
            }
        }

        // 3. 先按匹配质量排序，再按路径深度和名称稳定排序
        var topEntries = scored
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Path.Count(static character => character == '/'))
            .ThenBy(static item => item.Path.Length)
            .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(20);

        var items = new List<TuiAutocompleteItem>();
        foreach (var entry in topEntries)
        {
            var displayPath = displayBase is null
                ? entry.Path
                : displayBase + entry.Path;
            displayPath = EnsureForwardSlash(displayPath);
            var completionPath = entry.IsDirectory
                ? displayPath.TrimEnd('/') + "/"
                : displayPath;
            var value = BuildCompletionValue(
                completionPath,
                parsed.IsAtPrefix,
                parsed.IsQuotedPrefix);
            var label = Path.GetFileName(entry.Path) + (entry.IsDirectory ? "/" : string.Empty);
            items.Add(new TuiAutocompleteItem(value, label, displayPath));
        }

        return items;
    }

    /// <summary>
    /// 计算文件附件路径与查询文本的匹配分数。
    /// 参数 relativePath 是相对搜索目录的路径，query 是用户输入的查询，isDirectory 表示是否为目录；返回值越大表示匹配越优。
    /// </summary>
    private static int ScoreFuzzyPath(string relativePath, string query, bool isDirectory)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 1;
        }

        var fileName = Path.GetFileName(relativePath);
        var lowerFileName = fileName.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();
        var score = lowerFileName == lowerQuery
            ? 100
            : lowerFileName.StartsWith(lowerQuery, StringComparison.Ordinal)
                ? 80
                : lowerFileName.Contains(lowerQuery, StringComparison.Ordinal)
                    ? 50
                    : relativePath.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal)
                        ? 30
                        : 0;

        return isDirectory && score > 0 ? score + 10 : score;
    }

    /// <summary>
    /// 判断相对路径是否位于 .git 目录中。
    /// 参数 relativePath 是待检查的相对路径；返回值表示是否应从补全结果中排除。
    /// </summary>
    private static bool IsGitPath(string relativePath)
    {
        return string.Equals(relativePath, ".git", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
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
