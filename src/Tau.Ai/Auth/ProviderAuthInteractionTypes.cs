// 作者：xxx
namespace Tau.Ai.Auth;

/// <summary>【AI】【登录交互】类型化输入，单次信号可独立于整个登录取消。</summary>
public sealed record ProviderAuthPrompt(string Type, string Message, string? Placeholder = null,
    IReadOnlyList<ProviderAuthSelectOption>? Options = null, CancellationToken Signal = default)
{
    /// <summary>普通文本提示是否接受空输入，例如等待用户按 Enter 确认外部配置。</summary>
    public bool AllowEmpty { get; init; }
}

/// <summary>【AI】【登录交互】选择项及可选说明。</summary>
public sealed record ProviderAuthSelectOption(string Id, string Label, string? Description = null);

/// <summary>【AI】【登录说明链接】保留链接地址和可选显示标签。</summary>
/// <param name="Url">链接地址。</param><param name="Label">可选标签。</param>
public sealed record ProviderAuthInfoLink(string Url, string? Label = null);

/// <summary>【AI】【登录交互】类型化通知的说明、授权链接和设备码字段。</summary>
public sealed record ProviderAuthNotification(string Type, string? Message = null, string? Url = null, string? Instructions = null,
    string? UserCode = null, string? VerificationUri = null, double? IntervalSeconds = null, double? ExpiresInSeconds = null,
    IReadOnlyList<ProviderAuthInfoLink>? Links = null)
{
    /// <summary>【AI】【兼容通知】为文本宿主保留说明、设备码及每个链接的标签与地址。</summary><returns>多行可显示文本。</returns>
    public string ToDisplayText() => string.Join("\n", new[] { Message, Instructions, Url, UserCode, VerificationUri }
        .Where(value => !string.IsNullOrEmpty(value)).Concat((Links ?? []).Select(link => string.IsNullOrEmpty(link.Label) ? link.Url : link.Label + ": " + link.Url)));
}
