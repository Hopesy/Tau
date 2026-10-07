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
    bool? HideThinkingBlock = null,
    bool? ImagesAutoResize = null,
    bool? ImagesBlockImages = null,
    bool? ShowHardwareCursor = null,
    bool? FullscreenCopyOnSelect = null,
    int? EditorPaddingX = null,
    int? AutocompleteMaxVisible = null,
    string? MarkdownCodeBlockIndent = null,
    IReadOnlyList<CodingAgentPackageSource>? Packages = null)
{
    internal JsonElement? SourceDocument { get; init; }
    internal Dictionary<string, JsonElement>? TerminalAdditionalSettings { get; init; }
    internal Dictionary<string, JsonElement>? ImageAdditionalSettings { get; init; }
    internal Dictionary<string, JsonElement>? MarkdownAdditionalSettings { get; init; }
    public IReadOnlyList<string>? DefaultTools { get; init; }
    public bool? ShowCacheMissNotices { get; init; }
    public JsonElement? Compaction { get; init; }
    public JsonElement? Retry { get; init; }
    public JsonElement? BranchSummary { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? AdditionalSettings { get; init; }
}

public sealed partial class CodingAgentSettingsStore
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
        return LoadMergedSettings();
    }

    /// <summary>【CodingAgent】【设置投影】把合并后的 JSON 设置转换为类型化快照，保留原始字段用于差量保存。</summary>
    /// <param name="source">合并后的设置对象。</param><returns>独立设置快照。</returns>
    private static CodingAgentSettingsSnapshot ReadSnapshot(JsonElement source)
    {
        var document = source.Deserialize(CodingAgentSettingsJsonContext.Default.CodingAgentSettingsDocument);
        return new CodingAgentSettingsSnapshot(
                document?.DefaultProvider,
                document?.DefaultModel,
                document?.TreeFilterMode,
                CodingAgentNativeSettings.ReadRetryCount(document?.Retry, NormalizeNonNegative(document?.RetryMaxAttempts)),
                CodingAgentNativeSettings.ReadInteger(document?.Retry, "baseDelayMs", "retry") ?? NormalizeNonNegative(document?.RetryBaseDelayMilliseconds),
                document?.DefaultThinkingLevel,
                NormalizeEnabledModels(document?.EnabledModels),
                CodingAgentQueueModes.NormalizeOrNull(document?.SteeringMode)
                    ?? CodingAgentQueueModes.NormalizeOrNull(document?.QueueMode),
                CodingAgentQueueModes.NormalizeOrNull(document?.FollowUpMode),
                CodingAgentNativeSettings.ReadBoolean(document?.Compaction, "enabled", "compaction") ?? document?.AutoCompactionEnabled,
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
                document?.HideThinkingBlock,
                document?.Images?.AutoResize,
                document?.Images?.BlockImages,
                document?.ShowHardwareCursor,
                document?.FullscreenCopyOnSelect,
                NormalizeEditorPaddingX(document?.EditorPaddingX),
                NormalizeAutocompleteMaxVisible(document?.AutocompleteMaxVisible),
                document?.Markdown?.CodeBlockIndent,
                NormalizePackages(document?.Packages))
            {
                SourceDocument = source.Clone(),
                TerminalAdditionalSettings = document?.Terminal?.AdditionalSettings,
                ImageAdditionalSettings = document?.Images?.AdditionalSettings,
                MarkdownAdditionalSettings = document?.Markdown?.AdditionalSettings,
                DefaultTools = document?.DefaultTools,
                ShowCacheMissNotices = document?.ShowCacheMissNotices,
                Compaction = document?.Compaction?.Clone(), Retry = document?.Retry?.Clone(), BranchSummary = document?.BranchSummary?.Clone(),
                AdditionalSettings = document?.AdditionalSettings?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            };
    }

    public void SaveDefaultModel(Model model)
    {
        var current = Load();
        Save(current with { DefaultProvider = model.Provider, DefaultModel = model.Id });
    }

    public void Save(CodingAgentSettingsSnapshot snapshot)
    {
        SaveSettings(snapshot);
    }

    /// <summary>【CodingAgent】【设置序列化】规范化可编辑字段，同时保留尚未识别的嵌套设置。</summary>
    /// <param name="snapshot">待保存快照。</param><returns>文件序列化对象。</returns>
    private static CodingAgentSettingsDocument CreateDocument(CodingAgentSettingsSnapshot snapshot) => new()
        {
            DefaultProvider = snapshot.DefaultProvider,
            DefaultModel = snapshot.DefaultModel,
            TreeFilterMode = snapshot.TreeFilterMode,
            RetryMaxAttempts = NormalizeNonNegative(snapshot.RetryMaxAttempts),
            RetryBaseDelayMilliseconds = NormalizeNonNegative(snapshot.RetryBaseDelayMilliseconds),
            DefaultThinkingLevel = snapshot.DefaultThinkingLevel,
            EnabledModels = NormalizeEnabledModels(snapshot.EnabledModels),
            DefaultTools = snapshot.DefaultTools?.ToArray(),
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
            HideThinkingBlock = snapshot.HideThinkingBlock,
            ShowCacheMissNotices = snapshot.ShowCacheMissNotices,
            Images = CreateImageSettingsDocument(snapshot),
            ShowHardwareCursor = snapshot.ShowHardwareCursor,
            FullscreenCopyOnSelect = snapshot.FullscreenCopyOnSelect,
            EditorPaddingX = NormalizeEditorPaddingX(snapshot.EditorPaddingX),
            AutocompleteMaxVisible = NormalizeAutocompleteMaxVisible(snapshot.AutocompleteMaxVisible),
            Markdown = CreateMarkdownSettingsDocument(snapshot),
            Packages = NormalizePackages(snapshot.Packages),
            Compaction = CodingAgentNativeSettings.MergeCompaction(snapshot),
            Retry = CodingAgentNativeSettings.MergeRetry(snapshot),
            BranchSummary = snapshot.BranchSummary?.Clone(),
            AdditionalSettings = snapshot.AdditionalSettings?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow
        };

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
        snapshot.TerminalShowImages is null && snapshot.TerminalClearOnShrink is null && snapshot.TerminalAdditionalSettings is null
            ? null
            : new CodingAgentTerminalSettingsDocument
            {
                ShowImages = snapshot.TerminalShowImages,
                ClearOnShrink = snapshot.TerminalClearOnShrink,
                AdditionalSettings = snapshot.TerminalAdditionalSettings
            };

    private static CodingAgentImageSettingsDocument? CreateImageSettingsDocument(CodingAgentSettingsSnapshot snapshot) =>
        snapshot.ImagesAutoResize is null && snapshot.ImagesBlockImages is null && snapshot.ImageAdditionalSettings is null
            ? null
            : new CodingAgentImageSettingsDocument
            {
                AutoResize = snapshot.ImagesAutoResize,
                BlockImages = snapshot.ImagesBlockImages,
                AdditionalSettings = snapshot.ImageAdditionalSettings
            };

    private static CodingAgentMarkdownSettingsDocument? CreateMarkdownSettingsDocument(CodingAgentSettingsSnapshot snapshot) =>
        snapshot.MarkdownCodeBlockIndent is null && snapshot.MarkdownAdditionalSettings is null
            ? null
            : new CodingAgentMarkdownSettingsDocument { CodeBlockIndent = snapshot.MarkdownCodeBlockIndent, AdditionalSettings = snapshot.MarkdownAdditionalSettings };

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
    public JsonElement? Compaction { get; init; }
    public JsonElement? Retry { get; init; }
    public JsonElement? BranchSummary { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public string? DefaultProvider { get; init; }
    public string? DefaultModel { get; init; }
    public string? TreeFilterMode { get; init; }
    public int? RetryMaxAttempts { get; init; }
    public int? RetryBaseDelayMilliseconds { get; init; }
    public string? DefaultThinkingLevel { get; init; }
    public string[]? EnabledModels { get; init; }
    public string[]? DefaultTools { get; init; }
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
    public bool? HideThinkingBlock { get; init; }
    public bool? ShowCacheMissNotices { get; init; }
    public bool? ShowHardwareCursor { get; init; }
    public bool? FullscreenCopyOnSelect { get; init; }
    public int? EditorPaddingX { get; init; }
    public int? AutocompleteMaxVisible { get; init; }
    public CodingAgentMarkdownSettingsDocument? Markdown { get; init; }
    public CodingAgentPackageSource[]? Packages { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class CodingAgentTerminalSettingsDocument
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public bool? ShowImages { get; init; }
    public bool? ClearOnShrink { get; init; }
}

internal sealed class CodingAgentImageSettingsDocument
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public bool? AutoResize { get; init; }
    public bool? BlockImages { get; init; }
}

internal sealed class CodingAgentMarkdownSettingsDocument
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
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
