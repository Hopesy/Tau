# Tau.WebUi streaming, attachments, and session management

Date: 2026-05-16

## Summary

Advanced Tau.WebUi from minimal chat UI to a richer interactive workspace with streaming messages, file attachments, tool call timeline, session lifecycle management, auth status visibility, and client-side Markdown rendering.

## Changes

### Streaming messages (NDJSON)

- Added `POST /api/sessions/{id}/messages/stream` endpoint returning `application/x-ndjson` stream events.
- `WebChatStreamEventDto` carries `type` discriminator (`user`, `text_delta`, `thinking_delta`, `tool_start`, `tool_update`, `tool_end`, `tool_call`, `error`, `done`) with optional `text/thinking/toolEvent/toolCall/error/session/attachments` payloads.
- `WebChatSession.SendStreamAsync()` yields events as runner produces `AgentEvent` instances; `SendAsync()` now delegates to the stream internally.
- Frontend consumes NDJSON via `ReadableStream` reader, incrementally applies events to session state, and re-renders messages on each chunk.
- `done` event carries final persisted `WebChatSessionDto`.

### Attachments

- `SendMessageRequest` now accepts optional `Attachments` list alongside `Text`.
- `WebChatAttachmentDto` carries `id/type/fileName/mimeType/size/content/extractedText/preview`.
- Frontend reads files via `FileReader`, extracts base64 content, detects text files for `extractedText`, and renders image previews.
- Pending attachments shown in composer with remove buttons; sent attachments rendered in message bubbles.
- User messages with only attachments (no text) are valid.

### Tool call timeline

- `WebChatToolCallDto` carries `id/toolName/status/arguments/output/isError/createdAt/startedAt/completedAt/updates`.
- Stream events carry tool call state transitions; frontend merges by tool call ID.
- Rendered as expandable cards with status badges, input/output/updates details.

### Session lifecycle

- `DELETE /api/sessions/{id}` removes session from store.
- `GET /api/sessions/{id}/export` returns session JSON as downloadable file.
- `POST /api/sessions/import` imports a session DTO, assigns new ID, resolves provider/model.
- `PUT /api/sessions/{id}` updates session title/provider/model and persists the renamed session.
- Frontend sidebar shows Export/Delete buttons per session; Import button opens file picker.
- Frontend remembers the last opened session ID in local storage and restores that session on reload when it still exists.

### Auth status

- `GET /api/auth/{provider}?model=` returns `WebUiAuthStatusDto` with `isConfigured/source/usesOAuth/canLogin/message`.
- Frontend refreshes auth status on provider/model change, displays in session meta area.

### Richer client-side rendering

- Fenced code blocks with language labels.
- Inline code spans.
- Markdown links and bare URL auto-linking.
- Headings, ordered/unordered lists, blockquotes.
- Strong/emphasis spans.
- Pipe tables with horizontal scroll.
- Task list checkboxes.
- Responsive layout for mobile viewports.

### Other

- `GET /favicon.ico` returns 204 to suppress browser console errors.
- `WebUiNdjsonContext` source-gen JSON context for NDJSON serialization without indentation.
- `HasMessageInput()` helper validates text or attachments present.
- `SafeFileName()` helper for export download filename.
- `WebChatService` now accepts an injectable runner factory, allowing WebUi streaming behavior to be tested with a fake `ICodingAgentRunner` instead of requiring live provider credentials.
- Added a service-level streaming behavior test that verifies user event emission, text delta handling, attachment prompt construction, final persisted assistant message, and store write-back.
- Extracted production Minimal API mapping into `WebUiApplication.MapWebUiEndpoints()` so tests and the app use the same route table.
- Added endpoint-level tests that start WebUi on an ephemeral local Kestrel URL with a fake runner, then verify NDJSON streaming, attachment prompt propagation, persisted session write-back, and 400/404 message endpoint contracts.
- Added a test-local source-generated JSON context for endpoint tests to preserve the repository's AOT/trimming analyzer constraints.
- Added endpoint-level rename/restore coverage: `PUT /api/sessions/{id}` persists the new title, a fresh `WebChatService` restores it from `WebChatStore`, and missing sessions return 404 before invoking a runner.
- Added endpoint-level export/import/delete coverage: exported session JSON can be imported as a new session ID, the original can be deleted, and the remaining session list matches the imported session.

## Verification

- `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore` passes with 0 errors.
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 120 tests pass.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` — passed, including WebUi and Mom smoke.

## Decisions

- Chose NDJSON over SSE for streaming because it maps directly to typed DTOs without event/data framing overhead, and the frontend can parse each line as complete JSON.
- Kept all rendering client-side in the single-page HTML rather than server-side, matching the existing Tau.WebUi pattern of zero external dependencies.
- Import assigns a new session ID rather than preserving the original, avoiding ID collisions across instances.
- Auth status is read-only; actual login flows remain in the Tau.Ai OAuth backlog.
- Kept the WebUi runner seam as a narrow factory injection on `WebChatService` instead of adding a new DI-heavy abstraction; production still uses `RuntimeCodingAgentRunner.Create`, while tests can pass a fake runner.
- Kept endpoint tests in-process with real Kestrel rather than a mocked route delegate so status codes, content type, NDJSON line framing, and Minimal API JSON behavior are exercised through the same HTTP path as a browser client.
