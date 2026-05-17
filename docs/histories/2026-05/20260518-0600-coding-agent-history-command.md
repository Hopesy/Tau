# Tau.CodingAgent /history slash command

Date: 2026-05-18

## Summary

Exposed the `InputHistory` populated by the new interactive editor as a Tau-native `/history` slash command. Operators can now ask Tau "what did I type recently?" right from the prompt — default 20 entries newest-first, optional `count` argument, `all` keyword for the full window. Non-interactive sessions (redirected stdin/stdout, or editor disabled) return a clear "history not available" message rather than silently doing nothing.

## Changes

- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`: added `InputHistory.Snapshot(int limit)` returning the most recent entries (newest-first), bounded by the requested limit and existing entry count.
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`:
  - constructor gains an optional `Func<int, IReadOnlyList<string>> historySnapshotProvider`;
  - new `HandleHistoryCommand(parts)` returning an error when no provider is wired (typical for unit tests / non-interactive sessions), an empty-history status, or a numbered list of entries with newlines collapsed to `⏎` markers and long entries truncated at 200 chars;
  - `/history` is dispatched between `/retry` and `/compact`.
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`: added `/history [count|all]` to the supported-commands catalog.
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`: constructor accepts the provider and forwards it to the router.
- `src/Tau.CodingAgent/Program.cs`: when an interactive editor is created, the provider is wired to `editor.History.Snapshot(limit)`.
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`: 5 new tests cover the no-provider error, newest-first listing, explicit count, invalid-argument rejection, and empty-history status. Help-line tests updated to include `/history`.
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`: help-line assertion updated.
- Synced `next.md` and the active port plan with the new command.

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 129 tests passed (5 new + 124 prior, including the two updated help-line assertions).

## Decisions

- Plumbed a `Func<int, IReadOnlyList<string>>` snapshot delegate rather than exposing the editor or history directly through the router. The router is a stateless command dispatcher; keeping it ignorant of `Tau.Tui` types preserves the existing testability story (the new tests inject a tiny lambda) and avoids forcing every router consumer to wire an editor when they only care about commands.
- Provided a clear `Error` status when the provider is unset, matching the existing pattern for `/copy` / `/login` / `/retry` (commands that only make sense in interactive mode). Non-interactive callers see a single, actionable message instead of an empty/null result.
- Truncated entries past 200 characters and collapsed newlines to `⏎` so the listing fits a single line per entry. The full content is still in the history file (and reachable via Ctrl-R reverse search); the listing is a quick scanner, not a verbatim view.
- Numbered the listing newest-first to match `/tree` / `/session` conventions in Tau (most-recent activity at the top). The internal storage is oldest-first, but the user-facing index `[ 1]` always identifies the entry the user typed most recently.
