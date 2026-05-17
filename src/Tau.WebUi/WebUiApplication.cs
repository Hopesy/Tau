using System.Text;
using System.Text.Json;
using Tau.Ai;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;
using Tau.WebUi.Ui;

namespace Tau.WebUi;

public static class WebUiApplication
{
    public static IEndpointRouteBuilder MapWebUiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Content(WebUiPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/status", (WebChatService chat) => Results.Ok(chat.GetStatus()));
        app.MapGet("/api/catalog", (WebChatService chat) => Results.Ok(chat.GetCatalog()));
        app.MapGet("/api/auth/{provider}", (string provider, string? model, WebChatService chat) =>
        {
            try
            {
                return Results.Ok(chat.GetAuthStatus(provider, model));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
        app.MapGet("/api/sessions", (WebChatService chat) => Results.Ok(chat.ListSessions()));
        app.MapGet("/api/sessions/search", (string? q, WebChatService chat) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("Query parameter 'q' is required.");
            }

            return Results.Ok(chat.SearchSessions(q));
        });
        app.MapGet("/api/sessions/{id}", (string id, WebChatService chat) =>
        {
            var session = chat.GetSession(id);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });
        app.MapGet("/api/sessions/{id}/export", (string id, WebChatService chat) =>
        {
            var session = chat.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var json = JsonSerializer.Serialize(session, WebUiJsonContext.Default.WebChatSessionDto);
            return Results.File(
                Encoding.UTF8.GetBytes(json),
                "application/json; charset=utf-8",
                $"{SafeFileName(session.Title)}.tau-webui-session.json");
        });
        app.MapGet("/api/sessions/{id}/export.html", (string id, WebChatService chat) =>
        {
            var session = chat.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
            var html = WebChatHtmlExporter.Render(session, redactor);
            return Results.File(
                Encoding.UTF8.GetBytes(html),
                "text/html; charset=utf-8",
                $"{SafeFileName(session.Title)}.tau-webui-session.html");
        });
        app.MapGet("/api/sessions/{id}/export.md", (string id, WebChatService chat) =>
        {
            var session = chat.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
            var markdown = WebChatMarkdownExporter.Render(session, redactor);
            return Results.File(
                Encoding.UTF8.GetBytes(markdown),
                "text/markdown; charset=utf-8",
                $"{SafeFileName(session.Title)}.tau-webui-session.md");
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
        app.MapPost("/api/sessions/import", (WebChatSessionDto session, WebChatService chat) =>
        {
            try
            {
                return Results.Ok(chat.ImportSession(session));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
        app.MapDelete("/api/sessions/{id}", (string id, WebChatService chat) =>
        {
            return chat.DeleteSession(id) ? Results.NoContent() : Results.NotFound();
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
        app.MapPost("/api/sessions/{id}/clear", (string id, WebChatService chat) =>
        {
            var session = chat.ClearSessionMessages(id);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });
        app.MapPost("/api/sessions/{id}/messages", async (string id, SendMessageRequest request, WebChatService chat, CancellationToken cancellationToken) =>
        {
            if (!HasMessageInput(request))
            {
                return Results.BadRequest("Message text or at least one attachment is required.");
            }

            var session = await chat.SendMessageAsync(id, request.Text, request.Attachments, cancellationToken).ConfigureAwait(false);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });
        app.MapPost("/api/sessions/{id}/messages/stream", async (string id, SendMessageRequest request, WebChatService chat, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!HasMessageInput(request))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Message text or at least one attachment is required.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!chat.HasSession(id))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await foreach (var streamEvent in chat.SendMessageStreamAsync(id, request.Text, request.Attachments, cancellationToken).ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        streamEvent,
                        WebUiNdjsonContext.Default.WebChatStreamEventDto,
                        cancellationToken)
                    .ConfigureAwait(false);
                await context.Response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        });

        return app;
    }

    private static bool HasMessageInput(SendMessageRequest request) =>
        !string.IsNullOrWhiteSpace(request.Text) || request.Attachments is { Count: > 0 };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "tau-session" : cleaned;
    }
}
