// 作者：xxx
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthStoreTests
{
    /// <summary>【CodingAgent】【OAuth 账号隔离】旧 URL 状态只能接管一次，同 URL 不同服务器账号独立，观察令牌不会提前迁移。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthStoreMigratesLegacyOnceAndSeparatesNamedAccounts()
    {
        using var fixture = new OAuthDirectory(); var store = new CodingAgentMcpOAuthCredentialStore(fixture.Path);
        File.WriteAllText(store.Path, """{"https://example.test/":{"serverUrl":"https://example.test/","tokens":{"access_token":"legacy"},"unknown":{"keep":true}}}""");
        Assert.Equal("legacy", (await store.TokensAsync("first", "https://EXAMPLE.test:443"))!["access_token"]!.GetValue<string>());
        Assert.Contains("\"https://example.test/\"", File.ReadAllText(store.Path));
        var first = store.ForServer("first", "https://EXAMPLE.test:443"); var second = store.ForServer("second", "https://example.test/");
        var inherited = (await first.LoadAsync())!; Assert.Equal("legacy", inherited["tokens"]!["access_token"]!.GetValue<string>());
        Assert.Null(await second.LoadAsync()); inherited["tokens"]!["access_token"] = "changed";
        Assert.Equal("legacy", (await first.LoadAsync())!["tokens"]!["access_token"]!.GetValue<string>());
        await second.SaveAsync(new() { ["serverUrl"] = "https://example.test/", ["tokens"] = new JsonObject { ["access_token"] = "second" } });
        Assert.True(await store.RemoveAsync("first", "https://example.test/")); Assert.False(await store.RemoveAsync("first", "https://example.test/"));
        Assert.Equal("second", (await store.TokensAsync("second", "https://example.test/"))!["access_token"]!.GetValue<string>());
        Assert.Null(await store.TokensAsync("second", "https://example.test/other"));
    }

    /// <summary>【CodingAgent】【OAuth 原子存储】跨实例并发更新保留所有账号，损坏 JSON 不被新凭据覆盖。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthStorePreservesConcurrentAccountsAndRejectsCorruptJson()
    {
        using var fixture = new OAuthDirectory();
        var one = new CodingAgentMcpOAuthCredentialStore(fixture.Path); var two = new CodingAgentMcpOAuthCredentialStore(fixture.Path);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? one : two).ForServer("server" + index, "https://example.test/").SaveAsync(
            new() { ["serverUrl"] = "https://example.test/", ["number"] = index })));
        var entries = JsonNode.Parse(File.ReadAllText(one.Path))!.AsObject(); Assert.Equal(12, entries.Count);
        for (var index = 0; index < 12; index++) Assert.Equal(index, (await two.ForServer("server" + index, "https://example.test/").LoadAsync())!["number"]!.GetValue<int>());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(one.Path));
        File.WriteAllText(one.Path, "{broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => one.ForServer("new", "https://example.test/").SaveAsync(new()));
        Assert.Equal("{broken", File.ReadAllText(one.Path)); Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    /// <summary>【CodingAgent】【OAuth 刷新互斥】同服务器跨实例等待支持取消，不同账号可同时刷新，持锁异常后可复用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthRefreshLockSerializesSameServerAndAllowsOtherAccounts()
    {
        using var fixture = new OAuthDirectory();
        var first = new CodingAgentMcpOAuthCredentialStore(fixture.Path).ForServer("same", "https://example.test/");
        var second = new CodingAgentMcpOAuthCredentialStore(fixture.Path).ForServer("same", "https://example.test/");
        var other = new CodingAgentMcpOAuthCredentialStore(fixture.Path).ForServer("other", "https://example.test/");
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = first.WithRefreshLockAsync(async token => { acquired.SetResult(); await release.Task.WaitAsync(token); return 1; });
        await acquired.Task;
        try
        {
            Assert.Equal(2, await other.WithRefreshLockAsync(_ => Task.FromResult(2)));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WithRefreshLockAsync(_ => Task.FromResult(3), cancellation.Token));
        }
        finally { release.SetResult(); await running; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WithRefreshLockAsync<int>(_ => throw new InvalidOperationException("fixture")));
        Assert.Equal(4, await first.WithRefreshLockAsync(_ => Task.FromResult(4)));
    }

    /// <summary>【CodingAgent】【OAuth 崩溃锁回收】真实子进程持有锁时刷新等待，进程被终止后锁立即由操作系统释放。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthRefreshLockIsReleasedAfterOwnerProcessTerminates()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OAuthDirectory();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mcp__same|https://example.test/")))[..16];
        var lockPath = Path.Combine(fixture.Path, "mcp-auth-refresh-" + hash + ".tau-lock");
        var script = Path.Combine(fixture.Path, "lock.ps1");
        File.WriteAllText(script, """
            param([string]$LockPath)
            $lease=[System.IO.File]::Open($LockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None)
            [Console]::WriteLine("locked")
            [Console]::ReadLine() | Out-Null
            $lease.Dispose()
            """);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, lockPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("locked", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var store = new CodingAgentMcpOAuthCredentialStore(fixture.Path).ForServer("same", "https://example.test/");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WithRefreshLockAsync(_ => Task.FromResult(1), cancellation.Token));
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
            Assert.Equal(2, await store.WithRefreshLockAsync(_ => Task.FromResult(2)));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    /// <summary>【CodingAgent】【OAuth 状态提供方】并发写入保留全部字段，过期时间、精确失效、随机 state 和副本隔离符合约定。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthProviderPersistsIndependentFieldsExpiryAndInvalidation()
    {
        var store = new CodingAgentMcpOAuthMemoryStore(); var metadata = new JsonObject { ["client_name"] = "fixture" };
        var provider = new CodingAgentMcpOAuthProvider(new() { ServerUrl = "https://example.test/", RedirectUrl = "http://127.0.0.1/callback", ClientMetadata = metadata,
            Store = store, TimeProvider = new FixedTime() });
        metadata["client_name"] = "changed"; Assert.Equal("fixture", provider.ClientMetadata["client_name"]!.GetValue<string>());
        Assert.Equal("none", provider.ClientMetadata["token_endpoint_auth_method"]!.GetValue<string>());
        await Task.WhenAll(provider.SaveTokensAsync(new() { ["access_token"] = "fixture", ["expires_in"] = 1.5 }), provider.SaveCodeVerifierAsync("verifier"),
            provider.SaveClientInformationAsync(new() { ["client_id"] = "client" }), provider.SaveDiscoveryStateAsync(new() { ["authorizationServerUrl"] = "https://issuer.test/" }));
        var states = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.StateAsync()));
        Assert.Matches("^[a-f0-9]{64}$", states[0]); Assert.Single(states.Distinct());
        var saved = (await store.LoadAsync())!; Assert.Equal(101500, saved["tokensExpireAt"]!.GetValue<double>());
        Assert.Equal("verifier", await provider.CodeVerifierAsync()); Assert.Equal("client", (await provider.ClientInformationAsync())!["client_id"]!.GetValue<string>());
        Assert.NotNull(await provider.DiscoveryStateAsync());
        await provider.InvalidateCredentialsAsync(CodingAgentMcpOAuthInvalidation.Tokens);
        Assert.Null(await provider.TokensAsync()); Assert.False((await store.LoadAsync())!.ContainsKey("tokensExpireAt")); Assert.Equal(states[0], await provider.StateAsync());
        await provider.SaveTokensAsync(new() { ["access_token"] = "no-expiry" }); Assert.False((await store.LoadAsync())!.ContainsKey("tokensExpireAt"));
        await provider.InvalidateCredentialsAsync(CodingAgentMcpOAuthInvalidation.All);
        Assert.Equal(["serverUrl"], (await store.LoadAsync())!.Select(pair => pair.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CodeVerifierAsync());
        Assert.NotEqual(states[0], await provider.StateAsync());
    }

    /// <summary>【CodingAgent】【OAuth 身份边界】其他 URL 的凭据完全忽略，显式客户端不落盘且仍由配置提供。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthProviderIgnoresForeignStateAndKeepsConfiguredClientOutOfStore()
    {
        var store = new CodingAgentMcpOAuthMemoryStore();
        await store.SaveAsync(new() { ["serverUrl"] = "https://other.test/", ["tokens"] = new JsonObject { ["access_token"] = "foreign" }, ["clientInformation"] = new JsonObject { ["client_id"] = "foreign" } });
        var provider = new CodingAgentMcpOAuthProvider(new() { ServerUrl = "https://example.test/", RedirectUrl = "http://127.0.0.1/callback", Store = store, ClientId = "configured", ClientSecret = "fixture-secret" });
        Assert.Null(await provider.TokensAsync()); Assert.Equal("configured", (await provider.ClientInformationAsync())!["client_id"]!.GetValue<string>());
        await provider.SaveClientInformationAsync(new() { ["client_id"] = "new" }); Assert.Equal("https://other.test/", (await store.LoadAsync())!["serverUrl"]!.GetValue<string>());
        await provider.SaveCodeVerifierAsync("own"); Assert.Equal(["serverUrl", "codeVerifier"], (await store.LoadAsync())!.Select(pair => pair.Key));
        await provider.InvalidateCredentialsAsync(CodingAgentMcpOAuthInvalidation.All); Assert.Equal("configured", (await provider.ClientInformationAsync())!["client_id"]!.GetValue<string>());
        Assert.Equal("client_secret_post", provider.ClientMetadata["token_endpoint_auth_method"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【OAuth 测试时间】固定保存时刻，精确验证秒到毫秒转换。</summary>
    private sealed class FixedTime : TimeProvider
    {
        /// <summary>【CodingAgent】【测试时钟】返回固定 UTC 时间。</summary><returns>100 秒的 Unix 时间。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(100000);
    }
    /// <summary>【CodingAgent】【OAuth 测试目录】每个测试使用独立目录，不读取用户真实凭据。</summary>
    private sealed class OAuthDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        /// <summary>【CodingAgent】【测试目录创建】创建本夹具唯一目录。</summary>
        internal OAuthDirectory() => Directory.CreateDirectory(Path);
        /// <summary>【CodingAgent】【测试目录清理】仅删除本夹具创建的文件。</summary>
        public void Dispose() => Directory.Delete(Path, true);
    }
}
