// 作者：xxx
namespace Tau.Ai.Registry;

public sealed partial class ModelCatalog
{
    private IReadOnlyList<Model> _virtualModels = [];

    /// <summary>【AI】【虚拟目录】以独立覆盖层发布虚拟模型，保留物理目录供刷新和注销恢复。</summary>
    /// <param name="models">已经由路由注册层验证的虚拟模型完整快照。</param>
    public void SetVirtualModels(IReadOnlyList<Model> models)
    {
        if (models.Any(model => model.Api != "pi-virtual" || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Provider)))
            throw new ArgumentException("Virtual models require a provider, id and pi-virtual API.", nameof(models));
        lock (_catalogGate) _virtualModels = models.ToArray();
    }

    /// <summary>【AI】【物理目录】读取没有叠加虚拟模型的全部能力。</summary>
    /// <param name="provider">可选提供方。</param><returns>原始物理模型快照。</returns>
    public IReadOnlyList<Model> GetPhysicalModels(string? provider = null)
    {
        lock (_catalogGate)
            return _models.Values.SelectMany(bucket => bucket.Values).Where(model => !IsRetiredModel(model)).Concat(_capabilityModels)
                .Where(model => provider is null || model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                .Select(model => ModelTypes.GetModelType(model) == ModelTypes.Chat ? _authResolver.ResolveModel(model) : model).ToArray();
    }

    /// <summary>【AI】【虚拟目录】将虚拟模型加到目录末尾，遮盖刷新后产生的同名物理聊天模型。</summary>
    /// <param name="physical">物理目录。</param><param name="provider">可选提供方。</param><returns>完整模型快照。</returns>
    private IReadOnlyList<Model> WithVirtualModels(IReadOnlyList<Model> physical, string? provider)
    {
        var virtuals = _virtualModels.Where(model => provider is null || model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray();
        return [.. physical.Where(model => ModelTypes.GetModelType(model) != ModelTypes.Chat ||
            !virtuals.Any(overlay => overlay.Provider.Equals(model.Provider, StringComparison.OrdinalIgnoreCase) && overlay.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase))), .. virtuals];
    }
}
