# Tau.WebUi Markdown export endpoint

Date: 2026-05-18

## Summary

Added `GET /api/sessions/{id}/export.md` to `Tau.WebUi` so operators can download a session as a redacted Markdown transcript alongside the existing JSON and HTML formats. Same `TauSecretRedactor` (env: `TAU_WEBUI_REDACT_SECRETS`) governs both export shapes.

## Changes

- `src/Tau.WebUi/Services/WebChatMarkdownExporter.cs`: new static renderer that emits a portable Markdown transcript — H1 title with provider/model/timestamps metadata, role-based H2 sections per message, fenced-code blocks for tool-call arguments / outputs, attachment list with mime type / size / extracted-text fold, and a `> **Error:**` block for assistant errors. All user-typed strings go through `TauSecretRedactor.Redact` before rendering.
- `src/Tau.WebUi/WebUiApplication.cs`: new endpoint binding mirroring the HTML one; returns `text/markdown` with a `*.tau-webui-session.md` filename.
- `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`: new `ExportMarkdownEndpoint_RendersSessionAndRedactsSecrets` regression sending a message with an AWS access key, fetching the markdown, and asserting `[redacted]` appears while unrelated text survives. Also covers the 404 path for an unknown session id.
- Synced `next.md` (lifecycle bullet now mentions `.md`) and the active port plan (verification list bullet).

## Verification

- `dotnet build .\src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~ExportMarkdownEndpoint"` — 1 test passed.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --no-build --verbosity minimal` — 147 tests passed.

## Decisions

- Shared the same `TAU_WEBUI_REDACT_SECRETS` env var rather than introducing a per-format toggle. The two exporters answer the same security question ("am I sharing this outside my machine?") and a single toggle keeps operator ergonomics simple.
- Used vanilla Markdown without GitHub-flavored extensions (no front-matter, no YAML, no tables-by-default). The format is widely portable — pipes through `pandoc`, GitHub README, IntelliJ preview, plain text viewers — without requiring a specific renderer. Tool arguments / output go inside plain fenced blocks; recipients can pretty-print further if they want.
- Did **not** synthesize a built-in TOC or message index. Markdown viewers commonly auto-generate one from H2 headings; manually doing it would force the renderer to know about ordering and duplicate-handling without the user asking for it.
- Indented attachment extracted-text bodies by two spaces inside the fenced block so it nests visually under the attachment bullet when the markdown is rendered to HTML by GitHub / pandoc — keeps the structure self-evident without needing tables.
