# Tau.CodingAgent HTML secret redaction

Date: 2026-05-18

## Summary

Closed the export-path side of the security `[~]` checklist: HTML transcript exports (`/export` and `/share`) now run user-visible text through a `CodingAgentSecretRedactor` that replaces common credential patterns with `[redacted]`. Default behavior is opt-out via `TAU_CODING_AGENT_REDACT_SECRETS=0`. Patterns covered: AWS access key IDs (AKIA/ASIA/AROA/...), GitHub PATs (ghp_/gho_/ghu_/ghs_/ghr_), Slack tokens (xoxa-/xoxb-/xoxp-/xoxr-/xoxs-), Anthropic `sk-ant-` keys, OpenAI `sk-` keys, `Bearer <token>` headers, and JWT three-segment tokens.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentSecretRedactor.cs`: new redactor with `GeneratedRegex` patterns, an `IsEnabledFromEnvironment` helper, and a `Default` singleton driven by `TAU_CODING_AGENT_REDACT_SECRETS`.
- `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`: `Export` now accepts an optional `CodingAgentSecretRedactor` (defaults to the env-driven singleton). When enabled, messages are walked and replaced with redacted copies (TextContent / ThinkingContent / ToolCallContent.Arguments / AssistantMessage.ErrorMessage / ToolResultMessage children); image / tool-call id+name fields are left intact. The same redactor also runs over the embedded JSONL session blob.
- `src/Tau.CodingAgent/Tau.CodingAgent.csproj`: added `InternalsVisibleTo Tau.CodingAgent.Tests` so the new env-parsing helper can be unit-tested without poking real process env (avoiding parallel-test interference).
- `tests/Tau.CodingAgent.Tests/CodingAgentSecretRedactorTests.cs`: 11 new tests covering disabled passthrough, AWS / GitHub / Slack / Anthropic / OpenAI / Bearer / JWT patterns, plain-text non-matches, and `IsEnabledFromEnvironment` truth table.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: new `TryHandleAsync_ExportHtmlCommand_RedactsSecretsByDefault` regression verifying that `/export <html>` strips AWS / Bearer / Anthropic patterns out of the rendered HTML while leaving unrelated text intact.
- Synced `docs/SECURITY.md` (added the redaction default + escape hatch), `next.md` (secret bullet now mentions HTML redaction), and the active port plan (verification list + decision entry).

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 141 tests passed (12 new + 129 prior).

## Decisions

- Defaulted to redaction-on for export paths because `/export` and `/share` are explicit outbound-sharing flows. Even a single leaked AWS access key in a shared HTML can rotate fleets; the cost of redacting one false-positive paragraph is far smaller than leaking real credentials. `TAU_CODING_AGENT_REDACT_SECRETS=0` keeps the original transcript reachable for local debugging.
- Pre-processed messages at the boundary (`RedactMessages` → new record instances) rather than weaving redaction into every render branch. Single-point processing is auditable, keeps the renderer pure, and means future export shapes (e.g., Tau-share viewer payload) reuse the same redactor without re-walking the messages.
- Patterns intentionally err on the side of clear secret formats (prefix + length) rather than generic high-entropy detection. False positives on long hashes / SHA digests would be much more disruptive than missing an unusual provider's key shape; the redactor can grow with concrete user reports.
- Used `[GeneratedRegex]` source-generation so the compiled regexes are AOT-friendly and zero-allocation across calls.
- Exposed `IsEnabledFromEnvironment` via `InternalsVisibleTo` so tests can validate the truth table without mutating real env vars; this avoids the kind of cross-test env-pollution race that recently broke the Bedrock collection.
