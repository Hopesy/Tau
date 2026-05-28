using Microsoft.Extensions.Options;
using Tau.Ai.Observability;
using Tau.WebUi;
using Tau.WebUi.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<WebUiOptions>(builder.Configuration.GetSection("WebUi"));
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var configured = sp.GetRequiredService<IOptions<WebUiOptions>>().Value;
    var root = Directory.GetParent(env.ContentRootPath)?.Parent?.FullName ?? env.ContentRootPath;
    var path = Path.GetFullPath(configured.SessionsPath, root);
    return new WebChatStore(path);
});
builder.Services.AddSingleton<ITauLogSink>(_ =>
{
    try
    {
        return JsonlTauLogSink.FromEnvironment() is { } sink
            ? sink
            : NullTauLogSink.Instance;
    }
    catch
    {
        return NullTauLogSink.Instance;
    }
});
builder.Services.AddSingleton<WebChatService>();

var app = builder.Build();

app.MapWebUiEndpoints();

app.Run();
