// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【控制台认证测试】使用隔离的同步输入与输出验证真实提示路径。</summary>
public sealed class ConsoleOAuthLoginCallbacksTests
{
    private static readonly OAuthSelectOption[] Options = [new("browser", "Browser", "local callback"), new("device_code", "Device")];

    /// <summary>【CodingAgent】【输入取消】同步 TextReader 阻塞时，所有提示类型都能取消。</summary>
    /// <param name="kind">提示类型。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("prompt")]
    [InlineData("manual")]
    [InlineData("secret")]
    [InlineData("select")]
    public async Task Prompts_CancelWithoutWaitingForPhysicalInput(string kind)
    {
        using var input = new BlockingReader(); using var output = new StringWriter(); using var source = new CancellationTokenSource();
        var callbacks = new ConsoleOAuthLoginCallbacks(input, output, _ => { });
        Task task = kind switch
        {
            "manual" => callbacks.OnManualCodeInputAsync("code", "redirect", source.Token),
            "secret" => callbacks.OnSecretPromptAsync("key", null, source.Token),
            "select" => (Task)callbacks.OnSelectAsync("method", Options, source.Token),
            _ => callbacks.OnPromptAsync("name", null, true, source.Token)
        };
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); source.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { input.Line.TrySetResult("released"); await input.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>【CodingAgent】【选择方式】支持回车默认、候选 ID、序号及无效值后重试。</summary>
    /// <param name="input">输入行。</param><param name="expected">候选 ID。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("\n", "browser")]
    [InlineData("2\n", "device_code")]
    [InlineData("device_code\n", "device_code")]
    [InlineData("unknown\n9\n1\n", "browser")]
    public async Task Selection_ResolvesSupportedInput(string input, string expected)
    {
        using var output = new StringWriter(); using var reader = new StringReader(input);
        var callbacks = new ConsoleOAuthLoginCallbacks(reader, output, _ => { });
        Assert.Equal(expected, await callbacks.OnSelectAsync("method", Options, default));
        Assert.Contains("local callback", output.ToString());
    }

    /// <summary>【CodingAgent】【秘密通道】秘密输入不回显，读取函数能区分秘密与普通输入。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task SecretPrompt_DoesNotEchoCredential()
    {
        using var output = new StringWriter(); var secret = false;
        var callbacks = new ConsoleOAuthLoginCallbacks(TextReader.Null, output, _ => { }, (isSecret, _) => { secret = isSecret; return Task.FromResult<string?>("synthetic-secret-value"); });
        Assert.Equal("synthetic-secret-value", await callbacks.OnSecretPromptAsync("API key", null, default));
        Assert.True(secret); Assert.DoesNotContain("synthetic-secret-value", output.ToString());
        await callbacks.OnPromptAsync("ordinary", null, true, default); Assert.False(secret);
    }

    /// <summary>【CodingAgent】【设备通知】设备码只显示链接和代码，普通 auth_url 才调用浏览器。</summary>
    [Fact]
    public void DeviceNotification_DoesNotLaunchBrowser()
    {
        using var output = new StringWriter(); var launched = new List<string>();
        var callbacks = new ConsoleOAuthLoginCallbacks(TextReader.Null, output, launched.Add);
        callbacks.OnDeviceCode(new("USER", "https://unit.invalid/device", 0.5, 900));
        Assert.Empty(launched); Assert.Contains("Enter code: USER", output.ToString()); Assert.Contains("https://unit.invalid/device", output.ToString());
        callbacks.OnAuth("https://unit.invalid/authorize", "Instructions"); Assert.Equal(new[] { "https://unit.invalid/authorize" }, launched);
    }

    /// <summary>【CodingAgent】【已取消提示】不输出提示或调用读取。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task PreCancelledPrompt_DoesNotWriteOrRead()
    {
        using var output = new StringWriter(); using var source = new CancellationTokenSource(); source.Cancel(); var called = false;
        var callbacks = new ConsoleOAuthLoginCallbacks(TextReader.Null, output, _ => { }, (_, _) => { called = true; return Task.FromResult<string?>("value"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callbacks.OnPromptAsync("prompt", null, true, source.Token));
        Assert.False(called); Assert.Equal("", output.ToString());
    }

    /// <summary>【CodingAgent】【旧手工回调】浏览器胜出后能取消旧接口，新的普通提示继续可用。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task LegacyManualInput_CanBeCancelled()
    {
        using var output = new StringWriter(); var reads = 0;
        var callbacks = new ConsoleOAuthLoginCallbacks(TextReader.Null, output, _ => { }, (_, _) => ++reads == 1 ? new TaskCompletionSource<string?>().Task : Task.FromResult<string?>("next"));
        var manual = callbacks.OnManualCodeInputAsync()!; callbacks.CancelManualCodeInput();
        Assert.Equal("", await manual.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("next", await callbacks.OnPromptAsync("next prompt"));
    }

    /// <summary>【CodingAgent】【CLI 输入注入】缺省回调使用 CLI 传入的输入输出，选择与秘密字段可按脚本提供。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task AuthCli_DefaultCallbacksUseSuppliedStreams()
    {
        var root = Path.Combine(Path.GetTempPath(), "tau-console-auth-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var input = new StringReader("\nsynthetic-secret-value\n"); using var output = new StringWriter(); using var error = new StringWriter();
            var store = new OAuthCredentialStore([Path.Combine(root, "auth.json")]);
            var result = await CodingAgentAuthCli.TryHandleAsync(["auth", "login", "test"], input, output, error,
                oauthProviders: new OAuthProviderRegistry([new PromptProvider()]), credentialStore: store);
            Assert.Equal(0, result); Assert.Equal("", error.ToString()); Assert.Equal("synthetic-secret-value", store.Load()["test"].Access);
            Assert.DoesNotContain("synthetic-secret-value", output.ToString()); Assert.Contains("Choose method", output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>【CodingAgent】【CLI 取消】选择提供方时也能取消，不启动登录或写入凭据。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task AuthCli_CancelsProviderSelection()
    {
        using var input = new BlockingReader(); using var output = new StringWriter(); using var error = new StringWriter(); using var source = new CancellationTokenSource();
        var provider = new FakeOAuthProvider();
        var task = CodingAgentAuthCli.TryHandleAsync(["auth", "login"], input, output, error,
            oauthProviders: new OAuthProviderRegistry([provider]), cancellationToken: source.Token);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); source.Cancel();
        try { Assert.Equal(1, await task.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Contains("Login cancelled", error.ToString()); Assert.Equal(0, provider.LoginCalls); }
        finally { input.Line.TrySetResult("1"); await input.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>【CodingAgent】【EOF 处理】允许空行的提示仍将 EOF 当作取消。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Prompt_EofCancelsEvenWhenEmptyAllowed()
    {
        using var output = new StringWriter(); var callbacks = new ConsoleOAuthLoginCallbacks(TextReader.Null, output, _ => { });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callbacks.OnPromptAsync("input", null, true, default));
    }

    /// <summary>【CodingAgent】【同步输入夹具】模拟物理读取不支持取消。</summary>
    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public override string? ReadLine() { Started.TrySetResult(); try { return Line.Task.GetAwaiter().GetResult(); } finally { Finished.TrySetResult(); } }
    }

    /// <summary>【CodingAgent】【提示提供方】验证 CLI 默认回调，无网络请求。</summary>
    private sealed class PromptProvider : IOAuthProvider
    {
        public string Id => "test";
        public string Name => "Test";
        /// <inheritdoc />
        public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
        {
            Assert.Equal("browser", await callbacks.OnSelectAsync("Choose method", Options, cancellationToken));
            var secret = await callbacks.OnSecretPromptAsync("API key", null, cancellationToken);
            return new() { Access = secret, Refresh = "synthetic-refresh", ExpiresAt = DateTimeOffset.MaxValue };
        }
        /// <inheritdoc />
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(credentials);
        /// <inheritdoc />
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
}
