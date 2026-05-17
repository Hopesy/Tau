# Tau.WebUi clear messages endpoint

Date: 2026-05-18

## Summary

Added `POST /api/sessions/{id}/clear` for `Tau.WebUi` so a chat session can be reset to an empty message list without deleting the session record or losing its title/provider/model selection. Matches the conceptual peer of `Tau.CodingAgent`'s `/clear` command, but at the HTTP/session-state layer instead of the terminal screen.

## Changes

- `src/Tau.WebUi/Services/WebChatService.cs`: new `ClearSessionMessages(string id)` service method; `WebChatSession.ClearMessages()` empties the in-memory message list and bumps `UpdatedAt`.
- `src/Tau.WebUi/WebUiApplication.cs`: routes `POST /api/sessions/{id}/clear` → `WebChatService.ClearSessionMessages`, returning 200 with the refreshed `WebChatSessionDto` or 404 when the session id is unknown.
- `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`: new `ClearSessionMessagesEndpoint_RemovesMessagesAndKeepsSettings` regression that sends a message, clears, and verifies messages are empty while Title/Provider/Model survive; also covers the 404 path for an unknown session id.
- Synced `next.md` (WebUi session lifecycle bullet) and the active port plan (verification list).

## Verification

- `dotnet build .\src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 145 tests passed (1 new + 144 prior).

## Decisions

- Modeled clear as a separate endpoint instead of overloading `PUT /api/sessions/{id}` with a "messages=null" field. The PUT endpoint already takes optional title/provider/model and applies non-null updates; encoding "clear all messages" through optional fields would conflate two distinct intents. A dedicated POST keeps the semantics explicit (and HEAD/idempotency expectations cleaner).
- Persisted immediately after the in-memory clear so a reload doesn't resurrect old messages. The persist call mirrors how `UpdateSessionSettings` already commits updates.
- Did **not** apply secret redaction to the cleared payload (the response is the now-empty message list plus session settings). Redaction lives in the HTML export path; this endpoint never returns user-typed text.
