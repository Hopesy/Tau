// 作者：xxx
namespace Tau.Ai.Registry;

public sealed partial class ModelCatalog
{
    private IReadOnlyList<Model> _capabilityModels = [];
    private IReadOnlyList<Model>? _runtimeCapabilityBaseline;
    private IReadOnlyList<Model> _customCapabilityModels = [];

    /// <summary>【AI】【能力目录】读取聊天、图像和分类模型，保持同一次注册事务的完整快照。</summary>
    /// <param name="provider">可选提供方。</param><returns>全部类型的模型。</returns>
    public IReadOnlyList<Model> GetAllModels(string? provider = null)
    {
        lock (_catalogGate)
            return WithVirtualModels(GetPhysicalModels(provider), provider);
    }

    /// <summary>【AI】【能力目录】读取包含非聊天能力的提供方列表。</summary>
    /// <returns>去重并排序的提供方。</returns>
    public IReadOnlyList<string> GetAllProviders() => GetAllModels().Select(model => model.Provider)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>【AI】【能力目录】按类型读取模型，相同标识的不同能力保持独立。</summary>
    /// <param name="type">chat、image 或 classifier。</param><param name="provider">可选提供方。</param><returns>匹配模型。</returns>
    public IReadOnlyList<Model> GetModelsOfType(string type, string? provider = null) =>
        GetAllModels(provider).Where(model => ModelTypes.GetModelType(model) == type).ToArray();

    /// <summary>【AI】【能力目录】按类型和标识查找模型。</summary>
    /// <param name="type">能力类型。</param><param name="provider">提供方。</param><param name="modelId">模型标识。</param><returns>模型或空值。</returns>
    public Model? GetModelOfType(string type, string provider, string modelId) => GetModelsOfType(type, provider)
        .FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>【AI】【能力目录】从内置数据和当前配置创建非聊天目录。</summary>
    /// <returns>配置后的图像和分类模型。</returns>
    private IReadOnlyList<Model> CreateCapabilityModels()
    {
        var images = new ImageModelCatalog();
        return [.. ConfigurationStore.ApplyToModels(images.GetProviders().SelectMany(images.GetModels), ModelTypes.Image),
            .. ConfigurationStore.ApplyToModels(GeneratedBuiltInClassifierModels.Models, ModelTypes.Classifier)];
    }

    /// <summary>【AI】【能力注册】为一种能力应用完整扩展快照，先构造候选目录再提交。</summary>
    /// <param name="baseline">原始非聊天目录。</param><param name="providers">已规范化注册。</param><param name="type">能力类型。</param>
    /// <returns>该类型的新目录。</returns>
    private static IReadOnlyList<Model> ApplyCapabilityProviders(IReadOnlyList<Model> baseline,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> providers, string type)
    {
        var next = baseline.Where(model => ModelTypes.GetModelType(model) == type).GroupBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, definition) in providers)
        {
            if (definition.TryGetProperty("models", out _)) next.Remove(provider);
            ModelConfigurationStore.ApplyProvider(provider, definition, next, type);
            if (next.TryGetValue(provider, out var models))
                foreach (var (id, model) in models.ToArray()) models[id] = model with { Headers = null };
        }
        return next.Values.SelectMany(bucket => bucket.Values).ToArray();
    }
}
