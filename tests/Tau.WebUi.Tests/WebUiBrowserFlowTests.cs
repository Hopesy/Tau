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
}
