# Tau.WebUi session clone endpoint

Date: 2026-05-18

## Summary

Added `POST /api/sessions/{id}/clone` to `Tau.WebUi`. Creates a new session that copies the source session's title (prefixed with `"Copy of "`), provider, model, and full message history. The cloned session gets a fresh GUID id and new CreatedAt/UpdatedAt timestamps. Useful for forking a conversation to try a different direction without losing the original.

## Changes

- `src/Tau.WebUi/Services/WebChatService.cs`: new `CloneSession(string id)` method — fetches the source session, builds a `WebChatSessionDto` with a new id and "Copy of " title, hydrates it through the existing `WebChatSession.FromImportedDto` path so attachments / tool calls / message order are preserved, registers it in the in-memory map, persists immediately, and returns the new DTO.
- `src/Tau.WebUi/WebUiApplication.cs`: maps `POST /api/sessions/{id}/clone` → `WebChatService.CloneSession`, returning 200 with the new DTO or 404 when the source id is unknown.
- `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`: new `CloneSessionEndpoint_DuplicatesMessagesAndPrefixesTitle` regression — creates a session, sends a message, clones, asserts new id / "Copy of …" title / same provider+model / messages copied; also covers the 404 path for an unknown source id.
- Synced `next.md` (lifecycle bullet) and the active port plan (verification list).

## Verification

- `dotnet build .\src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~CloneSession"` — 1 test passed.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --no-build --verbosity minimal` — 151 tests passed.

## Decisions

- Reused `WebChatSession.FromImportedDto` for the clone path so the new session lands in exactly the same shape as imported sessions — same attachment normalization, same runner factory, same persistence semantics. Avoids drift between "import a backup" and "fork live".
- Prefixed the cloned Title with `"Copy of "` rather than appending `" (clone)"` or a numeric suffix. The prefix sorts naturally in the session list and is immediately legible without needing to wait for the listing to scan to the right column.
- Generated a brand-new GUID id on the server side rather than letting clients suggest one. This keeps id collisions impossible across simultaneous clone requests and matches the existing create / import behaviors.
- Persisted right after the in-memory clone so the new session survives a reload. Mirrors `UpdateSessionSettings` / `ClearSessionMessages`.
- Did **not** route the clone through the runner factory (i.e., no fresh conversation start). The clone is structural — same prior context, ready for the next user turn — which is the typical "fork from this point" use case.
