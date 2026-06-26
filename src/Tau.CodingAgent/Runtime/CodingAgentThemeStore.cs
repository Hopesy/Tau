using System.Globalization;
using System.Text.Json;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentTheme(
    string Name,
    string? FilePath,
    string Scope,
    IReadOnlyDictionary<string, string> Colors,
    IReadOnlyDictionary<string, string> ExportColors)
{
    public TuiSyntaxHighlightTheme ToSyntaxHighlightTheme() =>
        TuiSyntaxHighlightTheme.FromAnsiColors(Colors);
}

public sealed record CodingAgentThemeDiagnostic(
    string Severity,
    string Message,
    string Path,
    string Scope,
    CodingAgentResourceCollision? Collision = null);

public sealed record CodingAgentThemeStatus(
    IReadOnlyList<CodingAgentTheme> Themes,
    IReadOnlyList<CodingAgentThemeDiagnostic> Diagnostics)
{
    public IReadOnlyList<CodingAgentResourceDiagnostic> ResourceDiagnostics =>
        CodingAgentResourceDiagnostics.FromThemes(Diagnostics);
}

public sealed class CodingAgentThemeStore
{
    public const string ThemePathsEnvironmentVariable = "TAU_CODING_AGENT_THEME_PATHS";
    public const string DefaultThemeName = "dark";

    private static readonly string[] RequiredColorKeys =
    [
        "accent",
        "border",
        "borderAccent",
        "borderMuted",
        "success",
        "error",
        "warning",
        "muted",
        "dim",
        "text",
        "thinkingText",
        "selectedBg",
        "userMessageBg",
        "userMessageText",
        "customMessageBg",
        "customMessageText",
        "customMessageLabel",
        "toolPendingBg",
        "toolSuccessBg",
        "toolErrorBg",
        "toolTitle",
        "toolOutput",
        "mdHeading",
        "mdLink",
        "mdLinkUrl",
        "mdCode",
        "mdCodeBlock",
        "mdCodeBlockBorder",
        "mdQuote",
        "mdQuoteBorder",
        "mdHr",
        "mdListBullet",
        "toolDiffAdded",
        "toolDiffRemoved",
        "toolDiffContext",
        "syntaxComment",
        "syntaxKeyword",
        "syntaxFunction",
        "syntaxVariable",
        "syntaxString",
        "syntaxNumber",
        "syntaxType",
        "syntaxOperator",
        "syntaxPunctuation",
        "thinkingOff",
        "thinkingMinimal",
        "thinkingLow",
        "thinkingMedium",
        "thinkingHigh",
        "thinkingXhigh",
        "bashMode"
    ];

    private static readonly string[] ExportColorKeys = ["pageBg", "cardBg", "infoBg"];

    private readonly string _cwd;
    private readonly string _userThemesDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly bool _includeDefaults;

    public CodingAgentThemeStore(
        string? cwd = null,
        string? userThemesDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userThemesDirectory = string.IsNullOrWhiteSpace(userThemesDirectory)
            ? GetDefaultUserThemesDirectory()
            : Path.GetFullPath(userThemesDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredThemePaths();
        _additionalPathsProvider = additionalPathsProvider;
        _includeDefaults = includeDefaults;
    }

    public CodingAgentThemeStatus LoadStatus()
    {
        var loaded = new List<CodingAgentTheme>();
        var diagnostics = new List<CodingAgentThemeDiagnostic>();

        if (_includeDefaults)
        {
            loaded.Add(CreateBuiltInTheme("dark"));
            loaded.Add(CreateBuiltInTheme("light"));
            LoadFromDirectory(_userThemesDirectory, "user", loaded, diagnostics, reportMissing: false);
            LoadFromDirectory(Path.Combine(_cwd, ".tau", "themes"), "project", loaded, diagnostics, reportMissing: false);
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                LoadFromDirectory(resolved, "path", loaded, diagnostics, reportMissing: true);
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                LoadFromFile(resolved, "path", loaded, diagnostics);
            }
            else if (File.Exists(resolved))
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "warning",
                    "theme path is not a json file",
                    resolved,
                    "path"));
            }
            else
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "warning",
                    "theme path does not exist",
                    resolved,
                    "path"));
            }
        }

        var deduped = DedupeLastWins(loaded, diagnostics);
        return new CodingAgentThemeStatus(
            deduped.OrderBy(static theme => theme.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics.ToArray());
    }

    public CodingAgentTheme? Find(string? name)
    {
        var requested = string.IsNullOrWhiteSpace(name) ? DefaultThemeName : name.Trim();
        return LoadStatus().Themes.FirstOrDefault(theme =>
            theme.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> GetExplicitPaths()
    {
        foreach (var path in _explicitPaths)
        {
            yield return path;
        }

        if (_additionalPathsProvider is null)
        {
            yield break;
        }

        foreach (var path in _additionalPathsProvider())
        {
            yield return path;
        }
    }

    private static CodingAgentTheme CreateBuiltInTheme(string name) =>
        new(
            name,
            null,
            "builtin",
            RequiredColorKeys.ToDictionary(static key => key, static _ => string.Empty, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static IReadOnlyList<CodingAgentTheme> DedupeLastWins(
        IReadOnlyList<CodingAgentTheme> themes,
        ICollection<CodingAgentThemeDiagnostic> diagnostics)
    {
        var winners = new Dictionary<string, CodingAgentTheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            if (winners.TryGetValue(theme.Name, out var previous))
            {
                var winnerPath = theme.FilePath ?? "<builtin>";
                var loserPath = previous.FilePath ?? "<builtin>";
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "warning",
                    $"theme name \"{theme.Name}\" collision; later theme wins",
                    winnerPath,
                    theme.Scope,
                    new CodingAgentResourceCollision(
                        CodingAgentResourceTypes.Theme,
                        theme.Name,
                        winnerPath,
                        loserPath,
                        theme.Scope,
                        previous.Scope)));
            }

            winners[theme.Name] = theme;
        }

        return winners.Values.ToArray();
    }

    private static void LoadFromDirectory(
        string directory,
        string scope,
        ICollection<CodingAgentTheme> themes,
        ICollection<CodingAgentThemeDiagnostic> diagnostics,
        bool reportMissing)
    {
        if (!Directory.Exists(directory))
        {
            if (reportMissing)
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "warning",
                    "theme directory does not exist",
                    directory,
                    scope));
            }

            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentThemeDiagnostic(
                "warning",
                $"theme directory could not be read: {ex.Message}",
                directory,
                scope));
            return;
        }

        foreach (var file in files)
        {
            LoadFromFile(file, scope, themes, diagnostics);
        }
    }

    private static void LoadFromFile(
        string filePath,
        string scope,
        ICollection<CodingAgentTheme> themes,
        ICollection<CodingAgentThemeDiagnostic> diagnostics)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                File.ReadAllText(filePath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new CodingAgentThemeDiagnostic(
                "error",
                $"failed to load theme json: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentThemeDiagnostic(
                "warning",
                $"theme file could not be read: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }

        using (document)
        {
            var path = Path.GetFullPath(filePath);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    "theme json root must be an object",
                    path,
                    scope));
                return;
            }

            if (!document.RootElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    "theme json missing required string property 'name'",
                    path,
                    scope));
                return;
            }

            if (!document.RootElement.TryGetProperty("colors", out var colorsElement) ||
                colorsElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    "theme json missing required object property 'colors'",
                    path,
                    scope));
                return;
            }

            if (!TryReadVars(document.RootElement, path, scope, diagnostics, out var vars) ||
                !TryReadRequiredColors(colorsElement, vars, path, scope, diagnostics, out var colors))
            {
                return;
            }

            var exportColors = ReadExportColors(document.RootElement, vars, path, scope, diagnostics);
            themes.Add(new CodingAgentTheme(
                nameElement.GetString()!.Trim(),
                path,
                scope,
                colors,
                exportColors));
        }
    }

    private static bool TryReadRequiredColors(
        JsonElement colorsElement,
        IReadOnlyDictionary<string, ThemeColorValue> vars,
        string path,
        string scope,
        ICollection<CodingAgentThemeDiagnostic> diagnostics,
        out IReadOnlyDictionary<string, string> colors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in RequiredColorKeys)
        {
            if (!colorsElement.TryGetProperty(key, out var value))
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    $"theme json missing required color '{key}'",
                    path,
                    scope));
                colors = result;
                return false;
            }

            if (!TryReadColorValue(value, vars, out var color, out var error))
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    error ?? $"theme color '{key}' must be a string or 0-255 integer",
                    path,
                    scope));
                colors = result;
                return false;
            }

            result[key] = color;
        }

        colors = result;
        return true;
    }

    private static bool TryReadVars(
        JsonElement root,
        string path,
        string scope,
        ICollection<CodingAgentThemeDiagnostic> diagnostics,
        out IReadOnlyDictionary<string, ThemeColorValue> vars)
    {
        var result = new Dictionary<string, ThemeColorValue>(StringComparer.Ordinal);
        vars = result;
        if (!root.TryGetProperty("vars", out var varsElement))
        {
            return true;
        }

        if (varsElement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new CodingAgentThemeDiagnostic(
                "error",
                "theme json property 'vars' must be an object",
                path,
                scope));
            return false;
        }

        foreach (var property in varsElement.EnumerateObject())
        {
            if (!TryReadRawColorValue(property.Value, out var color))
            {
                diagnostics.Add(new CodingAgentThemeDiagnostic(
                    "error",
                    $"theme var '{property.Name}' must be a string or 0-255 integer",
                    path,
                    scope));
                return false;
            }

            result[property.Name] = color;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> ReadExportColors(
        JsonElement root,
        IReadOnlyDictionary<string, ThemeColorValue> vars,
        string path,
        string scope,
        ICollection<CodingAgentThemeDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("export", out var exportElement) ||
            exportElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ExportColorKeys)
        {
            if (!exportElement.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (TryReadColorValue(value, vars, out var color, out var error))
            {
                result[key] = color;
                continue;
            }

            diagnostics.Add(new CodingAgentThemeDiagnostic(
                "error",
                error ?? $"theme export color '{key}' must be a string or 0-255 integer",
                path,
                scope));
        }

        return result;
    }

    private static bool TryReadColorValue(
        JsonElement value,
        IReadOnlyDictionary<string, ThemeColorValue> vars,
        out string color,
        out string? error)
    {
        color = string.Empty;
        error = null;
        if (!TryReadRawColorValue(value, out var raw))
        {
            return false;
        }

        return TryResolveColorValue(raw, vars, new HashSet<string>(StringComparer.Ordinal), out color, out error);
    }

    private static bool TryReadRawColorValue(JsonElement value, out ThemeColorValue color)
    {
        color = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            color = ThemeColorValue.FromString(value.GetString() ?? string.Empty);
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number) &&
            number is >= 0 and <= 255)
        {
            color = ThemeColorValue.FromNumber(number);
            return true;
        }

        return false;
    }

    private static bool TryResolveColorValue(
        ThemeColorValue value,
        IReadOnlyDictionary<string, ThemeColorValue> vars,
        ISet<string> visited,
        out string color,
        out string? error)
    {
        color = string.Empty;
        error = null;
        if (value.IsNumber)
        {
            color = value.Number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var text = value.Text ?? string.Empty;
        if (text.Length == 0 || text.StartsWith('#'))
        {
            color = text;
            return true;
        }

        if (!visited.Add(text))
        {
            error = $"theme color variable '{text}' has a circular reference";
            return false;
        }

        if (!vars.TryGetValue(text, out var next))
        {
            error = $"theme color variable '{text}' is not defined";
            return false;
        }

        return TryResolveColorValue(next, vars, visited, out color, out error);
    }

    public static IReadOnlyList<string> GetConfiguredThemePaths()
    {
        var configured = Environment.GetEnvironmentVariable(ThemePathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        return configured
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public static string ResolvePath(string path, string cwd)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(cwd, path));
    }

    private static string GetDefaultUserThemesDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau", "themes");

    private readonly record struct ThemeColorValue(string? Text, int Number, bool IsNumber)
    {
        public static ThemeColorValue FromString(string value) => new(value, 0, false);

        public static ThemeColorValue FromNumber(int value) => new(null, value, true);
    }
}
