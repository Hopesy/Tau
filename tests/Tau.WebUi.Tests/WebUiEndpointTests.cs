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
using Tau.Ai.Observability;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebUiEndpointTests
{
    [Fact]
    public async Task AuthEndpoint_ReturnsStatusAndLogsAuthStatus()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENAI_API_KEY", "secret-openai-key");
        scope.Set("TAU_AUTH_FILE", Path.Combine(Path.GetTempPath(), $"missing-auth-{Guid.NewGuid():N}.json"));
        var logSink = new CapturingLogSink();
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk, logSink);

        var response = await fixture.Client.GetAsync("/api/auth/openai");

        response.EnsureSuccessStatusCode();
        var status = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebUiAuthStatusDto);
        Assert.NotNull(status);
        Assert.Equal("openai", status!.Provider);
        Assert.True(status.IsConfigured);
        Assert.Equal("environment", status.Source);
        Assert.False(status.UsesOAuth);
        Assert.False(status.CanLogin);
        Assert.DoesNotContain("secret-openai-key", status.Message, StringComparison.Ordinal);

        var evt = Assert.Single(logSink.Events);
        Assert.Equal("auth", evt.Category);
        Assert.Equal("status.checked", evt.Event);
        Assert.Equal("openai", evt.Fields["provider"]);
        Assert.Equal("true", evt.Fields["configured"]);
        Assert.Equal("environment", evt.Fields["source"]);
        Assert.Equal("false", evt.Fields["usesOAuth"]);
        Assert.Equal("false", evt.Fields["canLogin"]);
        Assert.False(evt.Fields.ContainsKey("message"));
        var fields = string.Join(
            Environment.NewLine,
            evt.Fields.Select(field => $"{field.Key}={field.Value}"));
        Assert.DoesNotContain("secret-openai-key", fields, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStreamEndpoint_DefaultRunnerLogsAgentRunEvents()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var modelsPath = Path.Combine(Path.GetTempPath(), $"tau-webui-models-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            modelsPath,
            """
            {
              "providers": {
                "tau-webui-log-sink-test": {
                  "models": [
                    {
                      "id": "local-only",
                      "name": "Local Only",
                      "api": "tau-test-unregistered-api",
                      "baseUrl": "http://127.0.0.1:9"
                    }
                  ]
                }
              }
            }
            """);
        scope.Set("TAU_MODELS_FILE", modelsPath);
        scope.Set("TAU_AUTH_FILE", Path.Combine(Path.GetTempPath(), $"missing-auth-{Guid.NewGuid():N}.json"));
        scope.Set("TAU_PROVIDER", null);
        scope.Set("TAU_MODEL", null);
        scope.Set("OPENAI_API_KEY", null);
        var logSink = new CapturingLogSink();

        try
        {
            await using var fixture = await WebUiEndpointFixture.StartDefaultRunnerAsync(logSink);
            var created = await fixture.Client.PostAsync(
                "/api/sessions",
                JsonBody(
                    new CreateSessionRequest("Runtime log", "tau-webui-log-sink-test", "local-only"),
                    WebUiEndpointJsonContext.Default.CreateSessionRequest));
            created.EnsureSuccessStatusCode();
            var session = JsonSerializer.Deserialize(
                await created.Content.ReadAsStringAsync(),
                WebUiEndpointJsonContext.Default.WebChatSessionDto);
            Assert.NotNull(session);

            var streamed = await fixture.Client.PostAsync(
                $"/api/sessions/{session!.Id}/messages/stream",
                JsonBody(
                    new SendMessageRequest("hello runtime"),
                    WebUiEndpointJsonContext.Default.SendMessageRequest));

            streamed.EnsureSuccessStatusCode();
            Assert.Equal("application/x-ndjson", streamed.Content.Headers.ContentType?.MediaType);
            var events = (await streamed.Content.ReadAsStringAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize(line, WebUiEndpointJsonContext.Default.WebChatStreamEventDto))
                .Where(evt => evt is not null)
                .Select(evt => evt!)
                .ToArray();

            Assert.Contains(events, evt => evt.Type == "user" && evt.Text == "hello runtime");
            var streamError = Assert.Single(events, evt => evt.Type == "error");
            Assert.Contains("No provider registered for API 'tau-test-unregistered-api'", streamError.Error, StringComparison.Ordinal);

            var runStart = Assert.Single(logSink.Events, evt => evt.Category == "agent" && evt.Event == "run.start");
            Assert.Equal("tau-webui-log-sink-test", runStart.Fields["provider"]);
            Assert.Equal("local-only", runStart.Fields["model"]);
            Assert.True(long.TryParse(runStart.Fields["inputBytes"], out var inputBytes));
            Assert.True(inputBytes > 0);
            Assert.Equal(session.Id, runStart.Fields["sessionId"]);
            Assert.False(string.IsNullOrWhiteSpace(runStart.Fields["messageId"]));
            Assert.False(string.IsNullOrWhiteSpace(runStart.Fields["correlationId"]));

            var runError = Assert.Single(logSink.Events, evt => evt.Category == "agent" && evt.Event == "run.error");
            Assert.Equal("tau-webui-log-sink-test", runError.Fields["provider"]);
            Assert.Equal("local-only", runError.Fields["model"]);
            Assert.Equal("KeyNotFoundException", runError.Fields["error"]);
            Assert.Contains("No provider registered for API 'tau-test-unregistered-api'", runError.Fields["message"], StringComparison.Ordinal);
            Assert.Equal(runStart.Fields["correlationId"], runError.Fields["correlationId"]);
            Assert.Equal(session.Id, runError.Fields["sessionId"]);
            Assert.Equal(runStart.Fields["messageId"], runError.Fields["messageId"]);
            Assert.DoesNotContain(logSink.Events, evt => evt.Category == "agent" && evt.Event == "run.end");
        }
        finally
        {
            if (File.Exists(modelsPath))
            {
                File.Delete(modelsPath);
            }
        }
    }

    [Fact]
    public async Task ArtifactEndpoints_PersistArtifactsAndHandleRuntimeMessages()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Artifacts", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var upsert = await fixture.Client.PutAsync(
            $"/api/sessions/{session!.Id}/artifacts/index.html",
            JsonBody(
                new UpsertWebArtifactRequest("<h1>Hello artifact</h1>", "text/html"),
                WebUiEndpointJsonContext.Default.UpsertWebArtifactRequest));

        upsert.EnsureSuccessStatusCode();
        var artifact = JsonSerializer.Deserialize(
            await upsert.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebArtifactDto);
        Assert.NotNull(artifact);
        Assert.Equal("index.html", artifact!.FileName);
        Assert.Equal("text/html", artifact.MimeType);
        Assert.Equal("<h1>Hello artifact</h1>", artifact.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(artifact.Content), artifact.Size);

        var list = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/artifacts");
        list.EnsureSuccessStatusCode();
        var summaries = JsonSerializer.Deserialize(
            await list.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebArtifactSummaryDtoArray);
        var summary = Assert.Single(summaries!);
        Assert.Equal("index.html", summary.FileName);
        Assert.Equal("text/html", summary.MimeType);

        var get = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/artifacts/index.html");
        get.EnsureSuccessStatusCode();
        var fetched = JsonSerializer.Deserialize(
            await get.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebArtifactDto);
        Assert.Equal("<h1>Hello artifact</h1>", fetched?.Content);

        var runtimeCreate = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-1",
                    SandboxId: "sandbox-1",
                    Action: "createOrUpdate",
                    Filename: "data.json",
                    Content: "{\"ok\":true}",
                    MimeType: "application/json"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeCreate.EnsureSuccessStatusCode();
        using (var createdRuntime = JsonDocument.Parse(await runtimeCreate.Content.ReadAsStringAsync()))
        {
            Assert.Equal("runtime-response", createdRuntime.RootElement.GetProperty("type").GetString());
            Assert.Equal("msg-1", createdRuntime.RootElement.GetProperty("messageId").GetString());
            Assert.Equal("sandbox-1", createdRuntime.RootElement.GetProperty("sandboxId").GetString());
            Assert.True(createdRuntime.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("data.json", createdRuntime.RootElement.GetProperty("result").GetString());
        }

        var runtimeList = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-2",
                    SandboxId: "sandbox-1",
                    Action: "list"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeList.EnsureSuccessStatusCode();
        using (var listedRuntime = JsonDocument.Parse(await runtimeList.Content.ReadAsStringAsync()))
        {
            var files = listedRuntime.RootElement.GetProperty("result")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            Assert.Contains("data.json", files);
            Assert.Contains("index.html", files);
        }

        var runtimeGet = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-3",
                    SandboxId: "sandbox-1",
                    Action: "get",
                    Filename: "data.json"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeGet.EnsureSuccessStatusCode();
        using (var fetchedRuntime = JsonDocument.Parse(await runtimeGet.Content.ReadAsStringAsync()))
        {
            Assert.True(fetchedRuntime.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("{\"ok\":true}", fetchedRuntime.RootElement.GetProperty("result").GetString());
        }

        var runtimeFile = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "file-returned",
                    MessageId: "msg-4",
                    SandboxId: "sandbox-1",
                    Filename: "chart.csv",
                    Content: "x,y\n1,2",
                    MimeType: "text/csv"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeFile.EnsureSuccessStatusCode();
        using (var returnedFile = JsonDocument.Parse(await runtimeFile.Content.ReadAsStringAsync()))
        {
            Assert.Equal("runtime-response", returnedFile.RootElement.GetProperty("type").GetString());
            Assert.Equal("msg-4", returnedFile.RootElement.GetProperty("messageId").GetString());
            Assert.True(returnedFile.RootElement.GetProperty("success").GetBoolean());
            var result = returnedFile.RootElement.GetProperty("result");
            Assert.Equal("chart.csv", result.GetProperty("fileName").GetString());
            Assert.Equal("text/csv", result.GetProperty("mimeType").GetString());
        }

        var persisted = await File.ReadAllTextAsync(fixture.ArtifactStorePath);
        Assert.Contains(session.Id, persisted, StringComparison.Ordinal);
        Assert.Contains("data.json", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("chart.csv", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactEndpoints_HandleUpstreamArtifactOperationSemantics()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Artifact operations", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var runtimeCreate = await fixture.Client.PostAsync(
            $"/api/sessions/{session!.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-create",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "create",
                    Filename: "report.html",
                    Content: "alpha old old",
                    MimeType: "text/html"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeCreate.EnsureSuccessStatusCode();
        using (var createResponse = JsonDocument.Parse(await runtimeCreate.Content.ReadAsStringAsync()))
        {
            Assert.True(createResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("report.html", createResponse.RootElement.GetProperty("result").GetString());
        }

        var duplicateCreate = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-duplicate",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "create",
                    Filename: "report.html",
                    Content: "overwrite"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        duplicateCreate.EnsureSuccessStatusCode();
        using (var duplicateResponse = JsonDocument.Parse(await duplicateCreate.Content.ReadAsStringAsync()))
        {
            Assert.False(duplicateResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("already exists", duplicateResponse.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        var runtimeUpdate = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-update",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "update",
                    Filename: "report.html",
                    OldString: "old",
                    NewString: "new"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeUpdate.EnsureSuccessStatusCode();
        using (var updateResponse = JsonDocument.Parse(await runtimeUpdate.Content.ReadAsStringAsync()))
        {
            Assert.True(updateResponse.RootElement.GetProperty("success").GetBoolean());
        }

        var afterUpdate = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/artifacts/report.html");
        afterUpdate.EnsureSuccessStatusCode();
        var updatedArtifact = JsonSerializer.Deserialize(
            await afterUpdate.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebArtifactDto);
        Assert.Equal("alpha new old", updatedArtifact?.Content);

        var missingStringUpdate = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-missing-string",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "update",
                    Filename: "report.html",
                    OldString: "absent",
                    NewString: "value"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        missingStringUpdate.EnsureSuccessStatusCode();
        using (var missingStringResponse = JsonDocument.Parse(await missingStringUpdate.Content.ReadAsStringAsync()))
        {
            Assert.False(missingStringResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("String not found in file", missingStringResponse.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Contains("alpha new old", missingStringResponse.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        var runtimeRewrite = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-rewrite",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "rewrite",
                    Filename: "report.html",
                    Content: "<h1>rewritten</h1>"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeRewrite.EnsureSuccessStatusCode();
        using (var rewriteResponse = JsonDocument.Parse(await runtimeRewrite.Content.ReadAsStringAsync()))
        {
            Assert.True(rewriteResponse.RootElement.GetProperty("success").GetBoolean());
        }

        var runtimeConsole = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "console",
                    MessageId: "msg-console",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Method: "log",
                    Text: "boot ok"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeConsole.EnsureSuccessStatusCode();

        var runtimeLogs = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-logs",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "htmlArtifactLogs",
                    Filename: "report.html"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeLogs.EnsureSuccessStatusCode();
        using (var logsResponse = JsonDocument.Parse(await runtimeLogs.Content.ReadAsStringAsync()))
        {
            Assert.True(logsResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("[log] boot ok", logsResponse.RootElement.GetProperty("result").GetString(), StringComparison.Ordinal);
        }

        var runtimeDelete = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/runtime/messages",
            JsonBody(
                new WebRuntimeMessageRequest(
                    "artifact-operation",
                    MessageId: "msg-delete",
                    SandboxId: $"artifact-{session.Id}-report.html",
                    Action: "delete",
                    Filename: "report.html"),
                WebUiEndpointJsonContext.Default.WebRuntimeMessageRequest));
        runtimeDelete.EnsureSuccessStatusCode();
        using (var deleteResponse = JsonDocument.Parse(await runtimeDelete.Content.ReadAsStringAsync()))
        {
            Assert.True(deleteResponse.RootElement.GetProperty("success").GetBoolean());
        }

        var deleted = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/artifacts/report.html");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task ArtifactEndpoints_RejectUnsafeNamesAndDeleteArtifactsWithSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Artifact cleanup", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);

        var rejected = await fixture.Client.PutAsync(
            $"/api/sessions/{session!.Id}/artifacts/bad:name.txt",
            JsonBody(
                new UpsertWebArtifactRequest("unsafe"),
                WebUiEndpointJsonContext.Default.UpsertWebArtifactRequest));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("not portable", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var upsert = await fixture.Client.PutAsync(
            $"/api/sessions/{session.Id}/artifacts/notes.txt",
            JsonBody(
                new UpsertWebArtifactRequest("keep me", "text/plain"),
                WebUiEndpointJsonContext.Default.UpsertWebArtifactRequest));
        upsert.EnsureSuccessStatusCode();

        var deleted = await fixture.Client.DeleteAsync($"/api/sessions/{session.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var missingArtifacts = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/artifacts");
        Assert.Equal(HttpStatusCode.NotFound, missingArtifacts.StatusCode);
        if (File.Exists(fixture.ArtifactStorePath))
        {
            Assert.DoesNotContain("notes.txt", await File.ReadAllTextAsync(fixture.ArtifactStorePath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task JavaScriptReplBridgeEndpoints_LongPollAndCompletePendingRequest()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("JavaScript bridge", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(session);
        var bridge = fixture.App.Services.GetRequiredService<WebUiJavaScriptReplBridge>();

        var execution = bridge.ExecuteAsync(session!.Id, "call-repl", "Calculating sum", "return 2 + 3");
        var next = await fixture.Client.GetAsync($"/api/sessions/{session.Id}/javascript-repl/next?timeoutMs=1000");

        next.EnsureSuccessStatusCode();
        var request = JsonSerializer.Deserialize(
            await next.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebJavaScriptReplRequestDto);
        Assert.NotNull(request);
        Assert.Equal("call-repl", request!.ToolCallId);
        Assert.Equal("Calculating sum", request.Title);
        Assert.Equal("return 2 + 3", request.Code);

        var completed = await fixture.Client.PostAsync(
            $"/api/sessions/{session.Id}/javascript-repl/{request.Id}/result",
            JsonBody(
                new WebJavaScriptReplResultRequest(
                    Output: "=> 5",
                    Files:
                    [
                        new WebJavaScriptReplFileDto(
                            "sum.json",
                            "application/json",
                            9,
                            Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sum":5}""")))
                    ]),
                WebUiEndpointJsonContext.Default.WebJavaScriptReplResultRequest));
        completed.EnsureSuccessStatusCode();

        var result = await execution;

        Assert.False(result.IsError);
        Assert.Equal("=> 5", result.Output);
        var file = Assert.Single(result.Files);
        Assert.Equal("sum.json", file.FileName);
        Assert.Equal("application/json", file.MimeType);
        Assert.Equal(9, file.Size);
    }

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
    public async Task CodingAgentJsonlPreviewEndpoint_RejectsUnsupportedContentTypeWithProblemDetails()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl/preview",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_content_type",
            null,
            "Use application/x-ndjson",
            expectedTitle: "Invalid CodingAgent JSONL preview");
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_RejectsUnsupportedContentTypeWithProblemDetails()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_content_type",
            null,
            "Use application/x-ndjson",
            expectedTitle: "Invalid CodingAgent JSONL import");
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
        Assert.Null(preview.Filter.Search);
        Assert.False(preview.Filter.CurrentBranchOnly);
        Assert.Equal(3, preview.Filter.TotalMessageCount);
        Assert.Equal(3, preview.Filter.MatchedMessageCount);
        Assert.Equal(["entry-user", "entry-assistant", "entry-tool"], preview.Filter.MatchedEntryIds);
        Assert.Equal("entry-tool", preview.Tree.LeafEntryId);
        Assert.Equal(1, preview.Tree.RootEntryCount);
        Assert.Equal(0, preview.Tree.BranchPointCount);
        Assert.Equal(4, preview.Tree.BranchEntryCount);
        Assert.Equal(3, preview.Tree.BranchMessageCount);
        Assert.Equal(0, preview.Tree.LabelCount);
        Assert.Equal(3, preview.Tree.EntryTypes["message"]);
        Assert.Equal(1, preview.Tree.EntryTypes["model_change"]);
        Assert.Equal(["entry-user", "entry-model", "entry-assistant", "entry-tool"], preview.Tree.CurrentBranchEntryIds);
        Assert.Equal("entry-tool", Assert.Single(preview.Tree.Entries, entry => entry.IsCurrentLeaf).EntryId);
        Assert.False(preview.Audit.IsBranched);
        Assert.True(preview.Audit.WillImportTimelineMessagesOnly);
        Assert.False(preview.Audit.WillImportCurrentBranchOnly);
        Assert.Equal(3, preview.Audit.ImportedMessageCount);
        Assert.Equal(1, preview.Audit.NonImportedEntryCount);
        Assert.Equal(3, preview.Audit.CurrentBranchMessageCount);
        Assert.Equal(0, preview.Audit.OffBranchMessageCount);
        Assert.Equal(["entry-user", "entry-model", "entry-assistant", "entry-tool"], preview.Audit.CurrentBranchTimeline.Select(entry => entry.EntryId).ToArray());
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "non_message_entries_not_imported_as_messages");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "webchat_import_is_linearized");
        Assert.Equal("conservative-timeline-linearized", preview.ImportStrategy.Strategy);
        Assert.Equal("entry-tool", preview.ImportStrategy.SourceLeafEntryId);
        Assert.False(preview.ImportStrategy.CurrentBranchOnly);
        Assert.True(preview.ImportStrategy.ImportsTimelineMessagesOnly);
        Assert.False(preview.ImportStrategy.PersistsBranchTree);
        Assert.Contains("webchat_import_is_linearized", preview.ImportStrategy.WarningCodes);
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
    public async Task CodingAgentJsonlPreviewEndpoint_CanFilterCurrentBranchAndSearchWithoutPersisting()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl/preview?currentBranchOnly=true&search=after",
            new StringContent(BranchedCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var preview = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlSessionPreviewDto);

        Assert.NotNull(preview);
        Assert.Equal(5, preview!.MessageCount);
        Assert.Equal("after", preview.Filter.Search);
        Assert.True(preview.Filter.CurrentBranchOnly);
        Assert.Equal(5, preview.Filter.TotalMessageCount);
        Assert.Equal(1, preview.Filter.MatchedMessageCount);
        Assert.Equal(["entry-after-summary"], preview.Filter.MatchedEntryIds);
        Assert.Equal("entry-label", preview.Tree.LeafEntryId);
        Assert.Equal(5, preview.Tree.BranchEntryCount);
        Assert.Equal(3, preview.Audit.CurrentBranchMessageCount);
        Assert.Equal(2, preview.Audit.OffBranchMessageCount);
        Assert.Equal("conservative-current-branch-linearized", preview.ImportStrategy.Strategy);
        Assert.Equal("entry-label", preview.ImportStrategy.SourceLeafEntryId);
        Assert.True(preview.ImportStrategy.CurrentBranchOnly);
        Assert.DoesNotContain("off_branch_messages_in_timeline", preview.ImportStrategy.WarningCodes);

        var message = Assert.Single(preview.Messages);
        Assert.Equal("entry-after-summary", message.EntryId);
        Assert.Equal("after summary", message.TextPreview);

        var sessionsResponse = await fixture.Client.GetAsync("/api/sessions");
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = JsonSerializer.Deserialize(
            await sessionsResponse.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        Assert.Empty(sessions ?? []);
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_ImportsPreviewTimelineAndPersistsSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent(ValidCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlImportResultDto);
        Assert.NotNull(result);
        var imported = result!.Session;
        Assert.NotNull(imported);
        Assert.NotEqual("coding-session-1", imported.Id);
        Assert.Equal("Imported CodingAgent session coding-session-1", imported.Title);
        Assert.True(imported.Persisted);
        Assert.Equal(3, imported.Messages.Count);
        Assert.Equal("user", imported.Messages[0].Role);
        Assert.Equal("hello coding agent", imported.Messages[0].Text);
        Assert.Equal("assistant", imported.Messages[1].Role);
        Assert.Contains("done", imported.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("[thinking content present]", imported.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("[tool call: 1]", imported.Messages[1].Text, StringComparison.Ordinal);
        Assert.Equal("assistant", imported.Messages[2].Role);
        Assert.Contains("not found", imported.Messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("[tool result: tool-1; status=error]", imported.Messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("entry-tool", result.SourceTree.LeafEntryId);
        Assert.Equal(1, result.SourceAudit.NonImportedEntryCount);
        Assert.Equal(3, result.SourceAudit.ImportedMessageCount);
        Assert.Contains(result.SourceAudit.Warnings, warning => warning.Code == "webchat_import_is_linearized");
        Assert.Equal("conservative-timeline-linearized", result.SourceStrategy.Strategy);
        Assert.Equal("entry-tool", result.SourceStrategy.SourceLeafEntryId);
        Assert.False(result.SourceStrategy.CurrentBranchOnly);
        Assert.True(result.SourceStrategy.ImportsTimelineMessagesOnly);
        Assert.False(result.SourceStrategy.PersistsBranchTree);
        Assert.Contains("webchat_import_is_linearized", result.SourceStrategy.WarningCodes);
        Assert.Equal("conservative-timeline-linearized", result.SourceMetadata.ImportStrategy?.Strategy);
        Assert.Equal("entry-tool", result.SourceMetadata.ImportStrategy?.SourceLeafEntryId);

        var fetched = await fixture.Client.GetAsync($"/api/sessions/{imported.Id}");
        fetched.EnsureSuccessStatusCode();
        var fetchedSession = JsonSerializer.Deserialize(
            await fetched.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.Equal(imported.Id, fetchedSession?.Id);
        Assert.Equal(3, fetchedSession?.Messages.Count);
        Assert.Equal("conservative-timeline-linearized", fetchedSession?.SourceMetadata?.ImportStrategy?.Strategy);
        Assert.Equal("entry-tool", fetchedSession?.SourceMetadata?.ImportStrategy?.SourceLeafEntryId);
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_ReturnsProblemDetailsForMalformedJsonl()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var jsonl = ValidCodingAgentHeader() + "{not-json}\n";

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));

        await AssertJsonlProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid_json",
            2,
            "line 2 is not valid JSON",
            "Invalid CodingAgent JSONL import");
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_GivesToolMessagesConservativeText()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent(CodingAgentJsonlWithToolOnlyMessages(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlImportResultDto);
        Assert.NotNull(result);
        var imported = result!.Session;
        Assert.Equal(2, imported.Messages.Count);
        Assert.Equal("assistant", imported.Messages[0].Role);
        Assert.False(string.IsNullOrWhiteSpace(imported.Messages[0].Text));
        Assert.Contains("[tool call: 1]", imported.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("assistant", imported.Messages[1].Role);
        Assert.False(string.IsNullOrWhiteSpace(imported.Messages[1].Text));
        Assert.Contains("[tool result: tool-2; status=ok]", imported.Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_ReturnsSourceTreeAuditForBranchedSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent(BranchedCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlImportResultDto);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Session.Messages.Count);
        Assert.Equal("entry-label", result.SourceTree.LeafEntryId);
        Assert.Equal(2, result.SourceTree.BranchPointCount);
        Assert.True(result.SourceAudit.IsBranched);
        Assert.True(result.SourceAudit.WillImportTimelineMessagesOnly);
        Assert.False(result.SourceAudit.WillImportCurrentBranchOnly);
        Assert.Equal(5, result.SourceAudit.ImportedMessageCount);
        Assert.Equal(4, result.SourceAudit.NonImportedEntryCount);
        Assert.Equal(3, result.SourceAudit.CurrentBranchMessageCount);
        Assert.Equal(2, result.SourceAudit.OffBranchMessageCount);
        Assert.Equal(["entry-root", "entry-left", "entry-summary", "entry-after-summary", "entry-label"], result.SourceAudit.CurrentBranchTimeline.Select(entry => entry.EntryId).ToArray());
        Assert.False(result.SourceAudit.CurrentBranchTimeline[2].WillImportAsMessage);
        Assert.Equal("abandoned", result.SourceAudit.CurrentBranchTimeline[2].TextPreview);

        var label = Assert.Single(result.SourceAudit.BranchLabels);
        Assert.Equal("entry-root", label.EntryId);
        Assert.Equal("checkpoint", label.Label);
        Assert.True(label.IsOnCurrentBranch);
        Assert.Contains(result.SourceAudit.Warnings, warning => warning.Code == "branch_tree_not_persisted");
        Assert.Contains(result.SourceAudit.Warnings, warning => warning.Code == "off_branch_messages_in_timeline");
        Assert.Contains(result.SourceAudit.Warnings, warning => warning.Code == "non_message_entries_not_imported_as_messages");
        Assert.Contains(result.SourceAudit.Warnings, warning => warning.Code == "webchat_import_is_linearized");
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_CanImportCurrentBranchOnly()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);

        var response = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl?currentBranchOnly=true",
            new StringContent(BranchedCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.CodingAgentJsonlImportResultDto);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Session.Messages.Count);
        Assert.Equal(["root", "left", "after summary"], result.Session.Messages.Select(message => message.Text).ToArray());
        Assert.True(result.SourceAudit.WillImportCurrentBranchOnly);
        Assert.Equal(3, result.SourceAudit.ImportedMessageCount);
        Assert.Equal(6, result.SourceAudit.NonImportedEntryCount);
        Assert.Equal(3, result.SourceAudit.CurrentBranchMessageCount);
        Assert.Equal(2, result.SourceAudit.OffBranchMessageCount);
        Assert.Equal(3, result.Summary.ImportedMessageCount);
        Assert.True(result.Summary.CurrentBranchOnly);
        Assert.Equal("conservative-current-branch-linearized", result.SourceStrategy.Strategy);
        Assert.Equal("entry-label", result.SourceStrategy.SourceLeafEntryId);
        Assert.True(result.SourceStrategy.CurrentBranchOnly);
        Assert.True(result.SourceStrategy.ImportsTimelineMessagesOnly);
        Assert.False(result.SourceStrategy.PersistsBranchTree);
        Assert.DoesNotContain("off_branch_messages_in_timeline", result.SourceStrategy.WarningCodes);
        Assert.True(result.SourceMetadata.Audit?.WillImportCurrentBranchOnly);
        Assert.Equal("conservative-current-branch-linearized", result.SourceMetadata.ImportStrategy?.Strategy);
        Assert.DoesNotContain(result.SourceAudit.Warnings, warning => warning.Code == "off_branch_messages_in_timeline");
        Assert.DoesNotContain(result.Session.Messages, message => message.Text.Contains("right", StringComparison.Ordinal));

        var fetched = await fixture.Client.GetAsync($"/api/sessions/{result.Session.Id}");
        fetched.EnsureSuccessStatusCode();
        var fetchedSession = JsonSerializer.Deserialize(
            await fetched.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.Equal(3, fetchedSession?.Messages.Count);
        Assert.Equal("coding-agent-jsonl", fetchedSession?.SourceMetadata?.Kind);
        Assert.True(fetchedSession?.SourceMetadata?.Audit?.WillImportCurrentBranchOnly);
        Assert.Equal("conservative-current-branch-linearized", fetchedSession?.SourceMetadata?.ImportStrategy?.Strategy);
        Assert.Equal("entry-label", fetchedSession?.SourceMetadata?.ImportStrategy?.SourceLeafEntryId);
    }

    [Fact]
    public async Task CodingAgentJsonlImportEndpoint_DoesNotMutateExistingSession()
    {
        await using var fixture = await WebUiEndpointFixture.StartAsync(StreamOk);
        var created = await fixture.Client.PostAsync(
            "/api/sessions",
            JsonBody(
                new CreateSessionRequest("Existing session", "openai", "gpt-5.4"),
                WebUiEndpointJsonContext.Default.CreateSessionRequest));
        created.EnsureSuccessStatusCode();
        var existing = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(existing);

        var importedResponse = await fixture.Client.PostAsync(
            "/api/sessions/import.coding-agent-jsonl",
            new StringContent(ValidCodingAgentJsonl(), Encoding.UTF8, "application/x-ndjson"));
        importedResponse.EnsureSuccessStatusCode();

        var fetchedExisting = await fixture.Client.GetAsync($"/api/sessions/{existing!.Id}");
        fetchedExisting.EnsureSuccessStatusCode();
        var afterImport = JsonSerializer.Deserialize(
            await fetchedExisting.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDto);
        Assert.NotNull(afterImport);
        Assert.Equal(existing.Id, afterImport!.Id);
        Assert.Equal(existing.Title, afterImport.Title);
        Assert.Equal(existing.Provider, afterImport.Provider);
        Assert.Equal(existing.Model, afterImport.Model);
        Assert.Equal(existing.UpdatedAt, afterImport.UpdatedAt);
        Assert.Empty(afterImport.Messages);

        var sessionsResponse = await fixture.Client.GetAsync("/api/sessions");
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = JsonSerializer.Deserialize(
            await sessionsResponse.Content.ReadAsStringAsync(),
            WebUiEndpointJsonContext.Default.WebChatSessionDtoArray);
        Assert.Equal(2, sessions?.Length);
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

    private static string CodingAgentJsonlWithToolOnlyMessages() =>
        ValidCodingAgentHeader() +
        "{\"type\":\"message\",\"id\":\"entry-tool-call-only\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"tool-2\",\"name\":\"read\",\"arguments\":\"{}\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-tool-result-empty\",\"parentId\":\"entry-tool-call-only\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"tool-2\",\"isError\":false,\"content\":[]}}\n";

    private static string BranchedCodingAgentJsonl() =>
        ValidCodingAgentHeader() +
        "{\"type\":\"message\",\"id\":\"entry-root\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"root\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-left\",\"parentId\":\"entry-root\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"left\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-right\",\"parentId\":\"entry-root\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"right\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-right-assistant\",\"parentId\":\"entry-right\",\"timestamp\":\"2026-05-23T02:04:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"right done\"}]}}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label-set\",\"parentId\":\"entry-right-assistant\",\"timestamp\":\"2026-05-23T02:05:00+00:00\",\"targetId\":\"entry-root\",\"label\":\"checkpoint\"}\n" +
        "{\"type\":\"branch_summary\",\"id\":\"entry-summary\",\"parentId\":\"entry-left\",\"timestamp\":\"2026-05-23T02:06:00+00:00\",\"fromId\":\"entry-left\",\"summary\":\"abandoned\"}\n" +
        "{\"type\":\"message\",\"id\":\"entry-after-summary\",\"parentId\":\"entry-summary\",\"timestamp\":\"2026-05-23T02:07:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"after summary\"}]}}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label-clear\",\"parentId\":\"entry-after-summary\",\"timestamp\":\"2026-05-23T02:08:00+00:00\",\"targetId\":\"entry-after-summary\",\"label\":\"temp\"}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label\",\"parentId\":\"entry-after-summary\",\"timestamp\":\"2026-05-23T02:09:00+00:00\",\"targetId\":\"entry-after-summary\",\"label\":\"\"}\n";

    private static string ValidCodingAgentHeader() =>
        "{\"type\":\"session\",\"version\":3,\"id\":\"coding-session-1\",\"timestamp\":\"2026-05-23T02:00:00+00:00\",\"cwd\":\"C:\\\\Users\\\\zhouh\\\\Desktop\\\\Tau\",\"parentSession\":\"parent-session.jsonl\"}\n";

    private sealed class WebUiEndpointFixture : IAsyncDisposable
    {
        private WebUiEndpointFixture(WebApplication app, HttpClient client, string storePath, string artifactStorePath)
        {
            App = app;
            Client = client;
            StorePath = storePath;
            ArtifactStorePath = artifactStorePath;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public string StorePath { get; }
        public string ArtifactStorePath { get; }

        public static async Task<WebUiEndpointFixture> StartAsync(
            Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run,
            ITauLogSink? logSink = null)
        {
            return await StartAsync(sp => new WebChatService(
                    sp.GetRequiredService<WebChatStore>(),
                    (_, _, _, _) => new FakeWebUiRunner(run),
                    logSink))
                .ConfigureAwait(false);
        }

        public static async Task<WebUiEndpointFixture> StartDefaultRunnerAsync(ITauLogSink? logSink = null)
        {
            return await StartAsync(sp => new WebChatService(
                    sp.GetRequiredService<WebChatStore>(),
                    sp.GetRequiredService<WebArtifactService>(),
                    sp.GetRequiredService<WebUiJavaScriptReplBridge>(),
                    logSink))
                .ConfigureAwait(false);
        }

        private static async Task<WebUiEndpointFixture> StartAsync(Func<IServiceProvider, WebChatService> chatFactory)
        {
            var storePath = Path.Combine(Path.GetTempPath(), $"tau-webui-endpoint-{Guid.NewGuid():N}.json");
            var artifactStorePath = Path.Combine(Path.GetTempPath(), $"tau-webui-artifacts-{Guid.NewGuid():N}.json");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new WebChatStore(storePath));
            builder.Services.AddSingleton(new WebArtifactStore(artifactStorePath));
            builder.Services.AddSingleton<WebArtifactService>();
            builder.Services.AddSingleton<WebUiJavaScriptReplBridge>();
            builder.Services.AddSingleton(chatFactory);
            var app = builder.Build();
            app.MapWebUiEndpoints();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = Assert.Single(addresses?.Addresses ?? []);
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new WebUiEndpointFixture(app, client, storePath, artifactStorePath);
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
            if (File.Exists(ArtifactStorePath))
            {
                File.Delete(ArtifactStorePath);
            }
        }
    }

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = [];

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private static readonly SemaphoreSlim SyncRoot = new(1, 1);

        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private EnvironmentVariableScope()
        {
            SyncRoot.Wait();
        }

        public static EnvironmentVariableScope Acquire() => new();

        public void Set(string name, string? value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_originalValues.ContainsKey(name))
            {
                _originalValues[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _disposed = true;
            SyncRoot.Release();
        }
    }
}

[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(UpsertWebArtifactRequest))]
[JsonSerializable(typeof(WebRuntimeMessageRequest))]
[JsonSerializable(typeof(WebJavaScriptReplRequestDto))]
[JsonSerializable(typeof(WebJavaScriptReplResultRequest))]
[JsonSerializable(typeof(WebJavaScriptReplResultDto))]
[JsonSerializable(typeof(WebJavaScriptReplFileDto))]
[JsonSerializable(typeof(IReadOnlyList<WebJavaScriptReplFileDto>))]
[JsonSerializable(typeof(WebChatSessionDto))]
[JsonSerializable(typeof(WebArtifactDto))]
[JsonSerializable(typeof(WebArtifactSummaryDto))]
[JsonSerializable(typeof(WebArtifactSummaryDto[]))]
[JsonSerializable(typeof(WebUiAuthStatusDto))]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(CodingAgentJsonlSessionPreviewDto))]
[JsonSerializable(typeof(CodingAgentJsonlTreeMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlEntryMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportAuditDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchTimelineEntryDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchLabelDto))]
[JsonSerializable(typeof(CodingAgentJsonlAuditWarningDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportStrategyDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportResultDto))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class WebUiEndpointJsonContext : JsonSerializerContext;
