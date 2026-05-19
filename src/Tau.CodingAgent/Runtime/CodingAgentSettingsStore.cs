using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSettingsSnapshot(
    string? DefaultProvider,
    string? DefaultModel,
    string? TreeFilterMode = null,
    int? RetryMaxAttempts = null,
    int? RetryBaseDelayMilliseconds = null);

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
                NormalizeNonNegative(document?.RetryBaseDelayMilliseconds));
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
}

internal sealed class CodingAgentSettingsDocument
{
    public string? DefaultProvider { get; init; }
    public string? DefaultModel { get; init; }
    public string? TreeFilterMode { get; init; }
    public int? RetryMaxAttempts { get; init; }
    public int? RetryBaseDelayMilliseconds { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentSettingsDocument))]
internal sealed partial class CodingAgentSettingsJsonContext : JsonSerializerContext;
