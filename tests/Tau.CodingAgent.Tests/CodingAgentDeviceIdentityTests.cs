// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【安装身份测试】验证惰性创建、全局归属、并发稳定和登录选项传递。</summary>
public sealed class CodingAgentDeviceIdentityTests
{
    /// <summary>【CodingAgent】【安装身份测试】项目不能覆盖身份，重建存储对象仍读到相同全局值。</summary>
    [Fact]
    public void Identity_IsGlobalAndStableAcrossInstances()
    {
        using var files = new IdentityFiles();
        File.WriteAllText(files.Project, """{"deviceId":"project-value","theme":"project-theme"}""");
        File.WriteAllText(files.Global, """{"theme":"global-theme","custom":{"value":42}}""");
        var store = CodingAgentSettingsStore.CreateLayered(files.Global, files.Project);
        var id = store.GetOrCreateDeviceId();
        Assert.True(Guid.TryParseExact(id, "D", out _));
        Assert.Equal(id, new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId());
        Assert.Equal(id, store.Load().AdditionalSettings!["deviceId"].GetString());
        using var global = JsonDocument.Parse(File.ReadAllText(files.Global));
        Assert.Equal("global-theme", global.RootElement.GetProperty("theme").GetString());
        Assert.Equal(42, global.RootElement.GetProperty("custom").GetProperty("value").GetInt32());
        Assert.Contains("project-value", File.ReadAllText(files.Project));
    }

    /// <summary>【CodingAgent】【安装身份测试】并行实例只生成一个安装身份。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ConcurrentCreation_UsesOneIdentity()
    {
        using var files = new IdentityFiles();
        var values = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId())));
        Assert.Single(values.Distinct());
        Assert.True(Guid.TryParseExact(values[0], "D", out _));
    }

    /// <summary>【CodingAgent】【安装身份测试】首次生成后保留稳定锁文件，后续并发读取不与 DeleteOnClose 竞争。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Identity_KeepsStableLockFileAcrossConcurrentReads()
    {
        using var files = new IdentityFiles();
        var id = new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId();
        Assert.True(File.Exists(files.Global + ".lock"));
        for (var round = 0; round < 3; round++)
        {
            var values = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId())));
            Assert.All(values, value => Assert.Equal(id, value));
        }
    }

    /// <summary>【CodingAgent】【安装身份测试】其他实例持锁超过旧的短重试窗口时，等待者仍可在释放后读取。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Identity_WaitsForLongerSettingsMutation()
    {
        using var files = new IdentityFiles();
        Task<string> reader;
        using (CodingAgentSettingsFileLock.Acquire(files.Global))
        {
            reader = Task.Run(() => new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId());
            await Task.Delay(300);
            Assert.False(reader.IsCompleted);
        }
        Assert.True(Guid.TryParseExact(await reader.WaitAsync(TimeSpan.FromSeconds(5)), "D", out _));
    }

    /// <summary>【CodingAgent】【安装身份测试】损坏全局文件不能被设备标识覆盖。</summary>
    [Fact]
    public void CorruptSettings_ArePreserved()
    {
        using var files = new IdentityFiles(); File.WriteAllText(files.Global, "{broken");
        Assert.ThrowsAny<JsonException>(() => new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId());
        Assert.Equal("{broken", File.ReadAllText(files.Global));
    }

    /// <summary>【CodingAgent】【安装身份测试】已有非空值交给认证提供方校验，不静默替换身份。</summary>
    [Fact]
    public void ExistingIdentity_IsNotRegenerated()
    {
        using var files = new IdentityFiles(); File.WriteAllText(files.Global, """{"deviceId":"invalid-existing"}""");
        Assert.Equal("invalid-existing", new CodingAgentSettingsStore(files.Global).GetOrCreateDeviceId());
    }

    /// <summary>【CodingAgent】【安装身份测试】旧登录入口不读取设备标识时，不创建设置文件。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task LegacyLogin_DoesNotCreateDeviceId()
    {
        using var files = new IdentityFiles();
        var exit = await CodingAgentAuthCli.TryHandleAsync(["login", "legacy"], TextReader.Null, TextWriter.Null, TextWriter.Null,
            oauthProviders: new OAuthProviderRegistry([new FakeOAuthProvider { Id = "legacy" }]), credentialStore: new OAuthCredentialStore([files.Auth]),
            settingsStore: new CodingAgentSettingsStore(files.Global));
        Assert.Equal(0, exit);
        Assert.False(File.Exists(files.Global));
    }

    /// <summary>【CodingAgent】【安装身份测试】命令行和交互命令将同一惰性身份提供给认证实现。</summary>
    /// <param name="cli">是否独立命令行入口。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginOptions_ReachBothEntryPoints(bool cli)
    {
        using var files = new IdentityFiles(); var store = new CodingAgentSettingsStore(files.Global);
        var provider = new IdentityProvider();
        if (cli)
        {
            Assert.Equal(0, await CodingAgentAuthCli.TryHandleAsync(["login", "identity"], TextReader.Null, TextWriter.Null, TextWriter.Null,
                oauthProviders: new OAuthProviderRegistry([provider]), credentialStore: new OAuthCredentialStore([files.Auth]), settingsStore: store));
        }
        else
        {
            var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>())
            { AuthStatus = new("identity", false, "none", false, true, "Not configured"), OAuthProvider = provider };
            var router = new CodingAgentCommandRouter(runner, settingsStore: store);
            Assert.False((await router.TryHandleAsync("/login identity"))!.IsError);
        }
        Assert.Equal(store.GetOrCreateDeviceId(), provider.DeviceId);
    }

    /// <summary>【CodingAgent】【安装身份测试】仅在新登录重载里请求身份。</summary>
    private sealed class IdentityProvider : IOAuthProvider
    {
        public string Id => "identity";
        public string Name => "Identity";
        public string? DeviceId { get; private set; }
        /// <summary>要求新配置入口。</summary><param name="callbacks">交互。</param><param name="cancellationToken">取消。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>调用身份回调并验证多次调用稳定。</summary><param name="callbacks">交互。</param><param name="options">配置。</param><param name="cancellationToken">取消。</param><returns>凭据。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(options?.GetDeviceId);
            DeviceId = options.GetDeviceId(); Assert.Equal(DeviceId, options.GetDeviceId());
            return Task.FromResult(new OAuthCredentials { Access = "test-access", Refresh = "test-refresh", ExpiresAt = DateTimeOffset.MaxValue });
        }
        /// <summary>刷新不修改测试凭据。</summary><param name="credentials">凭据。</param><param name="cancellationToken">取消。</param><returns>原凭据。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(credentials);
        /// <summary>返回测试访问令牌。</summary><param name="credentials">凭据。</param><returns>令牌。</returns>
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }

    /// <summary>【CodingAgent】【安装身份测试】唯一临时文件集合。</summary>
    private sealed class IdentityFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-device-id-" + Guid.NewGuid().ToString("N"));
        public string Global => Path.Combine(_root, "global.json");
        public string Project => Path.Combine(_root, "project.json");
        public string Auth => Path.Combine(_root, "auth.json");
        /// <summary>创建测试专属目录。</summary>
        public IdentityFiles() => Directory.CreateDirectory(_root);
        /// <summary>释放本测试的唯一目录。</summary>
        public void Dispose() => Directory.Delete(_root, true);
    }
}
