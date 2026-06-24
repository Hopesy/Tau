using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.AgentCore.Runtime;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSettingsSnapshot(
    string? DefaultProvider,
    string? DefaultModel,
    string? TreeFilterMode = null,
    int? RetryMaxAttempts = null,
    int? RetryBaseDelayMilliseconds = null,
    string? DefaultThinkingLevel = null,
    IReadOnlyList<string>? EnabledModels = null,
    string? SteeringMode = null,
    string? FollowUpMode = null,
    bool? AutoCompactionEnabled = null,
    string? Theme = null,
    IReadOnlyList<string>? TreeCollapsedEntryIds = null,
    string? ShellPath = null,
    string? ShellCommandPrefix = null,
    IReadOnlyList<string>? NpmCommand = null,
    bool? QuietStartup = null,
    bool? CollapseChangelog = null,
    bool? EnableInstallTelemetry = null,
    string? LastChangelogVersion = null,
    bool? TerminalShowImages = null,
    bool? TerminalClearOnShrink = null,
    bool? ImagesAutoResize = null,
    bool? ImagesBlockImages = null,
    bool? ShowHardwareCursor = null,
    int? EditorPaddingX = null,
    int? AutocompleteMaxVisible = null,
    string? MarkdownCodeBlockIndent = null,
    IReadOnlyList<CodingAgentPackageSource>? Packages = null);

public sealed class CodingAgentSettingsStore
{
    private readonly string _path;

    public CodingAgentSettingsStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public static string GetDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SETTINGS_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, ".tau", "coding-agent-settings.json");
    }

    public CodingAgentSettingsSnapshot Load()
    {
        if (!File.Exists(_path))
        {
            return new CodingAgentSettingsSnapshot(null, null);
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize(stream, CodingAgentSettingsJsonContext.Default.CodingAgentSettingsDocument);
            return new CodingAgentSettingsSnapshot(
                document?.DefaultProvider,
                document?.DefaultModel,
                document?.TreeFilterMode,
                NormalizeNonNegative(document?.RetryMaxAttempts),
                NormalizeNonNegative(document?.RetryBaseDelayMilliseconds),
                document?.DefaultThinkingLevel,
                NormalizeEnabledModels(document?.EnabledModels),
                CodingAgentQueueModes.NormalizeOrNull(document?.SteeringMode)
                    ?? CodingAgentQueueModes.NormalizeOrNull(document?.QueueMode),
                CodingAgentQueueModes.NormalizeOrNull(document?.FollowUpMode),
                document?.AutoCompactionEnabled,
                NormalizeTheme(document?.Theme),
                NormalizeStringList(document?.TreeCollapsedEntryIds),
                NormalizeOptionalString(document?.ShellPath),
                NormalizeOptionalString(document?.ShellCommandPrefix),
                NormalizeStringListPreserveOrder(document?.NpmCommand),
                document?.QuietStartup,
                document?.CollapseChangelog,
                document?.EnableInstallTelemetry,
                NormalizeOptionalString(document?.LastChangelogVersion),
                document?.Terminal?.ShowImages,
                document?.Terminal?.ClearOnShrink,
                document?.Images?.AutoResize,
                document?.Images?.BlockImages,
                document?.ShowHardwareCursor,
                NormalizeEditorPaddingX(document?.EditorPaddingX),
                NormalizeAutocompleteMaxVisible(document?.AutocompleteMaxVisible),
                document?.Markdown?.CodeBlockIndent,
                NormalizePackages(document?.Packages));
        }
        catch (JsonException)
        {
            return new CodingAgentSettingsSnapshot(null, null);
        }
        catch (IOException)
        {
            return new CodingAgentSettingsSnapshot(null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new CodingAgentSettingsSnapshot(null, null);
        }
    }

    public void SaveDefaultModel(Model model)
    {
        var current = Load();
        Save(current with { DefaultProvider = model.Provider, DefaultModel = model.Id });
    }

    public void Save(CodingAgentSettingsSnapshot snapshot)
    {
        var document = new CodingAgentSettingsDocument
        {
            DefaultProvider = snapshot.DefaultProvider,
            DefaultModel = snapshot.DefaultModel,
            TreeFilterMode = snapshot.TreeFilterMode,
            RetryMaxAttempts = NormalizeNonNegative(snapshot.RetryMaxAttempts),
            RetryBaseDelayMilliseconds = NormalizeNonNegative(snapshot.RetryBaseDelayMilliseconds),
            DefaultThinkingLevel = snapshot.DefaultThinkingLevel,
            EnabledModels = NormalizeEnabledModels(snapshot.EnabledModels),
            SteeringMode = CodingAgentQueueModes.NormalizeOrNull(snapshot.SteeringMode),
            FollowUpMode = CodingAgentQueueModes.NormalizeOrNull(snapshot.FollowUpMode),
            AutoCompactionEnabled = snapshot.AutoCompactionEnabled,
            Theme = NormalizeTheme(snapshot.Theme),
            TreeCollapsedEntryIds = NormalizeStringList(snapshot.TreeCollapsedEntryIds),
            ShellPath = NormalizeOptionalString(snapshot.ShellPath),
            ShellCommandPrefix = NormalizeOptionalString(snapshot.ShellCommandPrefix),
            NpmCommand = NormalizeStringListPreserveOrder(snapshot.NpmCommand),
            QuietStartup = snapshot.QuietStartup,
            CollapseChangelog = snapshot.CollapseChangelog,
            EnableInstallTelemetry = snapshot.EnableInstallTelemetry,
            LastChangelogVersion = NormalizeOptionalString(snapshot.LastChangelogVersion),
            Terminal = CreateTerminalSettingsDocument(snapshot),
            Images = CreateImageSettingsDocument(snapshot),
            ShowHardwareCursor = snapshot.ShowHardwareCursor,
            EditorPaddingX = NormalizeEditorPaddingX(snapshot.EditorPaddingX),
            AutocompleteMaxVisible = NormalizeAutocompleteMaxVisible(snapshot.AutocompleteMaxVisible),
            Markdown = CreateMarkdownSettingsDocument(snapshot),
            Packages = NormalizePackages(snapshot.Packages),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, document, CodingAgentSettingsJsonContext.Default.CodingAgentSettingsDocument);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        File.Move(tempPath, _path);
    }

    private static int? NormalizeNonNegative(int? value) =>
        value is null ? null : Math.Max(0, value.Value);

    private static string[]? NormalizeEnabledModels(IEnumerable<string>? enabledModels)
    {
        return NormalizeStringList(enabledModels);
    }

    private static string[]? NormalizeStringList(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Select(static value => value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string[]? NormalizeStringListPreserveOrder(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeTheme(string? theme) =>
        string.IsNullOrWhiteSpace(theme) ? null : theme.Trim();

    private static string? NormalizeOptionalString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizeEditorPaddingX(int? value) =>
        value is null ? null : Math.Clamp(value.Value, 0, 3);

    private static int? NormalizeAutocompleteMaxVisible(int? value) =>
        value is null ? null : Math.Clamp(value.Value, 3, 20);

    private static CodingAgentTerminalSettingsDocument? CreateTerminalSettingsDocument(CodingAgentSettingsSnapshot snapshot) =>
        snapshot.TerminalShowImages is null && snapshot.TerminalClearOnShrink is null
            ? null
            : new CodingAgentTerminalSettingsDocument
            {
                ShowImages = snapshot.TerminalShowImages,
                ClearOnShrink = snapshot.TerminalClearOnShrink
            };

    private static CodingAgentImageSettingsDocument? CreateImageSettingsDocument(CodingAgentSettingsSnapshot snapshot) =>
        snapshot.ImagesAutoResize is null && snapshot.ImagesBlockImages is null
            ? null
            : new CodingAgentImageSettingsDocument
            {
                AutoResize = snapshot.ImagesAutoResize,
                BlockImages = snapshot.ImagesBlockImages
            };

    private static CodingAgentMarkdownSettingsDocument? CreateMarkdownSettingsDocument(CodingAgentSettingsSnapshot snapshot) =>
        snapshot.MarkdownCodeBlockIndent is null
            ? null
            : new CodingAgentMarkdownSettingsDocument { CodeBlockIndent = snapshot.MarkdownCodeBlockIndent };

    private static CodingAgentPackageSource[]? NormalizePackages(IReadOnlyList<CodingAgentPackageSource>? packages)
    {
        if (packages is null || packages.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<CodingAgentPackageSource>();
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Source))
            {
                continue;
            }

            var source = package.Source.Trim();
            if (!seen.Add(source))
            {
                continue;
            }

            normalized.Add(package with
            {
                Source = source,
                Extensions = NormalizePackageFilterEntries(package.Extensions),
                Skills = NormalizePackageFilterEntries(package.Skills),
                Prompts = NormalizePackageFilterEntries(package.Prompts),
                Themes = NormalizePackageFilterEntries(package.Themes)
            });
        }

        return normalized.Count == 0 ? null : normalized.ToArray();
    }

    private static string[]? NormalizePackageFilterEntries(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
    }
}

internal sealed class CodingAgentSettingsDocument
{
    public string? DefaultProvider { get; init; }
    public string? DefaultModel { get; init; }
    public string? TreeFilterMode { get; init; }
    public int? RetryMaxAttempts { get; init; }
    public int? RetryBaseDelayMilliseconds { get; init; }
    public string? DefaultThinkingLevel { get; init; }
    public string[]? EnabledModels { get; init; }
    public string? SteeringMode { get; init; }
    public string? FollowUpMode { get; init; }
    public string? QueueMode { get; init; }
    public bool? AutoCompactionEnabled { get; init; }
    public string? Theme { get; init; }
    public string[]? TreeCollapsedEntryIds { get; init; }
    public string? ShellPath { get; init; }
    public string? ShellCommandPrefix { get; init; }
    public string[]? NpmCommand { get; init; }
    public bool? QuietStartup { get; init; }
    public bool? CollapseChangelog { get; init; }
    public bool? EnableInstallTelemetry { get; init; }
    public string? LastChangelogVersion { get; init; }
    public CodingAgentTerminalSettingsDocument? Terminal { get; init; }
    public CodingAgentImageSettingsDocument? Images { get; init; }
    public bool? ShowHardwareCursor { get; init; }
    public int? EditorPaddingX { get; init; }
    public int? AutocompleteMaxVisible { get; init; }
    public CodingAgentMarkdownSettingsDocument? Markdown { get; init; }
    public CodingAgentPackageSource[]? Packages { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class CodingAgentTerminalSettingsDocument
{
    public bool? ShowImages { get; init; }
    public bool? ClearOnShrink { get; init; }
}

internal sealed class CodingAgentImageSettingsDocument
{
    public bool? AutoResize { get; init; }
    public bool? BlockImages { get; init; }
}

internal sealed class CodingAgentMarkdownSettingsDocument
{
    public string? CodeBlockIndent { get; init; }
}

internal static class CodingAgentQueueModes
{
    public const string All = "all";
    public const string OneAtATime = "one-at-a-time";

    public static string NormalizeOrDefault(string? value) =>
        NormalizeOrNull(value) ?? OneAtATime;

    public static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" => All,
            "one-at-a-time" or "oneatatime" or "one_at_a_time" => OneAtATime,
            _ => null
        };
    }

    public static bool TryNormalize(string? value, out string mode)
    {
        mode = NormalizeOrNull(value) ?? string.Empty;
        return mode.Length > 0;
    }

    public static AgentQueueMode ToAgentQueueMode(string? value) =>
        NormalizeOrDefault(value) == All ? AgentQueueMode.All : AgentQueueMode.OneAtATime;

    public static string FromAgentQueueMode(AgentQueueMode mode) =>
        mode == AgentQueueMode.All ? All : OneAtATime;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentSettingsDocument))]
[JsonSerializable(typeof(CodingAgentTerminalSettingsDocument))]
[JsonSerializable(typeof(CodingAgentImageSettingsDocument))]
[JsonSerializable(typeof(CodingAgentMarkdownSettingsDocument))]
[JsonSerializable(typeof(CodingAgentPackageSource))]
[JsonSerializable(typeof(CodingAgentPackageSource[]))]
internal sealed partial class CodingAgentSettingsJsonContext : JsonSerializerContext;
