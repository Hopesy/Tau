// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【统一文件存储测试】验证严格读取、原生字段、跨实例修改和取消提交。</summary>
public sealed class FileProviderCredentialStoreTests
{
    /// <summary>【AI】【密钥往返】原生空字符串、空白字段及环境值不会在统一接口读取时消失。</summary><param name="key">合成密钥。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("fixture-key")]
    public async Task ApiKey_RoundTripsNativeValues(string? key)
    {
        using var fixture = new Fixture();
        var credential = new ProviderCredential.ApiKey(new(key, new Dictionary<string, string> { ["EMPTY"] = "", ["SPACE"] = " ", ["VALUE"] = "yes" }));
        await fixture.Store().ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(credential));
        var restored = Assert.IsType<ProviderCredential.ApiKey>(await fixture.Store().ReadAsync("test"));
        Assert.Equal(key, restored.Value.Key); Assert.Equal(credential.Value.Env, restored.Value.Env);
        Assert.Equal(new ProviderCredentialInfo("test", "api_key"), Assert.Single(await fixture.Store().ListAsync()));
    }

    /// <summary>【AI】【OAuth 往返】统一接口保存完整 JSON 扩展字段与超出日期范围的原生期限。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task OAuth_PreservesStructuredPropertiesAndNativeExpiry()
    {
        using var fixture = new Fixture(); using var document = JsonDocument.Parse("""{"values":[1,true,null]}""");
        var value = new ProviderOAuthCredential("refresh", "access", DateTimeOffset.MaxValue)
        { ExpiresUnixTimeMilliseconds = 9007199254740991, Properties = new Dictionary<string, JsonElement> { ["details"] = document.RootElement.Clone() } };
        await fixture.Store().ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(value)));
        var restored = Assert.IsType<ProviderCredential.OAuth>(await fixture.Store().ReadAsync("test")).Value;
        Assert.Equal(value.ExpiresUnixTimeMilliseconds, restored.ExpiresUnixTimeMilliseconds); Assert.True(JsonElement.DeepEquals(value.Properties["details"], restored.Properties["details"]));
        Assert.Equal("oauth", Assert.Single(await fixture.Store().ListAsync()).Type);
    }

    /// <summary>【AI】【未知条目】修改和删除已知提供方时保留未知 JSON，元数据列表跳过未来类型。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Mutation_PreservesUnknownEntriesAndNullMeansUnchanged()
    {
        using var fixture = new Fixture(); fixture.Write("""{"future":{"type":"future","secret":{"value":42}},"invalid":[]}""");
        var store = fixture.Store(); await store.ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("saved"))));
        Assert.Single(await store.ListAsync()); var before = File.ReadAllText(fixture.Path);
        Assert.Equal("saved", Assert.IsType<ProviderCredential.ApiKey>(await store.ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(null))).Value.Key);
        Assert.Equal(before, File.ReadAllText(fixture.Path)); await store.DeleteAsync("test");
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Path)); Assert.Equal(42, document.RootElement.GetProperty("future").GetProperty("secret").GetProperty("value").GetInt32());
        Assert.Empty(await store.ListAsync());
    }

    /// <summary>【AI】【损坏文件】读取、列出、修改和删除都拒绝损坏内容，原文件不变。</summary><param name="operation">接口。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("read")]
    [InlineData("list")]
    [InlineData("modify")]
    [InlineData("delete")]
    public async Task CorruptFile_IsReportedWithoutOverwrite(string operation)
    {
        using var fixture = new Fixture(); fixture.Write("{invalid"); var store = fixture.Store();
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            switch (operation)
            {
                case "read": await store.ReadAsync("test"); break;
                case "list": await store.ListAsync(); break;
                case "modify": await store.ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(null)); break;
                default: await store.DeleteAsync("test"); break;
            }
        });
        Assert.Equal("{invalid", File.ReadAllText(fixture.Path));
    }

    /// <summary>【AI】【只读路径】缺失文件的读取、列举与删除不创建用户配置目录。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task MissingStore_ReadOperationsHaveNoFilesystemSideEffects()
    {
        using var fixture = new Fixture(); var store = fixture.Store();
        Assert.Null(await store.ReadAsync("test")); Assert.Empty(await store.ListAsync()); await store.DeleteAsync("test");
        Assert.False(Directory.Exists(fixture.Directory));
    }

    /// <summary>【AI】【跨实例修改】多个存储共享文件锁，读改写不会丢失已提交的值。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ConcurrentStores_SerializeReadModifyWrite()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Store().ModifyAsync("counter", async current =>
        {
            var number = current is ProviderCredential.ApiKey key ? int.Parse(key.Value.Key!) : 0;
            await Task.Delay(5);
            return new ProviderCredential.ApiKey(new((number + 1).ToString()));
        })));
        Assert.Equal("12", Assert.IsType<ProviderCredential.ApiKey>(await fixture.Store().ReadAsync("counter")).Value.Key);
    }

    /// <summary>【AI】【取消提交】非协作修改取消后释放文件锁，迟到结果不能覆盖后续保存。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CanceledMutation_ReleasesLockAndRejectsLateResult()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Store().ModifyAsync("test", _ => { entered.TrySetResult(); return late.Task; }, cancellation.Token);
        await entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Store().ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("new"))));
        late.SetResult(new ProviderCredential.ApiKey(new("late")));
        Assert.Equal("new", Assert.IsType<ProviderCredential.ApiKey>(await fixture.Store().ReadAsync("test")).Value.Key);
    }

    /// <summary>【AI】【退出排序】注销等待正在进行的修改结束，包括首次保存尚未创建 auth.json 的情况。</summary><param name="existing">是否已有文件。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_WaitsForActiveMutationAndWins(bool existing)
    {
        using var fixture = new Fixture(); if (existing) fixture.Write("{}");
        var release = new TaskCompletionSource<ProviderCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Store().ModifyAsync("test", _ => release.Task);
        var deletion = fixture.Store().DeleteAsync("test"); Assert.False(deletion.IsCompleted);
        release.SetResult(new ProviderCredential.ApiKey(new("rotated")));
        await Task.WhenAll(pending, deletion).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(await fixture.Store().ReadAsync("test"));
    }

    /// <summary>【AI】【SDK 文件刷新】多个 Models 实例通过文件存储只轮换一次令牌，后续实例读取已提交结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ModelsInstances_RefreshOnceAndPersistOAuth()
    {
        using var fixture = new Fixture();
        await fixture.Store().ModifyAsync("test", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(new("refresh", "old", DateTimeOffset.UnixEpoch))));
        var calls = 0;
        var oauth = new OAuthAuthDefinition("OAuth", _ => throw new InvalidOperationException(), async (credential, _) =>
        { Interlocked.Increment(ref calls); await Task.Delay(20); return credential with { Access = "new", ExpiresAt = DateTimeOffset.MaxValue, ExpiresUnixTimeMilliseconds = null }; }, credential => Task.FromResult(new ProviderAuthResult(credential.Access)));
        var model = new Model { Id = "chat", Name = "Chat", Provider = "test", Api = "fixture" };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new Models([new ProviderDefinition("test", models: [model], auth: new(oauth: oauth))], credentialStore: fixture.Store()).ResolveAuthAsync(model)));
        Assert.All(results, result => Assert.Equal("new", result!.ApiKey)); Assert.Equal(1, calls);
        Assert.Equal("new", fixture.Store().Load()["test"].Access);
    }

    /// <summary>【AI】【测试目录】隔离文件且只清理本测试创建的随机目录。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-provider-store-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "auth.json");
        /// <summary>【AI】【测试存储】返回共享同一文件的独立存储。</summary><returns>存储。</returns>
        public OAuthCredentialStore Store() => new([Path]);
        /// <summary>【AI】【测试文件】创建指定 JSON。</summary><param name="json">正文。</param>
        public void Write(string json) { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path, json); }
        /// <summary>【AI】【测试清理】删除随机测试目录。</summary>
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
