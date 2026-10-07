// 作者：xxx
using Tau.Ai.Auth;

namespace Tau.Ai.Providers;

public static partial class BuiltInProviders
{
    /// <summary>【AI】【Bedrock 登录】选择令牌、AWS 配置文件或已有凭据链，并返回需要保存的字段。</summary>
    /// <param name="interaction">宿主交互。</param><returns>待保存的密钥或环境凭据。</returns>
    private static async Task<ApiKeyCredential> LoginBedrockAsync(AuthInteraction interaction)
    {
        // 1. 【AI】【Bedrock 登录】明确认证方式，避免把配置文件名当作令牌保存
        var method = await PromptCloudLoginAsync(interaction, new("select", "Select Amazon Bedrock authentication method:", Options:
            [new("bearer-token", "Bearer token"), new("aws-profile", "AWS profile"), new("credential-chain", "Existing AWS credential chain")])).ConfigureAwait(false);
        if (method == "bearer-token") return new(await PromptCloudLoginAsync(interaction, new("secret", "Enter Amazon Bedrock bearer token")).ConfigureAwait(false));
        interaction.Notify(new ProviderAuthNotification("info", "Amazon Bedrock supports AWS profiles, IAM credentials, and role-based credentials.",
            Links: [new("https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html")]));
        if (method == "aws-profile") return new(Env: new Dictionary<string, string>
            { ["AWS_PROFILE"] = await PromptCloudLoginAsync(interaction, new("text", "Enter AWS profile name")).ConfigureAwait(false) });
        if (method != "credential-chain") throw new InvalidOperationException($"Unknown Amazon Bedrock auth method: {method}");
        await PromptCloudLoginAsync(interaction, new("text", "Configure AWS credentials, then press Enter to continue") { AllowEmpty = true }).ConfigureAwait(false);
        return new();
    }

    /// <summary>【AI】【Vertex 登录】选择密钥、ADC 或服务账号，并保存项目、区域及可选凭据路径。</summary>
    /// <param name="interaction">宿主交互。</param><returns>待保存的 Vertex 凭据。</returns>
    private static async Task<ApiKeyCredential> LoginVertexAsync(AuthInteraction interaction)
    {
        // 1. 【AI】【Vertex 登录】密钥与云身份使用不同输入字段
        var method = await PromptCloudLoginAsync(interaction, new("select", "Select Google Vertex AI authentication method:", Options:
            [new("api-key", "Google Cloud API key"), new("adc", "Application Default Credentials"), new("service-account", "Service account credentials file")])).ConfigureAwait(false);
        if (method == "api-key") return new(await PromptCloudLoginAsync(interaction, new("secret", "Enter Google Cloud API key")).ConfigureAwait(false));
        if (method is not ("adc" or "service-account")) throw new InvalidOperationException($"Unknown Google Vertex AI auth method: {method}");
        interaction.Notify(new ProviderAuthNotification("info", method == "adc"
            ? "Run `gcloud auth application-default login`, then provide the project and location."
            : "Provide a service account credentials file, project, and location.", Links: [new("https://cloud.google.com/docs/authentication/provide-credentials-adc")]));
        // 2. 【AI】【Vertex 登录】不读取凭据文件内容，只保存提供方所需环境字段
        var environment = new Dictionary<string, string>
        {
            ["GOOGLE_CLOUD_PROJECT"] = await PromptCloudLoginAsync(interaction, new("text", "Enter Google Cloud project ID")).ConfigureAwait(false),
            ["GOOGLE_CLOUD_LOCATION"] = await PromptCloudLoginAsync(interaction, new("text", "Enter Google Cloud location")).ConfigureAwait(false)
        };
        if (method == "service-account")
        {
            var path = await PromptCloudLoginAsync(interaction, new("text", "Enter service account credentials file path")).ConfigureAwait(false);
            if (path.Length > 0) environment["GOOGLE_APPLICATION_CREDENTIALS"] = path;
        }
        return new(Env: environment);
    }

    /// <summary>【AI】【Cloudflare 登录】保存 Gateway 密钥以及构造请求地址所需账户和网关标识。</summary>
    /// <param name="interaction">宿主交互。</param><returns>待保存的 Gateway 凭据。</returns>
    private static async Task<ApiKeyCredential> LoginCloudflareGatewayAsync(AuthInteraction interaction)
    {
        var key = await PromptCloudLoginAsync(interaction, new("secret", "Enter Cloudflare API key")).ConfigureAwait(false);
        var account = await PromptCloudLoginAsync(interaction, new("text", "Enter Cloudflare account ID")).ConfigureAwait(false);
        var gateway = await PromptCloudLoginAsync(interaction, new("text", "Enter Cloudflare AI Gateway ID")).ConfigureAwait(false);
        return new(key, new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = account, ["CLOUDFLARE_GATEWAY_ID"] = gateway });
    }

    /// <summary>【AI】【云登录输入】为每一步输入附加整体取消信号，并拒绝取消后的迟到结果。</summary>
    /// <param name="interaction">宿主交互。</param><param name="prompt">类型化提示。</param><returns>有效输入。</returns>
    private static async Task<string> PromptCloudLoginAsync(AuthInteraction interaction, ProviderAuthPrompt prompt)
    {
        interaction.CancellationToken.ThrowIfCancellationRequested();
        var value = await interaction.PromptAsync(prompt with { Signal = interaction.CancellationToken }).ConfigureAwait(false);
        interaction.CancellationToken.ThrowIfCancellationRequested();
        return value;
    }
}
