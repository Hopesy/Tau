using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Agent.Runtime;
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
    string? Theme = null);

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
                NormalizeTheme(document?.Theme));
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
        if (enabledModels is null)
        {
            return null;
        }

        var normalized = enabledModels
            .Select(static model => model.Trim())
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeTheme(string? theme) =>
        string.IsNullOrWhiteSpace(theme) ? null : theme.Trim();
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
    public DateTimeOffset UpdatedAt { get; init; }
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
internal sealed partial class CodingAgentSettingsJsonContext : JsonSerializerContext;
