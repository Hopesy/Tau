// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【认证命令测试】验证状态退出码、秘密输出开关、OAuth 时限及提供方筛选。</summary>
public sealed class CodingAgentAuthCommandsTests
{
    /// <summary>【CodingAgent】【检查状态】配置成功、缺凭据、未知提供方与损坏状态有稳定的 JSON 和退出码。</summary>
    /// <param name="scenario">状态场景。</param><param name="status">预期状态。</param><param name="reason">失败原因。</param><param name="code">退出码。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("ready", "ready", null, 0)]
    [InlineData("missing", "not_ready", "credentials_not_configured", 1)]
    [InlineData("unknown", "not_ready", "provider_not_found", 1)]
    [InlineData("configuration", "invalid", "invalid_state", 2)]
    [InlineData("failure", "invalid", "invalid_state", 2)]
    public async Task Check_ReportsStatusWithoutSecrets(string scenario, string status, string? reason, int code)
    {
        var store = new InMemoryProviderCredentialStore();
        var auth = new ProviderAuthDefinition(apiKey: new("Key", _ => scenario switch
        {
            "failure" => throw new InvalidOperationException("fixture-secret-in-error"),
            "missing" => Task.FromResult<ProviderAuthResult?>(null),
            _ => Task.FromResult<ProviderAuthResult?>(new("fixture-secret"))
        }));
        var models = new Models([Provider("test", auth)], credentialStore: store);
        var result = await Run(["auth", "check", "--provider", scenario == "unknown" ? "unknown" : "test", "--json"], new(models, store, scenario == "configuration" ? "bad config" : null));
        Assert.Equal(code, result.Code); Assert.Empty(result.Error); Assert.DoesNotContain("fixture-secret", result.Output);
        using var document = JsonDocument.Parse(result.Output); Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(reason, document.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null);
        Assert.False(document.RootElement.TryGetProperty("credentials", out _));
    }

    /// <summary>【CodingAgent】【OAuth 检查】默认刷新过期凭据；no-refresh 直接输出旧 access 且不调用刷新与派生。</summary>
    /// <param name="noRefresh">禁止刷新。</param><param name="json">JSON 输出。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckCredentials_RespectsNoRefresh(bool noRefresh, bool json)
    {
        var store = new InMemoryProviderCredentialStore(); await SaveOAuth(store, "test", -1);
        var refreshes = 0; var derives = 0;
        var oauth = OAuth((credential, _) => { refreshes++; return Task.FromResult(credential with { Access = "renewed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) }); },
            credential => { derives++; return Task.FromResult(new ProviderAuthResult(credential.Access)); });
        var args = new List<string> { "auth", "check", "--provider", "test", "--credentials" }; if (noRefresh) args.Add("--no-refresh"); if (json) args.Add("--json");
        var result = await Run(args, new(new Models([Provider("test", new(oauth: oauth))], credentialStore: store), store));
        Assert.Equal(0, result.Code); var expected = noRefresh ? "old-access" : "renewed";
        if (json) { using var document = JsonDocument.Parse(result.Output); Assert.Equal(expected, document.RootElement.GetProperty("credentials").GetString()); Assert.Equal("oauth", document.RootElement.GetProperty("authType").GetString()); }
        else Assert.Equal(expected + Environment.NewLine, result.Output);
        Assert.Equal(noRefresh ? 0 : 1, refreshes); Assert.Equal(noRefresh ? 0 : 2, derives);
    }

    /// <summary>【CodingAgent】【不可输出认证】仅头部非 Bearer 或云环境认证可就绪，但请求输出凭据时报告不可用。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CredentialsRequested_WhenNoPrintableValue_IsNotReady()
    {
        var store = new InMemoryProviderCredentialStore();
        var auth = new ProviderAuthDefinition(apiKey: new("Cloud", _ => Task.FromResult<ProviderAuthResult?>(new(Headers: new Dictionary<string, string> { ["X-Cloud"] = "configured" }))));
        var result = await Run(["auth", "check", "--provider", "test", "--credentials", "--json"], new(new Models([Provider("test", auth)], credentialStore: store), store));
        Assert.Equal(1, result.Code); using var document = JsonDocument.Parse(result.Output); Assert.Equal("credential_not_available", document.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>【CodingAgent】【类型隔离】API key 和 OAuth 输出命令不能打印另一种已保存凭据。</summary><param name="oauth">保存 OAuth。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Print_RejectsWrongStoredCredentialType(bool oauth)
    {
        var store = new InMemoryProviderCredentialStore();
        if (oauth) await SaveOAuth(store, "test", 120); else await SaveKey(store, "test", "fixture-key");
        var models = new Models([Provider("test", new(apiKey: Key(), oauth: OAuth()))], credentialStore: store);
        var result = await Run(["auth", oauth ? "print-api-key" : "print-bearer-token", "--provider", "test"], new(models, store));
        Assert.Equal(1, result.Code); Assert.Empty(result.Output); Assert.Contains(oauth ? "configured with OAuth" : "not configured with an OAuth bearer token", result.Error);
    }

    /// <summary>【CodingAgent】【bearer 最低期限】默认要求半小时，可显式指定更长或更短期限，底层仍至少五分钟。</summary>
    /// <param name="minimum">显式期限。</param><param name="minutes">当前剩余分钟。</param><param name="refreshExpected">应否刷新。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null, 10, true)]
    [InlineData(null, 60, false)]
    [InlineData("1h", 40, true)]
    [InlineData("0ms", 2, true)]
    [InlineData("10m", 40, false)]
    public async Task BearerPrint_EnforcesRequestedValidity(string? minimum, int minutes, bool refreshExpected)
    {
        var store = new InMemoryProviderCredentialStore(); await SaveOAuth(store, "test", minutes); var calls = 0;
        var oauth = OAuth((credential, _) => { calls++; return Task.FromResult(credential with { Access = "renewed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) }); });
        var args = new List<string> { "auth", "print-bearer-token", "--provider", "test" }; if (minimum is not null) { args.Add("--min-expiry"); args.Add(minimum); }
        var result = await Run(args, new(new Models([Provider("test", new(oauth: oauth))], credentialStore: store), store));
        Assert.Equal(0, result.Code); Assert.Equal(refreshExpected ? 1 : 0, calls); Assert.Equal((refreshExpected ? "renewed" : "old-access") + Environment.NewLine, result.Output);
    }

    /// <summary>【CodingAgent】【短令牌拒绝】刷新结果仍未满足最低期限时不输出令牌，也不泄露底层错误。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BearerPrint_RejectsShortRefreshedToken()
    {
        var store = new InMemoryProviderCredentialStore(); await SaveOAuth(store, "test", -1);
        var oauth = OAuth((credential, _) => Task.FromResult(credential with { Access = "too-short-secret", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) }));
        var result = await Run(["auth", "print-bearer-token", "--provider", "test"], new(new Models([Provider("test", new(oauth: oauth))], credentialStore: store), store));
        Assert.Equal(1, result.Code); Assert.Empty(result.Output); Assert.Equal("Error: Failed to resolve credential" + Environment.NewLine, result.Error);
    }

    /// <summary>【CodingAgent】【模型筛选】只搜索保存凭据的提供方，多个可用结果要求显式 provider。</summary><param name="both">是否两者都保存密钥。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelOnlyPrint_UsesStoredProvidersAndRejectsAmbiguity(bool both)
    {
        var store = new InMemoryProviderCredentialStore(); await SaveKey(store, "first", "first-key"); if (both) await SaveKey(store, "second", "second-key");
        var models = new Models([Provider("first", new(apiKey: Key())), Provider("second", new(apiKey: Key()))], credentialStore: store);
        var result = await Run(["auth", "print-api-key", "--model", "chat"], new(models, store));
        Assert.Equal(both ? 1 : 0, result.Code);
        if (both) { Assert.Empty(result.Output); Assert.Contains("Multiple configured providers matched (first, second)", result.Error); }
        else Assert.Equal("first-key" + Environment.NewLine, result.Output);
    }

    /// <summary>【CodingAgent】【头部密钥】API key 输出兼容只通过 Bearer 头派生的认证结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ApiKeyPrint_CanExtractBearerHeader()
    {
        var store = new InMemoryProviderCredentialStore(); var auth = new ProviderAuthDefinition(apiKey: new("Key", _ => Task.FromResult<ProviderAuthResult?>(new(Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer header-key" }))));
        var result = await Run(["auth", "print-api-key", "--provider", "test"], new(new Models([Provider("test", auth)], credentialStore: store), store));
        Assert.Equal(0, result.Code); Assert.Equal("header-key" + Environment.NewLine, result.Output);
    }

    /// <summary>【CodingAgent】【参数退出码】未知选项退出 1，检查参数错误退出 2，帮助不初始化运行时。</summary><param name="line">参数。</param><param name="code">退出码。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("auth check", 2)]
    [InlineData("auth check --provider", 2)]
    [InlineData("auth print-api-key", 1)]
    [InlineData("auth check --provider test --unknown", 1)]
    [InlineData("auth print-bearer-token --json", 1)]
    [InlineData("auth check --help", 0)]
    public async Task Validation_DoesNotCreateRuntime(string line, int code)
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(code, await CodingAgentAuthCommands.HandleAsync(line.Split(' '), output, error, _ => throw new InvalidOperationException("Must not initialize")));
    }

    /// <summary>【CodingAgent】【实际入口】现有 auth CLI 分派检查命令，并通过文件凭据读取合成密钥。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExistingCli_RoutesCheckThroughFileCredentialStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-auth-command-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(directory, "auth.json");
        try
        {
            var store = new OAuthCredentialStore([path]); store.SaveApiKey("openai", new("fixture-key"));
            var output = new StringWriter(); var error = new StringWriter();
            var code = await CodingAgentAuthCli.TryHandleAsync(["auth", "check", "--provider", "openai", "--no-refresh", "--credentials"], TextReader.Null, output, error,
                oauthProviders: new OAuthProviderRegistry([]), credentialStore: store);
            Assert.Equal(0, code); Assert.Equal("fixture-key" + Environment.NewLine, output.ToString()); Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>【CodingAgent】【命令取消】初始化或认证解析忽略取消时，命令仍停止等待且不输出迟到凭据。</summary><param name="initializing">暂停初始化。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrintCancellation_StopsNonCooperativeOperation(bool initializing)
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryProviderCredentialStore();
        var auth = new ProviderAuthDefinition(apiKey: new("Key", async _ => { entered.TrySetResult(); await release.Task; return new("late-secret"); }));
        var runtime = new CodingAgentAuthRuntime(new Models([Provider("test", auth)], credentialStore: store), store);
        var output = new StringWriter(); var error = new StringWriter();
        var pending = CodingAgentAuthCommands.HandleAsync(["auth", "print-api-key", "--provider", "test"], output, error,
            async _ => { if (initializing) { entered.TrySetResult(); await release.Task; } return runtime; }, source.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
            Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Empty(output.ToString()); Assert.DoesNotContain("late-secret", error.ToString());
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>【CodingAgent】【测试执行】捕获单次命令输出，不向真实控制台打印合成凭据。</summary><param name="args">参数。</param><param name="runtime">模拟运行时。</param><returns>退出码及文本。</returns>
    private static async Task<(int Code, string Output, string Error)> Run(IReadOnlyList<string> args, CodingAgentAuthRuntime runtime)
    {
        var output = new StringWriter(); var error = new StringWriter(); var code = await CodingAgentAuthCommands.HandleAsync(args, output, error, _ => Task.FromResult(runtime));
        return (code, output.ToString(), error.ToString());
    }
    /// <summary>【CodingAgent】【测试密钥】只使用已保存合成凭据。</summary><returns>认证定义。</returns>
    private static ApiKeyAuthDefinition Key() => new("Key", context => Task.FromResult<ProviderAuthResult?>(context.Credential is null ? null : new(context.Credential.Key)));
    /// <summary>【CodingAgent】【测试 OAuth】注入轮换与派生行为。</summary><param name="refresh">轮换。</param><param name="derive">派生。</param><returns>认证定义。</returns>
    private static OAuthAuthDefinition OAuth(Func<ProviderOAuthCredential, CancellationToken, Task<ProviderOAuthCredential>>? refresh = null,
        Func<ProviderOAuthCredential, Task<ProviderAuthResult>>? derive = null) => new("OAuth", _ => throw new InvalidOperationException(), refresh ?? ((value, _) => Task.FromResult(value)), derive ?? (value => Task.FromResult(new ProviderAuthResult(value.Access))));
    /// <summary>【CodingAgent】【测试提供方】创建一个聊天模型及认证。</summary><param name="id">标识。</param><param name="auth">认证。</param><returns>提供方。</returns>
    private static ProviderDefinition Provider(string id, ProviderAuthDefinition auth) => new(id, models: [new Model { Id = "chat", Name = "Chat", Provider = id, Api = "fixture" }], auth: auth);
    /// <summary>【CodingAgent】【测试 OAuth 保存】创建指定剩余期限的合成凭据。</summary><param name="store">存储。</param><param name="id">提供方。</param><param name="minutes">剩余分钟。</param><returns>任务。</returns>
    private static Task SaveOAuth(InMemoryProviderCredentialStore store, string id, int minutes) => store.ModifyAsync(id, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(new("refresh", "old-access", DateTimeOffset.UtcNow.AddMinutes(minutes)))));
    /// <summary>【CodingAgent】【测试密钥保存】保存合成密钥。</summary><param name="store">存储。</param><param name="id">提供方。</param><param name="key">合成密钥。</param><returns>任务。</returns>
    private static Task SaveKey(InMemoryProviderCredentialStore store, string id, string key) => store.ModifyAsync(id, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new(key))));
}
