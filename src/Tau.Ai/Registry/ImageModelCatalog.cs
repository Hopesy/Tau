namespace Tau.Ai.Registry;

public sealed class ImageModelCatalog
{
    private const string DefaultProviderId = "openrouter";
    private const string DefaultModelId = "openrouter/auto";

    private readonly Dictionary<string, Dictionary<string, ImagesModel>> _models = new(StringComparer.OrdinalIgnoreCase);

    public ImageModelCatalog()
    {
        foreach (var (provider, models) in GeneratedBuiltInImageModels.Catalog)
        {
            if (!_models.TryGetValue(provider, out var bucket))
            {
                bucket = new Dictionary<string, ImagesModel>(StringComparer.OrdinalIgnoreCase);
                _models[provider] = bucket;
            }

            foreach (var (modelId, model) in models)
            {
                bucket[modelId] = model;
            }
        }
    }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<ImagesModel> GetModels(string provider) =>
        _models.TryGetValue(provider, out var models)
            ? [.. models.Values]
            : [];

    public ImagesModel GetModel(string provider, string modelId)
    {
        if (!_models.TryGetValue(provider, out var models) || !models.TryGetValue(modelId, out var model))
        {
            throw new KeyNotFoundException($"Image model '{provider}/{modelId}' is not registered.");
        }

        return model;
    }

    public ImagesModel? TryGetModel(string provider, string modelId)
    {
        return _models.TryGetValue(provider, out var models) && models.TryGetValue(modelId, out var model)
            ? model
            : null;
    }

    public void RegisterModel(ImagesModel model)
    {
        if (!_models.TryGetValue(model.Provider, out var models))
        {
            models = new Dictionary<string, ImagesModel>(StringComparer.OrdinalIgnoreCase);
            _models[model.Provider] = models;
        }

        models[model.Id] = model;
    }

    public static string GetDefaultProviderId() => DefaultProviderId;

    public static string GetDefaultModelId(string providerId) =>
        providerId.Equals(DefaultProviderId, StringComparison.OrdinalIgnoreCase)
            ? DefaultModelId
            : DefaultModelId;
}
