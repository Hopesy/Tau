using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Tau.Agent;
using Tau.Ai;
using Tau.WebUi;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebUiBrowserFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public string BaseAddress { get; private set; } = string.Empty;
    public string StorePath { get; private set; } = string.Empty;
    public string ArtifactStorePath { get; private set; } = string.Empty;
    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialised.");
    public WebUiJavaScriptReplBridge ReplBridge =>
        _app?.Services.GetRequiredService<WebUiJavaScriptReplBridge>() ??
        throw new InvalidOperationException("WebUi app not initialised.");

    public async Task InitializeAsync()
    {
        StorePath = Path.Combine(Path.GetTempPath(), $"tau-webui-browser-{Guid.NewGuid():N}.json");
        ArtifactStorePath = Path.Combine(Path.GetTempPath(), $"tau-webui-browser-artifacts-{Guid.NewGuid():N}.json");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new WebChatStore(StorePath));
        builder.Services.AddSingleton(new WebArtifactStore(ArtifactStorePath));
        builder.Services.AddSingleton<WebArtifactService>();
        builder.Services.AddSingleton<WebUiJavaScriptReplBridge>();
        builder.Services.AddSingleton<WebChatService>(sp => new WebChatService(
            sp.GetRequiredService<WebChatStore>(),
            (_, _, _) => new FakeWebUiRunner(StreamHello)));

        _app = builder.Build();
        _app.MapWebUiEndpoints();
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        BaseAddress = (addresses?.Addresses ?? []).Single();

        _playwright = await Playwright.CreateAsync();
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
                                           ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Playwright Chromium is not installed. Run 'pwsh tests/Tau.WebUi.Tests/bin/Debug/net10.0/playwright.ps1 install chromium' once outside dotnet test.",
                ex);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("spawn EPERM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Playwright Chromium is installed but cannot be launched in this environment. Run the WebUi browser flow tests in a context that permits launching the cached Chromium executable.",
                ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (File.Exists(StorePath))
        {
            File.Delete(StorePath);
        }
        if (File.Exists(ArtifactStorePath))
        {
            File.Delete(ArtifactStorePath);
        }
    }

    public async Task<IBrowserContext> NewContextAsync() =>
        await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseAddress,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
        });

    private static async IAsyncEnumerable<AgentEvent> StreamHello(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var partial = new AssistantMessage([new TextContent("hello from tau")]);
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "hello from tau", partial));
        yield return new AgentEndEvent();
    }
}

[CollectionDefinition(nameof(WebUiBrowserCollection))]
public sealed class WebUiBrowserCollection : ICollectionFixture<WebUiBrowserFixture> { }
