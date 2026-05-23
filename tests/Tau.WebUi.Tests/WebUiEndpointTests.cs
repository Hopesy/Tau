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
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebUiEndpointTests
{
    [Fact]
    public async Task ExportJsonlEndpoint_ExportsSessionAndPreservesExistingJsonAndHtmlExports()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Endpoint JSONL", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var sent = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/messages",
            JsonBody(
                new SendMessageRequest("hello jsonl"),
                WebUiEndpointJsonContext.Default.SendMessageRequest));
        sent.EnsureSuccessStatusCode();

        var jsonExport = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export");
        jsonExport.EnsureSuccessStatusCode();
        Assert.Equal("application/json", jsonExport.Content.Headers.ContentType?.MediaType);
        var exportedSession = JsonSerializer.Deserialize(
            await jsonExport.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.Equal(session.Id, exportedSession?.Id);
        Assert.Equal(2, exportedSession?.Messages.Count);

        var htmlExport = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export.html");
        htmlExport.EnsureSuccessStatusCode();
        Assert.Equal("text/html", htmlExport.Content.Headers.ContentType?.MediaType);
        var html = await htmlExport.Content.ReadAsStringAsync();
        Assert.Contains("Endpoint JSONL", html, StringComparison.Ordinal);
        Assert.Contains("hello jsonl", html, StringComparison.Ordinal);
        Assert.Contains("stream ok", html, StringComparison.Ordinal);

        var jsonlExport = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export.jsonl");
        jsonlExport.EnsureSuccessStatusCode();
        Assert.Equal("application/x-ndjson", jsonlExport.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", jsonlExport.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", jsonlExport.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(
            "Endpoint JSONL.tau-webui-session.jsonl",
            jsonlExport.Content.Headers.ContentDisposition?.FileNameStar);
        var jsonl = await jsonlExport.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\r\n", jsonl, StringComparison.Ordinal);
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("session", header.RootElement.GetProperty("type").GetString());
        Assert.Equal(session.Id, header.RootElement.GetProperty("id").GetString());
        Assert.Equal("Endpoint JSONL", header.RootElement.GetProperty("title").GetString());

        using var userMessage = JsonDocument.Parse(lines[1]);
        Assert.Equal("message", userMessage.RootElement.GetProperty("type").GetString());
        Assert.Equal("message-000001", userMessage.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, userMessage.RootElement.GetProperty("parentId").ValueKind);
        Assert.Equal("user", userMessage.RootElement.GetProperty("role").GetString());
        Assert.Equal("hello jsonl", userMessage.RootElement.GetProperty("text").GetString());

        using var assistantMessage = JsonDocument.Parse(lines[2]);
        Assert.Equal("message-000002", assistantMessage.RootElement.GetProperty("id").GetString());
        Assert.Equal("message-000001", assistantMessage.RootElement.GetProperty("parentId").GetString());
        Assert.Equal("assistant", assistantMessage.RootElement.GetProperty("role").GetString());
        Assert.Equal("stream ok", assistantMessage.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExportJsonlEndpoint_ReturnsNotFoundForMissingSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var missing = await fixture.Client.GetAsync("/api/sessions/does-not-exist/export.jsonl");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ExportJsonlEndpoint_SanitizesDownloadFileName()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Bad:Name/JSONL*?", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var jsonlExport = await fixture.Client.GetAsync($"/api/sessions/{session!.Id}/export.jsonl");

        jsonlExport.EnsureSuccessStatusCode();
        Assert.Equal("application/x-ndjson", jsonlExport.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "Bad-Name-JSONL--.tau-webui-session.jsonl",
            jsonlExport.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task ImportJsonlEndpoint_ImportsExportedLinearTranscript()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Endpoint JSONL Import", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var sent = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/messages",
            JsonBody(
                new SendMessageRequest("roundtrip me"),
                WebUiEndpointJsonContext.Default.SendMessageRequest));
        sent.EnsureSuccessStatusCode();
        var exported = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/export.jsonl");
        exported.EnsureSuccessStatusCode();
        var jsonl = await exported.Content.ReadAsStringAsync();

        var importedResponse = await fixture.Client.PostAsync(
            "/api/sessions/import.jsonl",
            new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));
        importedResponse.EnsureSuccessStatusCode();
        var imported = JsonSerializer.Deserialize(
            await importedResponse.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);

        Assert.NotNull(imported);
        Assert.NotEqual(session.Id, imported!.Id);
        Assert.Equal("Endpoint JSONL Import", imported.Title);
        Assert.Equal("openai", imported.Provider);
        Assert.Equal("gpt-5.4", imported.Model);
        Assert.True(imported.Persisted);
        Assert.Equal(2, imported.Messages.Count);
        Assert.Equal("user", imported.Messages[0].Role);
        Assert.Equal("roundtrip me", imported.Messages[0].Text);
        Assert.Equal("assistant", imported.Messages[1].Role);
        Assert.Equal("stream ok", imported.Messages[1].Text);

        var fetched = await fixture.Client.GetAsync($"/api/sessions/{imported.Id}");
        fetched.EnsureSuccessStatusCode();
        var fetchedSession = JsonSerializer.Deserialize(
            await fetched.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.Equal(imported.Id, fetchedSession?.Id);
        Assert.Equal("roundtrip me", fetchedSession?.Messages[0].Text);
    }

    [Theory]
    [InlineData("{not-json}\n", "invalid_json", 1, "not valid JSON")]
    [InlineData("{\"type\":\"message\",\"id\":\"message-000001\",\"parentId\":null,\"timestamp\":\"2026-05-23T01:03:00+00:00\",\"role\":\"user\",\"text\":\"hello\"}\n", "missing_session_header", 1, "First JSONL line must be a session header")]
    public async Task ImportJsonlEndpoint_ReturnsProblemDetailsForMalformedJsonl(
        string jsonl,
        string expectedCode,
        int expectedLineNumber,
        string expectedMessage)
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.jsonl",
            new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expectedCode,
            expectedLineNumber,
            expectedMessage);
    }

    [Fact]
    public async Task ImportJsonlEndpoint_RejectsUnsupportedContentTypeWithProblemDetails()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.jsonl",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_content_type",
            null,
            "Use application/x-ndjson");
    }

    [Fact]
    public async Task CodingAgentJsonlPreviewEndpoint_ReturnsHeaderAndMessageTimelineWithoutPersisting()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl/preview",
            new StringContent(ValidCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var preview = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlSessionPreviewDto);
        Assert.NotNull(preview);
        Assert.Equal("coding-session-1", preview!.SessionId);
        Assert.Equal(3, preview.Version);
        Assert.Equal("C:\\Users\\zhouh\\Desktop\\Tau", preview.Cwd);
        Assert.Equal("parent-session.jsonl", preview.ParentSession);
        Assert.Equal(4, preview.EntryCount);
        Assert.Equal(3, preview.MessageCount);
        Assert.Equal("user", preview.Messages[0].Role);
        Assert.Equal("hello coding agent", preview.Messages[0].TextPreview);
        Assert.Equal("entry-model", preview.Messages[1].ParentEntryId);
        Assert.True(preview.Messages[1].HasThinking);
        Assert.Equal(1, preview.Messages[1].ToolCallCount);
        Assert.Equal("tool-1", preview.Messages[2].ToolCallId);
        Assert.True(preview.Messages[2].IsError);

        var sessionsResponse = await fixture.Client.GetAsync("/api/sessions");
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = JsonSerializer.Deserialize(
            await sessionsResponse.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        Assert.Empty(sessions ?? []);
    }

    [Fact]
    public async Task CodingAgentJsonlPreviewEndpoint_ReturnsProblemDetailsForMalformedJsonl()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var jsonl = ValidCodingAgentHeader() + "{not-json}\n";

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl/preview",
            new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid_json",
            2,
            "line 2 is not valid JSON",
            "Invalid CodingAgent JSONL preview");
    }

    private static StringContent JsonBody<T>(T value, JsonTypeInfo<T> jsonTypeInfo) =>
        new(JsonSerializer.Serialize(value, jsonTypeInfo), Encoding.UTF8, "application/json");

    private static async Task AssertJsonlProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        int? expectedLineNumber,
        string expectedDetail,
        string expectedTitle = "Invalid WebUi JSONL import")
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedTitle, problem.RootElement.GetProperty("title").GetString());
        Assert.Equal((int)expectedStatus, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Contains(expectedDetail, problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        if (expectedLineNumber is null)
        {
            Assert.False(problem.RootElement.TryGetProperty("line", out _));
        }
        else
        {
            Assert.Equal(expectedLineNumber.Value, problem.RootElement.GetProperty("line").GetInt32());
        }
    }

    private static async IAsyncEnumerable<AgentEvent> StreamOk(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var partial = new AssistantMessage([new TextContent("stream ok")]);
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "stream ok", partial));
        yield return new AgentEndEvent();
    }

    private static string ValidCodingAgentJsonl() =>
        ValidCodingAgentHeader() +
        "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello coding agent\"}]}}\n" +
        "{\"type\":\"model_change\",\"id\":\"entry-model\",\"parentId\":\"entry-user\",\"timestamp\":\"2026-05-23T02:01:30+00:00\",\"provider\":\"openai\",\"model\":\"gpt-5.4\"}\n" +
        "{\"type\":\"message\",\"id\":\"entry-assistant\",\"parentId\":\"entry-model\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"plan\"},{\"type\":\"toolCall\",\"id\":\"tool-1\",\"name\":\"read\",\"arguments\":\"{}\"},{\"type\":\"text\",\"text\":\"done\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-tool\",\"parentId\":\"entry-assistant\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"tool-1\",\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"not found\"}]}}\n";

    private static string ValidCodingAgentHeader() =>
        "{\"type\":\"session\",\"version\":3,\"id\":\"coding-session-1\",\"timestamp\":\"2026-05-23T02:00:00+00:00\",\"cwd\":\"C:\\\\Users\\\\zhouh\\\\Desktop\\\\Tau\",\"parentSession\":\"parent-session.jsonl\"}\n";

    private sealed class WebUiEndpointFixture : IAsyncDisposable
    {
        private WebUiEndpointFixture(WebApplication app, HttpClient client, string storePath)
        {
            App = app;
            Client = client;
            StorePath = storePath;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public string StorePath { get; }

        public static async Task<WebUiEndpointFixture> StartAsync(
            Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
        {
            var storePath = Path.Combine(Path.GetTempPath(), $"tau-webui-endpoint-{Guid.NewGuid():N}.json");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new WebChatStore(storePath));
            builder.Services.AddSingleton<WebChatService>(sp => new WebChatService(
                sp.GetRequiredService<WebChatStore>(),
                (_, _, _) => new FakeWebUiRunner(run)));

            var app = builder.Build();
            app.MapWebUiEndpoints();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = Assert.Single(addresses?.Addresses ?? []);
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new WebUiEndpointFixture(app, client, storePath);
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
[JsonSerializable(typeof(WebChatSessionDto))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(CodingAgentJsonlSessionPreviewDto))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class WebUiEndpointJsonContext : JsonSerializerContext;
