// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【云登录】专用登录输入按类型保存，重开认证存储后仍能恢复环境字段。</summary>
    /// <param name="provider">云提供方。</param><param name="method">登录方式，空值表示不需要方式选择。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("amazon-bedrock", "bearer-token")]
    [InlineData("amazon-bedrock", "aws-profile")]
    [InlineData("amazon-bedrock", "credential-chain")]
    [InlineData("google-vertex", "api-key")]
    [InlineData("google-vertex", "adc")]
    [InlineData("google-vertex", "service-account")]
    [InlineData("cloudflare-ai-gateway", "")]
    [InlineData("cloudflare-workers-ai", "")]
    public async Task CloudLogin_PreservesProviderSpecificCredentialFields(string provider, string method)
    {
        using var fixture = new Fixture("cloud-login", "models");
        var credentialsPath = Path.Combine(fixture.Root, "service-account.json");
        File.WriteAllText(credentialsPath, "{}");
        var inputs = method switch
        {
            "bearer-token" or "api-key" => new[] { "cloud-secret" },
            "aws-profile" => ["fixture-profile"],
            "credential-chain" => [""],
            "adc" => ["fixture-project", "fixture-region"],
            "service-account" => ["fixture-project", "fixture-region", credentialsPath],
            _ => provider == "cloudflare-ai-gateway" ? ["cloud-secret", "fixture-account", "fixture-gateway"] : ["cloud-secret", "fixture-account"]
        };
        var callbacks = new CloudLoginCallbacks(method, inputs);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => callbacks);
        var result = await router.TryHandleAsync("/login " + provider);
        Assert.False(result!.IsError, result.Message);
        Assert.Contains("authenticated successfully", result.Message);
        Assert.Equal(inputs.Length, callbacks.Inputs.Count);
        var hasKey = method is "bearer-token" or "api-key" || provider.StartsWith("cloudflare", StringComparison.Ordinal);
        Assert.Equal(hasKey ? 1 : 0, callbacks.Inputs.Count(input => input.Type == "secret"));
        if (method == "credential-chain") Assert.True(Assert.Single(callbacks.Inputs).AllowEmpty);
        if (method is "adc" or "service-account" or "aws-profile" or "credential-chain") Assert.Contains(callbacks.Notifications, text => text.Contains("https://", StringComparison.Ordinal));
        var store = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]);
        var saved = store.LoadEntries()[provider];
        Assert.Equal(hasKey ? "cloud-secret" : null, saved.ApiKey);
        var resolver = fixture.Catalog().AuthResolver;
        var auth = resolver.ResolveRequestAuth(provider);
        if (method == "aws-profile")
        {
            Assert.Equal("fixture-profile", saved.Env!["AWS_PROFILE"]);
            Assert.Equal("fixture-profile", auth.Env!["AWS_PROFILE"]);
            Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, auth.ApiKey);
            Assert.True(resolver.GetStatus(provider).IsConfigured);
        }
        if (method is "adc" or "service-account")
        {
            Assert.Equal("fixture-project", auth.Env!["GOOGLE_CLOUD_PROJECT"]);
            Assert.Equal("fixture-region", auth.Env["GOOGLE_CLOUD_LOCATION"]);
            if (method == "service-account")
            {
                Assert.Equal(credentialsPath, auth.Env["GOOGLE_APPLICATION_CREDENTIALS"]);
                Assert.Equal(EnvironmentApiKeyResolver.AuthenticatedMarker, auth.ApiKey);
                Assert.True(resolver.GetStatus(provider).IsConfigured);
            }
        }
        if (provider.StartsWith("cloudflare", StringComparison.Ordinal))
        {
            Assert.Equal("fixture-account", auth.Env!["CLOUDFLARE_ACCOUNT_ID"]);
            if (provider == "cloudflare-ai-gateway") Assert.Equal("fixture-gateway", auth.Env["CLOUDFLARE_GATEWAY_ID"]);
        }
        Assert.Contains(session.Runner.ListStoredCredentials(), entry => entry.ProviderId == provider && entry.Type == "api_key");
    }

    /// <summary>【CodingAgent】【云登录边界】未知选择与中途取消都不会保存部分凭据。</summary>
    /// <param name="provider">云提供方。</param><param name="method">方法标识。</param><param name="cancel">是否取消第一个实际输入。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("amazon-bedrock", "invalid", false)]
    [InlineData("google-vertex", "invalid", false)]
    [InlineData("amazon-bedrock", "aws-profile", true)]
    [InlineData("google-vertex", "service-account", true)]
    [InlineData("cloudflare-ai-gateway", "", true)]
    public async Task CloudLogin_InvalidSelectionAndCancellationDoNotSave(string provider, string method, bool cancel)
    {
        using var fixture = new Fixture("cloud-cancel", "models");
        using var cancellation = new CancellationTokenSource();
        var callbacks = new CloudLoginCallbacks(method, ["first", "second", "third"]) { AfterInput = cancel ? cancellation.Cancel : null };
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => callbacks);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TryHandleAsync("/login " + provider, cancellation.Token));
        else Assert.True((await router.TryHandleAsync("/login " + provider))!.IsError);
        Assert.DoesNotContain(session.Runner.ListStoredCredentials(), entry => entry.ProviderId == provider);
    }

    /// <summary>【CodingAgent】【云登录夹具】保存输入类型、选择项和通知，按顺序提供模拟值。</summary>
    /// <param name="method">方式选择结果。</param><param name="values">普通或秘密输入序列。</param>
    private sealed class CloudLoginCallbacks(string method, string[] values) : IOAuthLoginCallbacks
    {
        public List<(string Type, bool AllowEmpty)> Inputs { get; } = [];
        public List<string> Notifications { get; } = [];
        public Action? AfterInput { get; init; }
        /// <summary>记录普通输入及是否允许空值。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">是否允许空值。</param><returns>模拟输入。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => Read("text", allowEmpty);
        /// <summary>记录秘密输入，不向通知输出值。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="token">取消信号。</param><returns>模拟秘密值。</returns>
        public Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Read("secret", false); }
        /// <summary>验证三种候选方式并返回指定方法。</summary><param name="message">提示。</param><param name="options">候选方式。</param><returns>选中的标识。</returns>
        public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options)
        { Assert.Equal(3, options.Count); return Task.FromResult<string?>(method); }
        /// <summary>读取下一项输入并可模拟取消。</summary><param name="type">输入类型。</param><param name="allowEmpty">空值标记。</param><returns>模拟值。</returns>
        private Task<string> Read(string type, bool allowEmpty)
        { var value = values[Inputs.Count]; Inputs.Add((type, allowEmpty)); AfterInput?.Invoke(); return Task.FromResult(value); }
        /// <summary>记录说明及文档链接。</summary><param name="message">通知。</param>
        public void OnProgress(string message) => Notifications.Add(message);
        /// <summary>此夹具禁止 OAuth 授权。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Unexpected OAuth request.");
        /// <summary>云凭据登录无需后台授权码。</summary><returns>空值。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
