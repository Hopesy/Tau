namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【提供方名称】复用 Ai 模块的主线提供方名称，保留现有界面查询接口。
/// 认证选择器和 provider 选择器用它渲染列表标签。
/// </summary>
public static class CodingAgentProviderDisplayNames
{
    /// <summary>
    /// 查询指定 provider id 的内置显示名。
    /// </summary>
    /// <param name="providerId">需要查找显示名的 provider id。</param>
    /// <returns>存在内置显示名时返回显示名；不存在或参数为空时返回 <see langword="null"/>。</returns>
    public static string? TryGetBuiltIn(string providerId) =>
        Tau.Ai.Providers.BuiltInProviderNames.TryGet(providerId);

    /// <summary>
    /// 解析 provider 的最终显示名，未命中内置映射时回退到 provider id 本身。
    /// </summary>
    /// <param name="providerId">需要解析显示名的 provider id。</param>
    /// <returns>用于界面展示的 provider 显示名。</returns>
    public static string Resolve(string providerId) =>
        TryGetBuiltIn(providerId) ?? providerId;

    /// <summary>
    /// 判断 provider id 是否存在内置显示名映射。
    /// </summary>
    /// <param name="providerId">需要检查的 provider id。</param>
    /// <returns>命中内置显示名表时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool IsBuiltInProvider(string providerId) =>
        TryGetBuiltIn(providerId) is not null;
}
