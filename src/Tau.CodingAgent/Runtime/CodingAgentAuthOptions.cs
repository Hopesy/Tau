// 作者：xxx
using System.Text;
using System.Text.RegularExpressions;
using Tau.Ai.Auth;
using Tau.Tui.Components;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证选择】将提供方和认证方式作为独立选项。</summary>
/// <param name="Provider">提供方标识。</param><param name="Name">提供方显示名。</param>
/// <param name="AuthType">oauth 或 api_key。</param><param name="Status">当前有效或已存储认证状态。</param>
/// <param name="MethodName">认证实现名称，参与搜索。</param><param name="Subscription">OAuth 是否使用订阅；未指定时按订阅展示。</param>
public sealed record CodingAgentAuthOption(string Provider, string Name, string AuthType,
    ProviderAuthStatus? Status = null, string? MethodName = null, bool? Subscription = null)
{
    /// <summary>提供方为账户登录菜单指定的可选标签。</summary>
    public string? LoginLabel { get; init; }
    /// <summary>不会与同一提供方另一认证方式冲突的选择值。</summary>
    public string SelectionKey => $"auth:{AuthType}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(Provider))}";
}

public static partial class CodingAgentAuthSelector
{
    /// <summary>【CodingAgent】【认证选择】格式化认证方式标签。</summary>
    /// <param name="authType">认证方式。</param><param name="subscription">是否使用订阅。</param><returns>可显示的类型标签。</returns>
    public static string FormatAuthType(string authType, bool? subscription = null) =>
        authType == "api_key" ? "API key" : subscription == false ? "account" : "subscription";

    /// <summary>【CodingAgent】【认证选择】显示已配置方式与来源，不访问秘密值。</summary>
    /// <param name="option">待展示选项。</param><returns>状态后缀。</returns>
    public static string FormatOptionStatus(CodingAgentAuthOption option)
    {
        if (option.Status is not { IsConfigured: true } status) return " • not configured";
        var configuredType = status.UsesOAuth ? "oauth" : "api_key";
        if (configuredType != option.AuthType) return $" • {FormatAuthType(configuredType, option.Subscription)} configured";
        var source = status.Source;
        if (string.IsNullOrEmpty(source) || source is "OAuth" or "stored credential" || source.StartsWith("auth.json", StringComparison.Ordinal))
            return " ✓ configured";
        return " ✓ " + (Regex.IsMatch(source, @"\A[A-Z][A-Z0-9_]*(?:, [A-Z][A-Z0-9_]*)*\z", RegexOptions.CultureInvariant) ? "env: " + source : source);
    }

    /// <summary>【CodingAgent】【认证选择】为每种方式生成独立选择值，搜索包含隐藏的认证实现名。</summary>
    /// <param name="state">选择状态。</param><param name="maxVisible">最多显示条目数。</param><returns>可搜索列表。</returns>
    private static TuiSelectList CreateOptionsList(CodingAgentAuthSelectorState state, int maxVisible)
    {
        // 1. 【CodingAgent】【认证选择】混合方式才显示类型标签，避免单一方式列表重复说明
        var mixed = state.Options.Select(option => option.AuthType).Distinct(StringComparer.Ordinal).Skip(1).Any();
        var items = state.Options.Select(option => new TuiSelectItem(option.SelectionKey, option.Name,
            (mixed ? $"[{FormatAuthType(option.AuthType, option.Subscription)}]" : "") + FormatOptionStatus(option))
            { SearchText = $"{option.Name} {option.Provider} {option.AuthType} {option.MethodName}" }).ToArray();
        return new TuiSelectList(items, maxVisible, layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 18, MaxPrimaryColumnWidth: 32,
            FooterHint: state.Mode == "logout" ? "Select provider to logout:" : "Select provider to configure:"));
    }
}
