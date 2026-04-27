using Microsoft.Extensions.Options;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;
using Tau.WebUi.Ui;

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
builder.Services.AddSingleton<WebChatService>();

var app = builder.Build();

app.MapGet("/", () => Results.Content(WebUiPage.Html, "text/html; charset=utf-8"));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/status", (WebChatService chat) => Results.Ok(chat.GetStatus()));
app.MapGet("/api/catalog", (WebChatService chat) => Results.Ok(chat.GetCatalog()));
app.MapGet("/api/sessions", (WebChatService chat) => Results.Ok(chat.ListSessions()));
app.MapGet("/api/sessions/{id}", (string id, WebChatService chat) =>
{
    var session = chat.GetSession(id);
    return session is null ? Results.NotFound() : Results.Ok(session);
});
app.MapPost("/api/sessions", (CreateSessionRequest? request, WebChatService chat) =>
{
    try
    {
        return Results.Ok(chat.CreateSession(request?.Title, request?.Provider, request?.Model));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});
app.MapPut("/api/sessions/{id}", (string id, UpdateSessionSettingsRequest request, WebChatService chat) =>
{
    try
    {
        var session = chat.UpdateSessionSettings(id, request);
        return session is null ? Results.NotFound() : Results.Ok(session);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});
app.MapPost("/api/sessions/{id}/messages", async (string id, SendMessageRequest request, WebChatService chat, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest("Message text is required.");
    }

    var session = await chat.SendMessageAsync(id, request.Text, cancellationToken).ConfigureAwait(false);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.Run();
