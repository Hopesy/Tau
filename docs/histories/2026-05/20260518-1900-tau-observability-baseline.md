# Tau minimal observability baseline

Date: 2026-05-18

## Summary

Added a minimal JSONL-based observability sink for Tau and wired the agent run lifecycle through it. The first observable surface is the runner: every `RuntimeCodingAgentRunner.RunAsync` call emits `agent / run.start`, then either `agent / run.end` (success), `agent / run.cancel` (cooperatively cancelled), or `agent / run.error` (provider/runtime threw). When the CLI starts, it opens (or appends to) `./.tau/log.jsonl` by default; `TAU_LOG_FILE` overrides the path; `TAU_LOG_DISABLED=1` switches the sink to no-op. A failure opening the log file does not block the CLI from starting.

This is a P2 baseline — meant to give every future feature a stable place to emit structured events without inventing per-component logging. Auth, tool execution, session, delegation, and pod probe events can layer onto the same sink in follow-ups.

## Changes

- `src/Tau.Ai/Observability/ITauLogSink.cs`: new `TauLogEvent(Category, Event, Timestamp, Fields)` record, `ITauLogSink.Log(evt)`, and `NullTauLogSink.Instance` no-op.
- `src/Tau.Ai/Observability/JsonlTauLogSink.cs`: append-only JSONL writer. Builds JSON by hand (no `System.Text.Json` `Serialize<T>` reflection so the project's AOT trimmer settings stay happy), escapes control characters and quotes, creates parent directories, locks the writer, and disposes idempotently. `FromEnvironment()` returns a sink configured from `TAU_LOG_FILE` (explicit path), `null` when `TAU_LOG_DISABLED=1`, or a default sink pointing at `./.tau/log.jsonl`.
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`: constructor and `Create(...)` factory take an optional `ITauLogSink? logSink` (default `NullTauLogSink.Instance`). `RunAsync` wraps the inner enumerable in an `InstrumentRun` helper that emits `run.start` before the first `MoveNextAsync`, `run.error` on `Exception`, `run.cancel` on `OperationCanceledException`, and `run.end` otherwise. Fields include `provider`, `model`, `inputBytes`, and `elapsedMs`.
- `src/Tau.CodingAgent/Program.cs`: opens `JsonlTauLogSink.FromEnvironment()` in a `try/catch` so a misconfigured path never blocks the CLI; passes the sink into `RuntimeCodingAgentRunner.Create(...)`.
- `tests/Tau.Ai.Tests/JsonlTauLogSinkTests.cs` (new, 6 tests): null sink no-ops; sink writes one JSON object per line; quotes / control chars escape correctly and round-trip through `JsonDocument`; missing parent directory is created; `TAU_LOG_FILE` overrides path; `TAU_LOG_DISABLED=1` returns null.
- `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs` (2 new tests): happy path emits exactly one `run.start` and one `run.end` with the expected `provider` / `model` / `elapsedMs` fields, and no `run.error` / `run.cancel`; provider exception emits `run.error` with `error` (type name) + `message` fields and *no* `run.end`. A small `RecordingLogSink` + `FactoryStreamProvider` provide deterministic coverage.

## Verification

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --verbosity minimal` — succeeded, 0 warnings.
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --verbosity minimal` — 191/191 (185 prior + 6 new).
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --verbosity minimal` — 168/168 (166 prior + 2 new).

## Decisions

- **Hand-rolled JSON, not `JsonSerializer.Serialize<T>`**: `Tau.Ai.csproj` builds with AOT analyzers enabled (`IL2026` / `IL3050`). Going through `System.Text.Json` source generation for a 3-field record + a string dictionary would have been more setup than the savings warrant; the writer's field types are constrained enough that hand-escaping is straightforward and the unit tests round-trip the output through `JsonDocument.Parse` to verify correctness.
- **`run.cancel` vs `run.end` distinguished, but errors swallow the `finally` end event**: a cancelled run is still a clean termination; an errored run shouldn't emit both `error` and `end` because consumers would have to deduplicate. Tests verify this contract explicitly.
- **No static `TauLog.Current` facade**: a static would have been smaller to plumb but introduces order-of-init concerns and forces tests to reset shared state. Threading `ITauLogSink?` through `RuntimeCodingAgentRunner.Create(...)` keeps the dependency explicit while only touching one call site per CLI launch.
- **Sink lifetime not explicitly disposed at process exit**: the underlying `StreamWriter` has `AutoFlush = true` so no events are dropped on process termination; the OS reclaims the file handle. A clean shutdown could add `using var sink = ...` and explicit dispose, but the cost in Program.cs scaffolding doesn't pay for itself yet.
- **Auth / tool / pod probe / Mom delegation surfaces deferred**: scope-controlled this slice to one observable surface (the runner) so the sink contract gets exercised end-to-end before being adopted in five more places. Each follow-up wiring is now a small, separate change.
