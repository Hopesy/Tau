# Tau.Tui persistent input history

Date: 2026-05-18

## Summary

Made `InputHistory` persistable through a small `IInputHistoryStore` seam and shipped a `FileInputHistoryStore` that round-trips entries to `~/.tau/coding-agent-history` (overridable via `TAU_CODING_AGENT_HISTORY_FILE`). Tau.CodingAgent now hydrates the in-memory history from disk at startup and appends each submitted line back to the file. Reverse-search, Up/Down navigation, and duplicate-drop behavior are unchanged.

## Changes

- `src/Tau.Tui/Runtime/InputHistoryStore.cs`: new `IInputHistoryStore` (`Load()` / `Append(entry)`) and `FileInputHistoryStore` with capacity-bounded read, best-effort append, and post-write truncation when the file grows past `maxEntries`.
- `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`: `InputHistory` accepts an optional `IInputHistoryStore`; on construction it loads existing entries (preserving order, skipping blanks); `Add` deduplicates consecutive identical entries before writing to the store so the in-memory and on-disk view stay aligned.
- `src/Tau.CodingAgent/Program.cs`: `CreateInteractiveEditorIfAttached` now resolves `TAU_CODING_AGENT_HISTORY_FILE` (or `~/.tau/coding-agent-history`), wires it into a `FileInputHistoryStore`, and constructs the editor with the resulting `InputHistory`.
- `tests/Tau.Tui.Tests/InputHistoryStoreTests.cs`: new test file with 6 cases — store hydration, append-on-add, consecutive-duplicate dedup, file round-trip across two `InputHistory` instances, `maxEntries` truncation, and missing-file resilience.
- Synced `next.md` and the active port plan with the new persistence behavior.

## Verification

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 45 tests passed (39 prior + 6 new).

## Decisions

- Built `IInputHistoryStore` as a tiny seam rather than embedding file I/O into `InputHistory`. Tests inject an in-memory store; production injects a file store; future stores (JSONL with timestamps, encrypted store, shared host directory) plug into the same shape without changing the editor's contract.
- Wrote append-only with periodic truncation rather than rewriting on every commit. The file is small enough that an `AppendAllText` per submit is cheap, but trimming with `ReadAllLines` + `WriteAllLines` only fires when the file exceeds `maxEntries`. That keeps the steady-state cost O(1) per submit and the worst case bounded by capacity.
- Made all file errors best-effort. The history is an editor convenience, not a correctness-critical asset: a read-only `$HOME`, a missing directory, or a transient lock should never crash the prompt. Silent fallback to in-memory-only preserves the editor's interactive guarantees.
- Routed the path through env var first, then user-profile default, mirroring the existing Tau pattern for `*_FILE` overrides. Operators can redirect history to a project-local file (e.g., for CI consistency) without touching the editor wiring.
