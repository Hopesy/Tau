// 作者：xxx
using Tau.Ai.Auth;

namespace Tau.Ai.Providers;

public static partial class BuiltInProviders
{
    /// <summary>【AI】【内置登录】为内置提供方暴露标准密钥或专用云凭据定义。</summary>
    /// <param name="providerId">提供方标识。</param><param name="authResolver">当前会话认证解析器。</param>
    /// <returns>密钥或云凭据定义；未知或仅 OAuth 的提供方返回空值。</returns>
    public static ApiKeyAuthDefinition? GetApiKeyAuth(string providerId, ProviderAuthResolver authResolver)
    {
        if (BuiltInProviderNames.TryGet(providerId) is null || providerId == "openai-codex") return null;
        return CreateProviderAuth(providerId, authResolver)?.ApiKey;
    }

    /// <summary>【AI】【密钥登录】通过秘密输入获取 API key，并在输入前后检查整个登录的取消状态。</summary>
    /// <param name="interaction">宿主登录交互。</param><param name="displayName">密钥方式名称。</param><returns>尚未保存的凭据。</returns>
    private static async Task<ApiKeyCredential> LoginWithApiKeyAsync(AuthInteraction interaction, string displayName)
    {
        interaction.CancellationToken.ThrowIfCancellationRequested();
        var key = await interaction.PromptAsync(new ProviderAuthPrompt("secret", $"Enter {displayName}",
            Signal: interaction.CancellationToken)).ConfigureAwait(false);
        interaction.CancellationToken.ThrowIfCancellationRequested();
        return new ApiKeyCredential(key);
    }
}
