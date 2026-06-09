using System.Text;
using System.Text.Json;
using Tau.Ai;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;
using Tau.WebUi.Ui;

namespace Tau.WebUi;

public static class WebUiApplication
{
    private const string JsonlContentType = "application/x-ndjson; charset=utf-8";
    private const string JsonlImportProblemTitle = "Invalid WebUi JSONL import";
    private const string CodingAgentJsonlPreviewProblemTitle = "Invalid CodingAgent JSONL preview";
    private const string CodingAgentJsonlImportProblemTitle = "Invalid CodingAgent JSONL import";
    private static readonly char[] PortableInvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

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
        app.MapGet("/api/sessions/{id}/artifacts", (string id, WebChatService chat, WebArtifactService artifacts) =>
        {
            return chat.HasSession(id) ? Results.Ok(artifacts.ListArtifacts(id)) : Results.NotFound();
        });
        app.MapGet("/api/sessions/{id}/artifacts/{fileName}", (string id, string fileName, WebChatService chat, WebArtifactService artifacts) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            var artifact = artifacts.GetArtifact(id, fileName);
            return artifact is null ? Results.NotFound() : Results.Ok(artifact);
        });
        app.MapPut("/api/sessions/{id}/artifacts/{fileName}", (string id, string fileName, UpsertWebArtifactRequest request, WebChatService chat, WebArtifactService artifacts) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(artifacts.UpsertArtifact(id, fileName, request));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
        app.MapDelete("/api/sessions/{id}/artifacts/{fileName}", (string id, string fileName, WebChatService chat, WebArtifactService artifacts) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            return artifacts.DeleteArtifact(id, fileName) ? Results.NoContent() : Results.NotFound();
        });
        app.MapPost("/api/sessions/{id}/runtime/messages", (string id, WebRuntimeMessageRequest request, WebChatService chat, WebArtifactService artifacts) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            return Results.Json(artifacts.HandleRuntimeMessage(id, request));
        });
        app.MapGet("/api/sessions/{id}/javascript-repl/next", async Task<IResult> (
            string id,
            int? timeoutMs,
            WebChatService chat,
            WebUiJavaScriptReplBridge replBridge,
            CancellationToken cancellationToken) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            var request = await replBridge.WaitForNextAsync(id, NormalizeReplPollTimeout(timeoutMs), cancellationToken).ConfigureAwait(false);
            return request is null ? Results.NoContent() : Results.Ok(request);
        });
        app.MapPost("/api/sessions/{id}/javascript-repl/{requestId}/result", (
            string id,
            string requestId,
            WebJavaScriptReplResultRequest request,
            WebChatService chat,
            WebUiJavaScriptReplBridge replBridge) =>
        {
            if (!chat.HasSession(id))
            {
                return Results.NotFound();
            }

            return replBridge.Complete(id, requestId, request)
                ? Results.Ok(new { success = true })
                : Results.NotFound();
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
        app.MapGet("/api/sessions/{id}/export.jsonl", (string id, WebChatService chat) =>
        {
            var session = chat.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
            var jsonl = WebChatJsonlExporter.Render(session, redactor);
            return Results.File(
                Encoding.UTF8.GetBytes(jsonl),
                JsonlContentType,
                $"{SafeFileName(session.Title)}.tau-webui-session.jsonl");
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
        app.MapPost("/api/sessions/import.jsonl", async Task<IResult> (HttpRequest request, WebChatService chat, CancellationToken cancellationToken) =>
        {
            if (!IsSupportedJsonlImportContentType(request.ContentType))
            {
                return JsonlProblem(
                    JsonlImportProblemTitle,
                    "unsupported_content_type",
                    $"Unsupported JSONL import content type '{request.ContentType}'. Use application/x-ndjson.",
                    StatusCodes.Status415UnsupportedMediaType);
            }

            try
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var jsonl = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
                var session = WebChatJsonlImporter.Parse(jsonl, redactor);
                return Results.Ok(chat.ImportSession(session));
            }
            catch (WebChatJsonlImportException ex)
            {
                return JsonlProblem(JsonlImportProblemTitle, ex.Code, ex.Message, StatusCodes.Status400BadRequest, ex.LineNumber);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
        app.MapPost("/api/sessions/import.coding-agent-jsonl/preview", async Task<IResult> (
            HttpRequest request,
            string? search,
            bool? currentBranchOnly,
            WebChatService chat,
            CancellationToken cancellationToken) =>
        {
            if (!IsSupportedJsonlImportContentType(request.ContentType))
            {
                return JsonlProblem(
                    CodingAgentJsonlPreviewProblemTitle,
                    "unsupported_content_type",
                    $"Unsupported CodingAgent JSONL preview content type '{request.ContentType}'. Use application/x-ndjson.",
                    StatusCodes.Status415UnsupportedMediaType);
            }

            try
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var jsonl = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
                var options = new CodingAgentJsonlPreviewOptions(search, currentBranchOnly ?? false);
                return Results.Ok(chat.PreviewCodingAgentJsonlSession(jsonl, options: options, redactor: redactor));
            }
            catch (CodingAgentJsonlPreviewException ex)
            {
                return JsonlProblem(
                    CodingAgentJsonlPreviewProblemTitle,
                    ex.Code,
                    ex.Message,
                    StatusCodes.Status400BadRequest,
                    ex.LineNumber);
            }
        });
        app.MapPost("/api/sessions/import.coding-agent-jsonl", async Task<IResult> (
            HttpRequest request,
            bool? currentBranchOnly,
            WebChatService chat,
            CancellationToken cancellationToken) =>
        {
            if (!IsSupportedJsonlImportContentType(request.ContentType))
            {
                return JsonlProblem(
                    CodingAgentJsonlImportProblemTitle,
                    "unsupported_content_type",
                    $"Unsupported CodingAgent JSONL import content type '{request.ContentType}'. Use application/x-ndjson.",
                    StatusCodes.Status415UnsupportedMediaType);
            }

            try
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var jsonl = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var redactor = TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable);
                var options = new CodingAgentJsonlPreviewOptions(Search: null, CurrentBranchOnly: currentBranchOnly ?? false);
                return Results.Ok(chat.ImportCodingAgentJsonlSession(jsonl, options: options, redactor: redactor));
            }
            catch (CodingAgentJsonlPreviewException ex)
            {
                return JsonlProblem(
                    CodingAgentJsonlImportProblemTitle,
                    ex.Code,
                    ex.Message,
                    StatusCodes.Status400BadRequest,
                    ex.LineNumber);
            }
        });
        app.MapDelete("/api/sessions/{id}", (string id, WebChatService chat, WebArtifactService artifacts, WebUiJavaScriptReplBridge replBridge) =>
        {
            if (!chat.DeleteSession(id))
            {
                return Results.NotFound();
            }

            artifacts.DeleteSession(id);
            replBridge.CancelSession(id, "JavaScript REPL request was cancelled because the WebUi session was deleted.");
            return Results.NoContent();
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
        app.MapPost("/api/sessions/{id}/clone", (string id, WebChatService chat) =>
        {
            var session = chat.CloneSession(id);
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

    private static TimeSpan NormalizeReplPollTimeout(int? timeoutMs)
    {
        if (timeoutMs is not { } value)
        {
            return WebUiJavaScriptReplBridge.DefaultPollTimeout;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(value, 100, 60000));
    }

    private static IResult JsonlProblem(string title, string code, string detail, int statusCode, int? lineNumber = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code
        };
        if (lineNumber is not null)
        {
            extensions["line"] = lineNumber.Value;
        }

        return Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            extensions: extensions);
    }

    private static bool IsSupportedJsonlImportContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/jsonl", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/json-lines", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(ch => char.IsControl(ch) || invalid.Contains(ch) || PortableInvalidFileNameChars.Contains(ch) ? '-' : ch)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "tau-session" : cleaned;
    }
}
