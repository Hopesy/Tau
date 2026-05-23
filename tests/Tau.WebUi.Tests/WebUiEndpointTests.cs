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
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class WebUiEndpointJsonContext : JsonSerializerContext;
