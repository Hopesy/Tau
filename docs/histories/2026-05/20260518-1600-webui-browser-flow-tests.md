# Tau.WebUi browser flow tests

Date: 2026-05-18

## Summary

Added `tests/Tau.WebUi.Tests/` — a new xunit project that drives the WebUi through a real headless Chromium browser via Microsoft.Playwright. Previously `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs` covered the HTTP layer (NDJSON streaming, session CRUD, export endpoints) but nothing exercised the embedded HTML/JS in `src/Tau.WebUi/Ui/WebUiPage.cs`. The new tests boot the WebApp on a dynamic port, launch a Playwright-controlled Chromium, and walk the user-facing UI flows.

## Changes

- `tests/Tau.WebUi.Tests/Tau.WebUi.Tests.csproj`: new project; xunit + Microsoft.Playwright 1.49.0; references `Tau.WebUi`, `Tau.Agent`, `Tau.Ai`, `Tau.CodingAgent`.
- `Tau.slnx`: registers the new project under `/tests/`.
- `tests/Tau.WebUi.Tests/WebUiBrowserFixture.cs`: `IAsyncLifetime` collection fixture. `InitializeAsync` (1) starts `WebApplication` with a temp-file `WebChatStore` and a `WebChatService` whose runner factory returns `FakeWebUiRunner`, (2) invokes `Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"])` so the first test run downloads the browser, (3) launches headless Chromium. `NewContextAsync()` mints a per-test browser context bound to the dynamic base URL so storage state never leaks between tests.
- `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`: minimal `ICodingAgentRunner` implementation. `SelectModel` is lenient — it accepts whatever provider/model the real `ModelCatalog` surfaces in the dropdowns, so tests don't depend on a particular default. `RunAsync` yields `MessageUpdateEvent(TextDeltaEvent(0, "hello from tau", partial))` followed by `AgentEndEvent`.
- `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`: 3 browser tests covering core flows:
  1. **HomePage_LoadsCatalogAndCreatesSessionFromSidebar** — waits for `#provider option` to populate, fills `#session-title`, clicks `#new-session`, asserts a new `.session.active` appears with the title.
  2. **SendMessage_StreamsAssistantTextIntoMessagePane** — creates a session, fills `#prompt`, clicks `#send`, waits for `.message.user .message-text` to contain the prompt, then `.message.assistant .message-text` to contain the streamed `hello from tau`, and verifies the prompt textarea cleared.
  3. **RenameSession_UpdatesSidebarTitle** — creates a session with one title, then changes `#session-title`, clicks `#save-settings`, asserts the sidebar entry's text updates.

## Verification

- `dotnet build tests/Tau.WebUi.Tests/Tau.WebUi.Tests.csproj --verbosity minimal` — succeeded.
- `dotnet test tests/Tau.WebUi.Tests/Tau.WebUi.Tests.csproj --no-build --verbosity minimal` — 3/3 tests passed (~2s after Chromium cache warmed; first run downloads Chromium).
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --verbosity minimal` — 155/155 prior tests still pass (no regression).

## Decisions

- **Playwright over AngleSharp**: the WebUi page relies on JS-driven NDJSON streaming consumption, dynamic message rendering, and catalog loading — none of which AngleSharp can execute. A real browser is the only way to catch frontend regressions that aren't visible through HttpClient round-trips.
- **Browser install in fixture, not a separate setup script**: `Microsoft.Playwright.Program.Main(["install", "chromium"])` works inside the test process and idempotently no-ops when Chromium is already cached under `%LOCALAPPDATA%\ms-playwright`. The trade-off is that first-time runs take longer (download), but no developer-side script is required.
- **Fake runner kept tiny and per-fixture**: avoided referencing `Tau.CodingAgent.Tests` from this project (cross-test-project references confuse xunit discovery). `FakeWebUiRunner` only implements what `WebChatService` actually exercises in browser flow tests.
- **`NewContextAsync` per test, not per fixture**: each test gets a fresh browser context so localStorage / cookie state from one test (e.g. the remembered session id) never bleeds into another. The browser process itself is reused for speed.
- **Did not yet cover delete / clone / search / import via browser**: those have endpoint-level coverage in `WebUiEndpointTests`. The browser tests deliberately focus on the path that *only* the UI can exercise — DOM rendering + NDJSON consumption + sidebar reactivity to settings updates. More browser flows can layer on if regressions show up.
