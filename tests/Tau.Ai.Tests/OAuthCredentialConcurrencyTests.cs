// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【OAuth 竞争回归】验证跨存储实例刷新、登录、注销与取消的提交边界。</summary>
public sealed class OAuthCredentialConcurrencyTests
{
    /// <summary>【AI】【并发刷新】多个解析器同时请求过期令牌，只允许一次刷新并共享新凭据。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConcurrentResolvers_RefreshRotatedTokenOnlyOnce()
    {
        using var fixture = new CredentialFixture();
        var provider = new GatedOAuthProvider();
        var calls = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            var resolver = new ProviderAuthResolver(credentialStore: fixture.Store(), configurationStore: new ModelConfigurationStore([]));
            resolver.SetRuntimeOAuthProviders(new Dictionary<string, IOAuthProvider> { [provider.Id] = provider });
            if (index % 2 == 0) return resolver.ResolveApiKey(provider.Id);
            var credential = await resolver.ResolveRefreshCredentialAsync(provider.Id, allowNetwork: true);
            return credential!.Value.GetProperty("access").GetString();
        })).ToArray();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.TrySetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, value => Assert.Equal("renewed", value));
        Assert.Equal(1, provider.RefreshCalls);
    }

    /// <summary>【AI】【认证竞争】刷新中的登录覆盖和注销按锁顺序提交，不会被旧刷新结果复活。</summary>
    /// <param name="logout">是否删除而非登录覆盖。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentMutation_WaitsForRefreshAndWins(bool logout)
    {
        using var fixture = new CredentialFixture();
        var provider = new GatedOAuthProvider();
        var refresh = fixture.Store().RefreshAsync(provider.Id, provider);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<IOException>(() => { using var lease = new FileStream(fixture.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = Task.Run(() =>
        {
            entered.TrySetResult();
            if (logout) fixture.Store().Remove(provider.Id);
            else fixture.Store().Save(provider.Id, CredentialFixture.Credentials("login"));
        });
        await entered.Task;
        provider.Release.TrySetResult();
        await Task.WhenAll(refresh, mutation).WaitAsync(TimeSpan.FromSeconds(20));
        var stored = fixture.Store().Load().GetValueOrDefault(provider.Id);
        if (logout) Assert.Null(stored);
        else Assert.Equal("login", stored!.Access);
    }

    /// <summary>【AI】【取消提交】即使提供方忽略取消并返回令牌，存储也不能写入已取消的结果。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CancelledRefresh_DoesNotCommitAndReleasesFileLock()
    {
        using var fixture = new CredentialFixture();
        var before = File.ReadAllText(fixture.Path);
        var provider = new GatedOAuthProvider();
        using var source = new CancellationTokenSource();
        var refresh = fixture.Store().RefreshAsync(provider.Id, provider, source.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        fixture.Store().Save(provider.Id, CredentialFixture.Credentials("login"));
        provider.Release.TrySetResult();
        Assert.Equal("login", fixture.Store().Load()[provider.Id].Access);
    }

    /// <summary>【AI】【刷新时限】提供方忽略取消时仍释放文件锁，超时不覆盖原始文件。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RefreshTimeout_ReleasesFileLockBeforeProviderReturns()
    {
        using var fixture = new CredentialFixture();
        var before = File.ReadAllText(fixture.Path);
        var provider = new GatedOAuthProvider();
        var store = new OAuthCredentialStore([fixture.Path]) { RefreshTimeout = TimeSpan.FromMilliseconds(50) };
        var refresh = store.RefreshAsync(provider.Id, provider);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(before, File.ReadAllText(fixture.Path));
            store.Save(provider.Id, CredentialFixture.Credentials("after-timeout"));
        }
        finally { provider.Release.TrySetResult(); }
        Assert.Equal("after-timeout", store.Load()[provider.Id].Access);
    }

    /// <summary>【AI】【等待取消】文件锁被其他进程持有时，可取消当前等待且不修改文件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task LockContention_RespondsToCancellation()
    {
        using var fixture = new CredentialFixture();
        using var source = new CancellationTokenSource();
        using var lease = new FileStream(fixture.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var refresh = fixture.Store().RefreshAsync("rotation", new GatedOAuthProvider(), source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("expired", fixture.Store().Load()["rotation"].Access);
    }

    /// <summary>【AI】【并发保存】多个存储实例写入不同提供方时保留所有条目。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConcurrentSaves_PreserveAllProviders()
    {
        using var fixture = new CredentialFixture();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() => fixture.Store().Save("provider-" + index, CredentialFixture.Credentials("token-" + index)))));
        var entries = fixture.Store().Load();
        Assert.Equal(17, entries.Count);
        for (var index = 0; index < 16; index++) Assert.Equal("token-" + index, entries["provider-" + index].Access);
    }

    /// <summary>【AI】【Windows 凭据占用】读取者短暂禁止删除时继续原子替换，等待期间原文件保持完整。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Save_RetriesTemporaryWindowsReplaceSharingConflict()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CredentialFixture();
        var original = File.ReadAllText(fixture.Path);
        Task save;
        using (var reader = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            save = Task.Run(() => fixture.Store().Save("later", CredentialFixture.Credentials("new-token")));
            await Task.Delay(100);
            Assert.False(save.IsCompleted);
            Assert.Equal(original, File.ReadAllText(fixture.Path));
        }
        await save.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("new-token", fixture.Store().Load()["later"].Access);
        Assert.Equal(2, fixture.Store().Load().Count);
    }

    /// <summary>【AI】【损坏保护】保存前发现非对象或无效 JSON 时保留原文件并报告错误。</summary>
    /// <param name="content">损坏文件内容。</param>
    [Theory]
    [InlineData("{ invalid")]
    [InlineData("[]")]
    public void Save_RejectsCorruptFileWithoutOverwriting(string content)
    {
        using var fixture = new CredentialFixture();
        File.WriteAllText(fixture.Path, content);
        Assert.ThrowsAny<JsonException>(() => fixture.Store().Save("rotation", CredentialFixture.Credentials("new")));
        Assert.Equal(content, File.ReadAllText(fixture.Path));
    }

    /// <summary>【AI】【测试凭据】独立临时目录及存储实例。</summary>
    private sealed class CredentialFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-credential-race-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "auth.json");
        /// <summary>创建过期凭据。</summary>
        public CredentialFixture() => Store().Save("rotation", Credentials("expired") with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) });
        /// <summary>创建共享同一文件的新存储实例。</summary><returns>存储实例。</returns>
        public OAuthCredentialStore Store() => new([Path]);
        /// <summary>创建测试凭据。</summary><param name="access">访问令牌。</param><returns>凭据。</returns>
        public static OAuthCredentials Credentials(string access) => new() { Access = access, Refresh = "rotation", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        /// <summary>清理本测试创建的临时目录。</summary>
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    /// <summary>【AI】【刷新屏障】在测试释放之前暂停令牌刷新。</summary>
    private sealed class GatedOAuthProvider : IOAuthProvider
    {
        public string Id => "rotation";
        public string Name => "Rotation";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCalls;
        /// <summary>测试不执行交互登录。</summary><param name="callbacks">交互。</param><param name="cancellationToken">取消。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>等待测试释放后返回新凭据，故意忽略取消以检验存储提交边界。</summary><param name="credentials">旧凭据。</param><param name="cancellationToken">取消。</param><returns>新凭据。</returns>
        public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RefreshCalls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return CredentialFixture.Credentials("renewed");
        }
        /// <summary>派生请求密钥。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
}
