using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.WebUi;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.CodingAgent.Tests;

public class WebUiEndpointTests
{
    [Fact]
    public async Task StreamEndpoint_EmitsNdjsonAndPersistsSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
                "/api/sessions",
                JsonBody(
                    new CreateSessionRequest("Endpoint stream", "openai", "gpt-5.4"),
                    WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var attachment = new WebChatAttachmentDto(
            "att-1",
            "document",
            "notes.txt",
            "text/plain",
            5,
            "aGVsbG8=",
            "hello from endpoint");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session!.Id}/messages/stream")
        {
            Content = JsonBody(
                new SendMessageRequest("please inspect", [attachment]),
                WebUiEndpointJsonContext.Default.SendMessageRequest)
        };
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var events = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(line, WebUiEndpointJsonContext.Default.WebChatStreamEventDto))
            .Select(evt => Assert.IsType<WebChatStreamEventDto>(evt))
            .ToArray();

        Assert.Equal(["user", "text_delta", "done"], events.Select(evt => evt.Type).ToArray());
        Assert.Equal("stream ok", events.Single(evt => evt.Type == "text_delta").Text);
        var done = events.Single(evt => evt.Type == "done");
        Assert.True(done.Session?.Persisted);
        Assert.Equal(2, done.Session?.Messages.Count);

        var runner = Assert.Single(fixture.Runners);
        var content = Assert.Single(runner.ContentInputs);
        var prompt = Assert.IsType<TextContent>(content[0]).Text;
        Assert.Contains("<file name=\"notes.txt\" mimeType=\"text/plain\" size=\"5\">", prompt, StringComparison.Ordinal);
        Assert.Contains("hello from endpoint", prompt, StringComparison.Ordinal);

        var stored = Assert.Single(new WebChatStore(fixture.StorePath).Load());
        Assert.Equal(session.Id, stored.Id);
        Assert.Equal("stream ok", stored.Messages[1].Text);
        Assert.True(stored.Persisted);
    }

    [Fact]
    public async Task MessageEndpoints_ReturnBadRequestOrNotFoundBeforeRunnerInvocation()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var emptyMessage = await fixture.Client.PostAsync(
                "/api/sessions/missing/messages",
                JsonBody(
                    new SendMessageRequest("   "),
                    WebUiEndpointJsonContext.Default.SendMessageRequest));
        Assert.Equal(HttpStatusCode.BadRequest, emptyMessage.StatusCode);
        Assert.Equal(
            "Message text or at least one attachment is required.",
            JsonSerializer.Deserialize(
                await emptyMessage.Content.ReadAsStringAsync(),
                WebUiEndpointJsonContext.Default.String));

        var missingStream = await fixture.Client.PostAsync(
                "/api/sessions/missing/messages/stream",
                JsonBody(
                    new SendMessageRequest("hello"),
                    WebUiEndpointJsonContext.Default.SendMessageRequest));
        Assert.Equal(HttpStatusCode.NotFound, missingStream.StatusCode);
        Assert.Empty(fixture.Runners);
    }

    [Fact]
    public async Task UpdateEndpoint_RenamesSessionAndRestoresFromStore()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
                "/api/sessions",
                JsonBody(
                    new CreateSessionRequest("Original title", "openai", "gpt-5.4"),
                    WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var renamed = await fixture.Client.PutAsync(
            $"/api/sessions/{session!.Id}",
            JsonBody(
                new UpdateSessionSettingsRequest("Renamed title", session.Provider, session.Model),
                WebUiEndpointJsonContext.Default.UpdateSessionSettingsRequest));
        renamed.EnsureSuccessStatusCode();
        var updated = JsonSerializer.Deserialize(
            await renamed.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);

        Assert.Equal("Renamed title", updated?.Title);
        var stored = Assert.Single(new WebChatStore(fixture.StorePath).Load());
        Assert.Equal("Renamed title", stored.Title);

        var restored = new WebChatService(
            new WebChatStore(fixture.StorePath),
            (_, _, _) => new FakeCodingAgentRunner(StreamOk));
        Assert.Equal("Renamed title", restored.GetSession(session.Id)?.Title);

        var missing = await fixture.Client.PutAsync(
            "/api/sessions/missing",
            JsonBody(
                new UpdateSessionSettingsRequest("Missing", session.Provider, session.Model),
                WebUiEndpointJsonContext.Default.UpdateSessionSettingsRequest));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SessionLifecycleEndpoints_ExportImportAndDeleteSessions()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
                "/api/sessions",
                JsonBody(
                    new CreateSessionRequest("Lifecycle title", "openai", "gpt-5.4"),
                    WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var exported = await fixture.Client.GetAsync($"/api/sessions/{session!.Id}/export");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("application/json", exported.Content.Headers.ContentType?.MediaType);
        var exportedSession = JsonSerializer.Deserialize(
            await exported.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.Equal(session.Id, exportedSession?.Id);
        Assert.Equal("Lifecycle title", exportedSession?.Title);

        var imported = await fixture.Client.PostAsync(
            "/api/sessions/import",
            JsonBody(exportedSession!, WebUiEndpointJsonContext.Default.WebChatSessionDto));
        imported.EnsureSuccessStatusCode();
        var importedSession = JsonSerializer.Deserialize(
            await imported.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(importedSession);
        Assert.NotEqual(session.Id, importedSession!.Id);
        Assert.Equal("Lifecycle title", importedSession.Title);

        var deleted = await fixture.Client.DeleteAsync($"/api/sessions/{session.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var missing = await fixture.Client.GetAsync($"/api/sessions/{session.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var list = await fixture.Client.GetAsync("/api/sessions");
        list.EnsureSuccessStatusCode();
        var sessions = JsonSerializer.Deserialize(
            await list.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        var remaining = Assert.Single(Assert.IsType<WebChatSessionDto[]>(sessions));
        Assert.Equal(importedSession.Id, remaining.Id);
    }

    [Fact]
    public async Task ClearSessionMessagesEndpoint_RemovesMessagesAndKeepsSettings()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Clearable", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var send = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/messages",
            JsonBody(new SendMessageRequest("hello"), WebUiEndpointJsonContext.Default.SendMessageRequest));
        send.EnsureSuccessStatusCode();
        var afterSend = JsonSerializer.Deserialize(
            await send.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(afterSend);
        Assert.NotEmpty(afterSend!.Messages);

        var cleared = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/clear",
            content: new StringContent(string.Empty));
        cleared.EnsureSuccessStatusCode();
        var clearedSession = JsonSerializer.Deserialize(
            await cleared.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(clearedSession);
        Assert.Empty(clearedSession!.Messages);
        Assert.Equal("Clearable", clearedSession.Title);
        Assert.Equal("openai", clearedSession.Provider);
        Assert.Equal("gpt-5.4", clearedSession.Model);

        var missing = await fixture.Client.PostAsync(
            "/api/sessions/does-not-exist/clear",
            content: new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CloneSessionEndpoint_DuplicatesMessagesAndPrefixesTitle()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Source", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var source = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(source);

        var send = await fixture.Client.PostAsync(
            $"/api/sessions/{source!.Id}/messages",
            JsonBody(new SendMessageRequest("hello"), WebUiEndpointJsonContext.Default.SendMessageRequest));
        send.EnsureSuccessStatusCode();

        var cloned = await fixture.Client.PostAsync(
            $"/api/sessions/{source.Id}/clone",
            content: new StringContent(string.Empty));
        cloned.EnsureSuccessStatusCode();
        var clone = JsonSerializer.Deserialize(
            await cloned.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(clone);
        Assert.NotEqual(source.Id, clone!.Id);
        Assert.Equal("Copy of Source", clone.Title);
        Assert.Equal(source.Provider, clone.Provider);
        Assert.Equal(source.Model, clone.Model);
        // Source had at least the user message; the clone should mirror it.
        Assert.NotEmpty(clone.Messages);

        var missing = await fixture.Client.PostAsync(
            "/api/sessions/does-not-exist/clone",
            content: new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SearchSessionsEndpoint_MatchesByTitleCaseInsensitive()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Tauri rewrite plan", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Database migrations", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("tauri integration", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));

        var search = await fixture.Client.GetAsync("/api/sessions/search?q=tauri");
        search.EnsureSuccessStatusCode();
        var matches = JsonSerializer.Deserialize(
            await search.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        Assert.NotNull(matches);
        var titles = matches!.Select(m => m.Title).ToArray();
        Assert.Contains("Tauri rewrite plan", titles);
        Assert.Contains("tauri integration", titles);
        Assert.DoesNotContain("Database migrations", titles);
    }

    [Fact]
    public async Task SearchSessionsEndpoint_ReturnsEmptyArrayWhenNoMatch()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Only session", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));

        var search = await fixture.Client.GetAsync("/api/sessions/search?q=missing");
        search.EnsureSuccessStatusCode();
        var matches = JsonSerializer.Deserialize(
            await search.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        Assert.NotNull(matches);
        Assert.Empty(matches!);
    }

    [Fact]
    public async Task SearchSessionsEndpoint_RejectsEmptyQuery()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var search = await fixture.Client.GetAsync("/api/sessions/search?q=");
        Assert.Equal(HttpStatusCode.BadRequest, search.StatusCode);

        var noParam = await fixture.Client.GetAsync("/api/sessions/search");
        Assert.Equal(HttpStatusCode.BadRequest, noParam.StatusCode);
    }

    [Fact]
    public async Task ExportMarkdownEndpoint_RendersSessionAndRedactsSecrets()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Markdown me", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var send = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/messages",
            JsonBody(
                new SendMessageRequest("AKIAIOSFODNN7EXAMPLE belongs in the dust"),
                WebUiEndpointJsonContext.Default.SendMessageRequest));
        send.EnsureSuccessStatusCode();

        var exported = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export.md");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", exported.Content.Headers.ContentType?.MediaType);
        var markdown = await exported.Content.ReadAsStringAsync();
        Assert.Contains("# Markdown me", markdown, StringComparison.Ordinal);
        Assert.Contains("Provider: `openai`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", markdown, StringComparison.Ordinal);
        Assert.Contains("[redacted]", markdown, StringComparison.Ordinal);
        Assert.Contains("belongs in the dust", markdown, StringComparison.Ordinal);

        var missing = await fixture.Client.GetAsync("/api/sessions/does-not-exist/export.md");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ExportHtmlEndpoint_RendersSessionAndRedactsSecrets()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Export me", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var send = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/messages",
            JsonBody(
                new SendMessageRequest("AWS key AKIAIOSFODNN7EXAMPLE and a note"),
                WebUiEndpointJsonContext.Default.SendMessageRequest));
        send.EnsureSuccessStatusCode();

        var exported = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export.html");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("text/html", exported.Content.Headers.ContentType?.MediaType);
        var html = await exported.Content.ReadAsStringAsync();
        Assert.Contains("Export me", html, StringComparison.Ordinal);
        Assert.Contains("gpt-5.4", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", html, StringComparison.Ordinal);
        Assert.Contains("[redacted]", html, StringComparison.Ordinal);
        Assert.Contains("a note", html, StringComparison.Ordinal);

        var missing = await fixture.Client.GetAsync("/api/sessions/does-not-exist/export.html");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static StringContent JsonBody<T>(T value, JsonTypeInfo<T> jsonTypeInfo) =>
        new(JsonSerializer.Serialize(value, jsonTypeInfo), Encoding.UTF8, "application/json");

    private static async IAsyncEnumerable<AgentEvent> StreamOk(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var partial = new AssistantMessage([new TextContent("stream ok")]);
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "stream ok", partial));
        yield return new AgentEndEvent();
    }

    private sealed class WebUiEndpointFixture : IAsyncDisposable
    {
        private WebUiEndpointFixture(WebApplication app, HttpClient client, string storePath, List<FakeCodingAgentRunner> runners)
        {
            App = app;
            Client = client;
            StorePath = storePath;
            Runners = runners;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public string StorePath { get; }
        public List<FakeCodingAgentRunner> Runners { get; }

        public static async Task<WebUiEndpointFixture> StartAsync(
            Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
        {
            var storePath = Path.Combine(Path.GetTempPath(), $"tau-webui-endpoint-{Guid.NewGuid():N}.json");
            var runners = new List<FakeCodingAgentRunner>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new WebChatStore(storePath));
            builder.Services.AddSingleton<WebChatService>(sp => new WebChatService(
                sp.GetRequiredService<WebChatStore>(),
                (_, _, _) =>
                {
                    var runner = new FakeCodingAgentRunner(run);
                    runners.Add(runner);
                    return runner;
                }));

            var app = builder.Build();
            app.MapWebUiEndpoints();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = Assert.Single(addresses?.Addresses ?? []);
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new WebUiEndpointFixture(app, client, storePath, runners);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
            }
        }
    }
}

[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(UpdateSessionSettingsRequest))]
[JsonSerializable(typeof(WebChatSessionDto))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class WebUiEndpointJsonContext : JsonSerializerContext;
