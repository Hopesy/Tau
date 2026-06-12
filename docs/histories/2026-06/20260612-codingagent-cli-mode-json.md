# CodingAgent CLI `--mode json` single-shot output baseline

- **When**: 2026-06-12
- **Agent**: Main Integrator (Claude)
- **Blocker class**: `contract`
- **Slice**: CodingAgent `--mode json` print-mode JSON event stream parity

## Why

Upstream `packages/coding-agent/src/cli/args.ts` + `main.ts` `resolveAppMode` define
`--mode json` as a distinct single-shot app mode (peer of `text` / `rpc`): non-interactive,
prints the session header as the first JSON line, then emits every agent event as a JSON
line via `modes/print-mode.ts` (`writeRawStdout(JSON.stringify(event))`), and still exits
non-zero on an error/aborted final assistant message. Tau's help text already advertised
`--mode <mode>  Output mode: text (default), json, or rpc`, but the parser only recognized
`rpc`; any `--mode json` was silently downgraded to plain text streaming. This closed an
advertised-but-missing CLI contract.

## What changed

- `CodingAgentInitialMessageBuilder.cs` (`CodingAgentCliArguments`):
  - `--mode` / `--mode=` now set a new `JsonMode` flag when the value is `json` (in addition
    to the existing `rpc` handling). Record gained `bool JsonMode`.
- `Program.cs`:
  - `jsonMode` is read from the parsed args and implies print mode (mirrors upstream
    `resolveAppMode` returning `json` -> non-interactive print path).
  - When json mode is active and a tree session controller exists, the upstream-compatible
    session header line is emitted as the first JSONL line before events (best-effort; a
    header read failure does not block the event stream).
  - `CodingAgentPrintMode` is constructed with `jsonMode: jsonMode`.
- `CodingAgentPrintMode.cs`:
  - New `jsonMode` constructor overload. In json mode, each `AgentEvent` is emitted as a JSON
    line using the RPC host's existing schema (`CodingAgentRpcHost.SerializeEventLine`), the
    trailing text newline is suppressed, and on an error end event the process returns 1 while
    leaving stderr quiet (the error stays on the event stream, matching upstream). Text mode
    behavior is unchanged.
- `CodingAgentRpcHost.cs`:
  - Exposed two internal serialization seams that reuse the existing `ToRpcEvent` projection
    and `JsonOptions` (camelCase, omit-null): `SerializeEventLine(AgentEvent)` and
    `SerializeHeaderLine(CodingAgentTreeSessionHeaderInfo)`. No RPC schema change; json print
    mode and RPC mode now share one event schema.
- `CodingAgentTreeSessionStore.cs`:
  - Added `CodingAgentTreeSessionHeaderInfo` record (`Version?`, `Id`, `Timestamp`, `Cwd`,
    `ParentSession`, computed `Type="session"`) plus `GetSessionHeader()` on the store and the
    `CodingAgentTreeSessionController` to surface the JSONL header faithfully.

## Validation

- `dotnet build src/Tau.CodingAgent` clean.
- Targeted `Tau.CodingAgent.Tests` 71 print-mode/CLI-parser tests pass, including new:
  - `Parse` recognizes `--mode json` / `--mode=json` (and does not set rpc).
  - json print mode emits one JSON line per event with upstream `type` fields.
  - json print mode returns 1 on an error end event while keeping stderr empty.
- Full project gate `verify-dotnet.ps1 -SkipRestore` green: Ai 287, Agent 123, Tui 251,
  CodingAgent 629, WebUi 61, Pods 216.

## Still open (not claimed)

- Exact upstream stdout-takeover / output-guard semantics, signal handling (SIGTERM/SIGHUP)
  and full session-event field parity beyond the shared RPC projection.
- Real provider/auth e2e for json single-shot runs.
- This slice only closes the `--mode json` CLI contract + shared event/header serialization;
  it is not final CodingAgent `verified` status.
