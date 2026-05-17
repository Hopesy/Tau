# Tau.CodingAgent /find command + global.json adjustment

Date: 2026-05-18

## Summary

Two changes in one slice:

1. **`/find <pattern>` slash command** for Tau.CodingAgent. Greps the current session's message contents (TextContent / ToolCallContent.Arguments / ToolResultMessage children) for a case-insensitive substring, prints role + 1-based index + a truncated context window per match.
2. **Pinned `global.json` to 10.0.104** — the previously pinned `10.0.201` SDK was uninstalled mid-session (only the shell `Roslyn` subdir remained), which made `dotnet build` reject with `[10.0.201] SDK not found`. `latestFeature` rollForward lets the build pick up newer 10.0.x feature bands when they reappear on disk.

## Changes

- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`: dispatches `/find`; new `HandleFindCommand(input, parts)` + `AppendContentMatches(...)` helper that walks `IReadOnlyList<ContentBlock>` and produces `[idx] role: …context…` lines with newlines collapsed to `⏎` markers.
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`: registers `/find <pattern>` between `/history` and `/clear`.
- `global.json`: pinned to `10.0.104` (the lowest installed 10.0.x SDK band) with `rollForward: latestFeature` so future feature-band installs are picked up automatically.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: 4 new tests cover the happy path with multiple messages, case-insensitive match, no-match status, and missing-pattern usage error. Help-line assertion updated.
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`: help-line assertion updated.
- Synced `next.md` (`/find` bullet) and the active port plan (verification list).

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded after the global.json adjustment.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 155 tests passed (4 new + 151 prior, with two help-line assertion updates).

## Decisions

- Modeled `/find` on `/history` rather than on `/tree --search`. `/find` answers "what did anyone say about X?" against the in-memory runner; `/tree --search` works against the JSONL audit log. The two complement each other and have separate latency profiles — runner search is instant; tree search may scan a large append-only file.
- Truncated each match preview to a 40-char radius around the hit and replaced newlines with `⏎`. A long single-line message is still readable; a multi-line tool result doesn't blow up the listing.
- Did **not** support regex patterns. Substring search keeps the implementation predictable and avoids edge cases around quoting regex metacharacters in the slash command surface. A `/find-regex` could layer on later if real users ask.
- Adjusted `global.json` rather than trying to chase the volatile preview SDK. Pinning the lowest installed feature band with `latestFeature` rollForward means future SDK installs (200, 300, ...) are picked up automatically without further config changes.
