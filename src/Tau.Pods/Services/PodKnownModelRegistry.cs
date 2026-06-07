using System.Text.Json;
using Tau.Pods.Models;
using Tau.Pods.Serialization;

namespace Tau.Pods.Services;

public sealed class PodKnownModelRegistry
{
    private readonly Lazy<PodKnownModelsFile> _models;

    public PodKnownModelRegistry(string? modelsJsonPath = null)
    {
        var path = string.IsNullOrWhiteSpace(modelsJsonPath)
            ? FindDefaultModelsJsonPath()
            : modelsJsonPath;
        _models = new Lazy<PodKnownModelsFile>(() => Load(path), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsKnownModel(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        _models.Value.Models.ContainsKey(modelId.Trim());

    public IReadOnlyList<string> GetKnownModels() =>
        _models.Value.Models.Keys.Order(StringComparer.Ordinal).ToArray();

    public string GetModelName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        var normalized = modelId.Trim();
        return _models.Value.Models.TryGetValue(normalized, out var info) &&
            !string.IsNullOrWhiteSpace(info.Name)
                ? info.Name
                : normalized;
    }

    public PodKnownModelConfig? GetBestConfig(string modelId, IReadOnlyList<PodGpuInfo> gpus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(gpus);

        for (var gpuCount = gpus.Count; gpuCount >= 1; gpuCount--)
        {
            var config = GetConfig(modelId, gpus, gpuCount);
            if (config is not null)
            {
                return config;
            }
        }

        return null;
    }

    public PodKnownModelConfig? GetConfig(string modelId, IReadOnlyList<PodGpuInfo> gpus, int requestedGpuCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(gpus);

        if (requestedGpuCount <= 0 ||
            !_models.Value.Models.TryGetValue(modelId.Trim(), out var info))
        {
            return null;
        }

        var config = FindConfig(info, gpus, requestedGpuCount);
        if (config is null)
        {
            return null;
        }

        return new PodKnownModelConfig(
            modelId.Trim(),
            string.IsNullOrWhiteSpace(info.Name) ? modelId.Trim() : info.Name,
            config.GpuCount,
            config.Args.ToArray(),
            config.Env is null ? null : new Dictionary<string, string>(config.Env, StringComparer.Ordinal),
            string.IsNullOrWhiteSpace(config.Notes) ? info.Notes : config.Notes);
    }

    private static PodKnownModelConfigEntry? FindConfig(
        PodKnownModelInfo info,
        IReadOnlyList<PodGpuInfo> gpus,
        int requestedGpuCount)
    {
        var gpuType = ExtractGpuType(gpus.Count > 0 ? gpus[0].Name : string.Empty);

        foreach (var config in info.Configs)
        {
            if (config.GpuCount != requestedGpuCount)
            {
                continue;
            }

            if (config.GpuTypes is { Count: > 0 })
            {
                var matches = config.GpuTypes.Any(type =>
                    !string.IsNullOrWhiteSpace(type) &&
                    (gpuType.Contains(type.Trim(), StringComparison.OrdinalIgnoreCase) ||
                     type.Trim().Contains(gpuType, StringComparison.OrdinalIgnoreCase)));
                if (!matches)
                {
                    continue;
                }
            }

            return config;
        }

        return info.Configs.FirstOrDefault(config => config.GpuCount == requestedGpuCount);
    }

    private static string ExtractGpuType(string gpuName)
    {
        var normalized = gpuName.Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static PodKnownModelsFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PodKnownModelsFile();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, PodsJsonContext.Default.PodKnownModelsFile) ??
            new PodKnownModelsFile();
    }

    private static string FindDefaultModelsJsonPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models.json"),
            Path.Combine(AppContext.BaseDirectory, "Models", "models.json"),
            Path.Combine(Environment.CurrentDirectory, "src", "Tau.Pods", "Models", "models.json"),
            Path.Combine(Environment.CurrentDirectory, "Models", "models.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
