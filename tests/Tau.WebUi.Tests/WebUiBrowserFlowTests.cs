using System.Text;
using Microsoft.Playwright;

namespace Tau.WebUi.Tests;

[Collection(nameof(WebUiBrowserCollection))]
public sealed class WebUiBrowserFlowTests
{
    private readonly WebUiBrowserFixture _fixture;

    public WebUiBrowserFlowTests(WebUiBrowserFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HomePage_LoadsCatalogAndCreatesSessionFromSidebar()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Browser smoke");
        await page.ClickAsync("#new-session");

        var session = page.Locator("#sessions .session").First;
        await Assertions.Expect(session).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Assertions.Expect(session).ToContainTextAsync("Browser smoke");
        await Assertions.Expect(session).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
    }

    [Fact]
    public async Task SendMessage_StreamsAssistantTextIntoMessagePane()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Streaming");
        await page.ClickAsync("#new-session");
        await Assertions.Expect(page.Locator("#sessions .session.active")).ToBeVisibleAsync(new() { Timeout = 5000 });

        await page.FillAsync("#prompt", "hello tau");
        await page.ClickAsync("#send");

        var userBubble = page.Locator("#messages .message.user .message-text");
        await Assertions.Expect(userBubble).ToContainTextAsync("hello tau", new() { Timeout = 5000 });

        var assistantBubble = page.Locator("#messages .message.assistant .message-text");
        await Assertions.Expect(assistantBubble).ToContainTextAsync("hello from tau", new() { Timeout = 5000 });

        var promptValue = await page.InputValueAsync("#prompt");
        Assert.Equal(string.Empty, promptValue);
    }

    [Fact]
    public async Task RenameSession_UpdatesSidebarTitle()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Initial title");
        await page.ClickAsync("#new-session");
        await Assertions.Expect(page.Locator("#sessions .session.active")).ToContainTextAsync("Initial title", new() { Timeout = 5000 });

        await page.FillAsync("#session-title", "Renamed title");
        await page.ClickAsync("#save-settings");

        await Assertions.Expect(page.Locator("#sessions .session.active")).ToContainTextAsync("Renamed title", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#sessions .session.active")).Not.ToContainTextAsync("Initial title");
    }

    [Fact]
    public async Task ArtifactsPane_RefreshesAndRendersTextArtifact()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Artifacts");
        await page.ClickAsync("#new-session");
        var session = page.Locator("#sessions .session.active");
        await Assertions.Expect(session).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Assertions.Expect(session).ToContainTextAsync("Artifacts", new() { Timeout = 5000 });
        var sessionId = await session.GetAttributeAsync("data-id");
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        await page.EvaluateAsync(
            """
            async sessionId => {
              const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts/notes.txt`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: 'hello artifact pane', mimeType: 'text/plain' })
              });
              if (!response.ok) throw new Error(await response.text());
            }
            """,
            sessionId);
        await page.ClickAsync("#refresh-artifacts");

        await Assertions.Expect(page.Locator("#artifacts-list .artifact-pill")).ToContainTextAsync("notes.txt", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("hello artifact pane", new() { Timeout = 5000 });
    }

    [Fact]
    public async Task ArtifactsPane_RendersSpecializedViewerBaselines()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Artifact viewers");
        await page.ClickAsync("#new-session");
        var session = page.Locator("#sessions .session.active");
        await Assertions.Expect(session).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Assertions.Expect(session).ToContainTextAsync("Artifact viewers", new() { Timeout = 5000 });
        var sessionId = await session.GetAttributeAsync("data-id");
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        await page.EvaluateAsync(
            """
            async sessionId => {
              const markdown = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts/notes.md`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: '# Viewer Title\n\n- [x] rendered item', mimeType: 'text/markdown' })
              });
              if (!markdown.ok) throw new Error(await markdown.text());

              const svg = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts/chart.svg`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                  content: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40"><text x="8" y="24">SVG OK</text></svg>',
                  mimeType: 'image/svg+xml'
                })
              });
              if (!svg.ok) throw new Error(await svg.text());

              const pdf = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts/report.pdf`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: 'JVBERi0xLjQKJcTl8uXrp/Og0MTGCg==', mimeType: 'application/pdf' })
              });
              if (!pdf.ok) throw new Error(await pdf.text());

              const generic = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts/archive.bin`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: 'AAECAwQ=', mimeType: 'application/octet-stream' })
              });
              if (!generic.ok) throw new Error(await generic.text());
            }
            """,
            sessionId);

        await page.ClickAsync($"#sessions .session[data-id='{sessionId}']");
        await page.ClickAsync("#refresh-artifacts");

        await Assertions.Expect(page.Locator("#artifacts-list .artifact-pill[data-file='notes.md']")).ToContainTextAsync("notes.md", new() { Timeout = 5000 });
        await page.ClickAsync("#artifacts-list .artifact-pill[data-file='notes.md']");
        await Assertions.Expect(page.Locator("#artifact-preview .artifact-viewer-title")).ToContainTextAsync("notes.md", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("Markdown preview", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview h1")).ToContainTextAsync("Viewer Title", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview .task-check")).ToBeCheckedAsync(new() { Timeout = 5000 });

        await page.ClickAsync("#artifacts-list .artifact-pill[data-file='chart.svg']");

        await Assertions.Expect(page.Locator("#artifact-preview .artifact-viewer-title")).ToContainTextAsync("chart.svg", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("SVG preview", new() { Timeout = 5000 });
        var svgFrame = page.FrameLocator("iframe.artifact-frame");
        await Assertions.Expect(svgFrame.Locator("svg")).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Assertions.Expect(svgFrame.Locator("text")).ToContainTextAsync("SVG OK", new() { Timeout = 5000 });

        await page.ClickAsync("#artifacts-list .artifact-pill[data-file='report.pdf']");

        await Assertions.Expect(page.Locator("#artifact-preview .artifact-viewer-title")).ToContainTextAsync("report.pdf", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("PDF preview", new() { Timeout = 5000 });
        var pdfSrc = await page.Locator("#artifact-preview iframe.artifact-frame").GetAttributeAsync("src");
        Assert.StartsWith("data:application/pdf;base64,", pdfSrc);

        await page.ClickAsync("#artifacts-list .artifact-pill[data-file='archive.bin']");

        await Assertions.Expect(page.Locator("#artifact-preview .artifact-viewer-title")).ToContainTextAsync("archive.bin", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("Generic file", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("Preview not available for this file type.", new() { Timeout = 5000 });
        var downloadHref = await page.Locator("#artifact-preview .artifact-download-link").GetAttributeAsync("href");
        Assert.StartsWith("data:application/octet-stream;base64,", downloadHref);
    }

    [Fact]
    public async Task HtmlArtifact_CanReadSessionAttachmentsAndReturnDownloadableFile()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "Artifact attachments");
        await page.ClickAsync("#new-session");
        var session = page.Locator("#sessions .session.active");
        await Assertions.Expect(session).ToBeVisibleAsync(new() { Timeout = 5000 });
        var sessionId = await session.GetAttributeAsync("data-id");
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var attachmentContent = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello attachment"));
        const string artifactHtml = """
            <!doctype html>
            <html>
            <body>
            <script>
            (async () => {
              const files = window.listAttachments();
              const text = window.readTextAttachment(files[0].id);
              const bytes = window.readBinaryAttachment(files[0].id);
              const returned = await window.returnDownloadableFile('from-attachment.txt', text, 'text/plain');
              document.body.textContent = `${files[0].fileName}|${text}|${bytes.length}|${returned.fileName}`;
            })().catch(error => {
              document.body.textContent = `error:${error.message || error}`;
            });
            </script>
            </body>
            </html>
            """;

        await page.EvaluateAsync(
            """
            async args => {
              const message = await fetch(`/api/sessions/${encodeURIComponent(args.sessionId)}/messages`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                  text: 'use attachment',
                  attachments: [{
                    id: 'att-1',
                    type: 'text',
                    fileName: 'notes.txt',
                    mimeType: 'text/plain',
                    size: 16,
                    content: args.attachmentContent,
                    extractedText: 'hello attachment'
                  }]
                })
              });
              if (!message.ok) throw new Error(await message.text());

              const artifact = await fetch(`/api/sessions/${encodeURIComponent(args.sessionId)}/artifacts/attachment.html`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: args.artifactHtml, mimeType: 'text/html' })
              });
              if (!artifact.ok) throw new Error(await artifact.text());
            }
            """,
            new { sessionId, attachmentContent, artifactHtml });

        await page.EvaluateAsync(
            "async sessionId => { await openSession(sessionId); await loadArtifacts(sessionId); }",
            sessionId);

        await Assertions.Expect(page.Locator("#artifacts-list .artifact-pill")).ToContainTextAsync("attachment.html", new() { Timeout = 5000 });
        var frame = page.FrameLocator("iframe.artifact-frame");
        await Assertions.Expect(frame.Locator("body"))
            .ToContainTextAsync("notes.txt|hello attachment|16|from-attachment.txt", new() { Timeout = 5000 });
    }

    [Fact]
    public async Task JavaScriptRepl_CanUseSandboxProvidersAndCreateArtifacts()
    {
        await using var context = await _fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("document.querySelectorAll('#provider option').length > 0");

        await page.FillAsync("#session-title", "JavaScript REPL");
        await page.ClickAsync("#new-session");
        var session = page.Locator("#sessions .session.active");
        await Assertions.Expect(session).ToBeVisibleAsync(new() { Timeout = 5000 });
        var sessionId = await session.GetAttributeAsync("data-id");
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var attachmentContent = Convert.ToBase64String(Encoding.UTF8.GetBytes("from attachment"));
        var resultJson = await page.EvaluateAsync<string>(
            """
            async args => {
              const message = await fetch(`/api/sessions/${encodeURIComponent(args.sessionId)}/messages`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                  text: 'seed repl attachment',
                  attachments: [{
                    id: 'repl-att-1',
                    type: 'text',
                    fileName: 'source.txt',
                    mimeType: 'text/plain',
                    size: 15,
                    content: args.attachmentContent,
                    extractedText: 'from attachment'
                  }]
                })
              });
              if (!message.ok) throw new Error(await message.text());

              await openSession(args.sessionId);
              const result = await window.executeJavaScriptRepl(`
                console.log('sum', 2 + 3);
                const attachments = window.listAttachments();
                const text = window.readTextAttachment(attachments[0].id);
                await window.createOrUpdateArtifact('repl-data.json', { text, answer: 42 }, 'application/json');
                const stored = await window.getArtifact('repl-data.json');
                await window.returnDownloadableFile('repl.txt', stored.text, 'text/plain');
                return { stored, fileCount: attachments.length };
              `);
              return JSON.stringify(result);
            }
            """,
            new { sessionId, attachmentContent });

        using var result = System.Text.Json.JsonDocument.Parse(resultJson);
        var root = result.RootElement;
        Assert.Contains("sum 5", root.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.Contains("[Files returned: 1]", root.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, root.GetProperty("files").GetArrayLength());
        var file = root.GetProperty("files")[0];
        Assert.Equal("repl.txt", file.GetProperty("fileName").GetString());
        Assert.Equal("text/plain", file.GetProperty("mimeType").GetString());
        Assert.Equal("from attachment", file.GetProperty("content").GetString());
        Assert.Equal(42, root.GetProperty("returnValue").GetProperty("stored").GetProperty("answer").GetInt32());
        Assert.Equal(1, root.GetProperty("returnValue").GetProperty("fileCount").GetInt32());

        await Assertions.Expect(page.Locator("#artifacts-list .artifact-pill[data-file='repl-data.json']"))
            .ToContainTextAsync("repl-data.json", new() { Timeout = 5000 });
        await page.ClickAsync("#artifacts-list .artifact-pill[data-file='repl-data.json']");
        await Assertions.Expect(page.Locator("#artifact-preview")).ToContainTextAsync("\"answer\": 42", new() { Timeout = 5000 });
    }
}
