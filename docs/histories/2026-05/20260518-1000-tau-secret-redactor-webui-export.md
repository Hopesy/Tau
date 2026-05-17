# Tau secret redactor + WebUi HTML export

Date: 2026-05-18

## Summary

Two related changes shipped together:

1. **`TauSecretRedactor` in `Tau.Ai`** — promoted the secret-pattern redactor that previously lived under `Tau.CodingAgent.Runtime` to a shared `Tau.Ai.TauSecretRedactor`. `CodingAgentSecretRedactor` becomes a thin shim retained for backward compatibility, delegating to the new shared type. Both env-driven constructor (`ForEnvironmentVariable(name)`) and `IsEnabledFromEnvironment(raw)` truth-table helper are kept stable.
2. **`GET /api/sessions/{id}/export.html` in `Tau.WebUi`** — a new endpoint that returns the chat session rendered as a standalone HTML file. Uses `TauSecretRedactor.ForEnvironmentVariable("TAU_WEBUI_REDACT_SECRETS")` so the same secret-pattern coverage applies; default is on, `TAU_WEBUI_REDACT_SECRETS=0` opts out. The renderer ships small inline CSS for role-tinted message blocks, attachments, and tool calls.

## Changes

- `src/Tau.Ai/Security/TauSecretRedactor.cs`: new shared redactor with the existing seven patterns plus `ForEnvironmentVariable(name)` factory and named constants for the coding agent / WebUi env vars.
- `src/Tau.CodingAgent/Runtime/CodingAgentSecretRedactor.cs`: rewritten as a thin shim over `TauSecretRedactor` so existing callers (HTML transcript export) compile unchanged.
- `src/Tau.WebUi/Services/WebChatHtmlExporter.cs`: new static `Render(WebChatSessionDto, TauSecretRedactor)` that emits a single-page HTML transcript with title, provider/model metadata, role-tinted message blocks, optional thinking `<details>`, tool-call list, attachment list (with optional extracted-text fold), and error highlight. All user-typed strings go through `redactor.Redact` before HTML escaping.
- `src/Tau.WebUi/WebUiApplication.cs`: maps `GET /api/sessions/{id}/export.html` to the renderer; uses `TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.WebUiEnvironmentVariable)`.
- `tests/Tau.Ai.Tests/TauSecretRedactorTests.cs`: 11 new tests mirroring the existing coding-agent redactor unit tests, now scoped to the shared type.
- `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`: new `ExportHtmlEndpoint_RendersSessionAndRedactsSecrets` integration test that posts a message containing an AWS access key, fetches the HTML, and verifies the placeholder appears while the unrelated `"a note"` text survives. Also covers the 404 path for an unknown session id.
- Synced `next.md`, `docs/SECURITY.md` (added the WebUi env var note), and the active port plan (verification list bullet).

## Verification

- `dotnet build .\src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~TauSecretRedactor"` — 11 tests passed.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~ExportHtmlEndpoint"` — 1 test passed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` — full chain green (Tau.Ai 185 / Tau.Agent 54 / Tau.Tui 45 / Tau.CodingAgent 146 / Tau.Pods 24 = 454 tests).

## Decisions

- Promoted the redactor to `Tau.Ai` rather than `Tau.WebUi` depending on `Tau.CodingAgent`. WebUi already references `Tau.Ai`; moving the shared utility up to that module keeps the cross-cutting type close to other "things both apps need" (chat messages, providers, model catalog) without giving WebUi a dependency on a CLI module.
- Kept `CodingAgentSecretRedactor` as a shim so the existing public API and named environment variable (`TAU_CODING_AGENT_REDACT_SECRETS`) keep working for in-flight callers (HTML transcript export, tests). Adding the new `TAU_WEBUI_REDACT_SECRETS` for the WebUi path lets operators tune each surface independently.
- Built a WebUi-specific HTML exporter instead of trying to reuse the Coding Agent's `CodingAgentHtmlSessionExporter`. The two surfaces have different inputs (`WebChatSessionDto` with attachments + tool-call DTOs vs. raw `ChatMessage`) and different fidelity needs (WebUi already produces nicely-shaped DTOs, no need to walk `ContentBlock` types). A small focused renderer keeps both ecosystems decoupled.
- Stamped the footer with "secret redaction enabled" when the redactor is on, so recipients of a shared HTML can tell at a glance whether the file was sanitized.
- Inlined the CSS (rather than linking external) so the resulting `.html` file is fully portable — operators can email it, drop it into a ticket, or open it offline without losing styling.
