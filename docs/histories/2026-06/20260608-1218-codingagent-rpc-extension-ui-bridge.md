# [2026-06-08 12:18] | Task: CodingAgent RPC extension UI bridge baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell / .NET 10

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity 迁移。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **RPC extension UI bridge**: Added `CodingAgentRpcExtensionUiBridge` to emit upstream-shaped `extension_ui_request` JSONL events for `select`, `confirm`, `input`, `editor`, `notify`, `setStatus`, `setWidget`, `setTitle`, and `set_editor_text`.
* **Response handling**: Updated `CodingAgentRpcHost` to recognize stdin `extension_ui_response` messages and resolve pending UI requests without treating them as normal commands or emitting command responses.
* **Protocol tests**: Added RPC host tests for select value resolution, confirm/input/editor response handling, cancelled editor requests, fire-and-forget request shapes, optional field omission, and pre-cancelled dialog defaults.
* **Docs sync**: Updated the parity matrix, active 100% plan, `next.md`, `docs/QUALITY_SCORE.md`, and architecture notes to move the RPC extension UI protocol from missing to partial while preserving TypeScript extension runtime and package/custom-tool gaps.

### Design Intent

Upstream RPC mode exposes extension UI as JSONL request/response messages so a headless embedding host can render select/confirm/input/editor prompts and receive user responses. Tau already had RPC commands and streamed bash output, but it lacked a protocol surface for extension UI requests. This slice adds the protocol/state-machine seam without pretending that full TypeScript extension runtime or package-loaded extension execution exists.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcExtensionUiBridge.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/ARCHITECTURE.md`

### Upstream Reference

* `packages/coding-agent/src/modes/rpc/rpc-types.ts`
* `packages/coding-agent/src/modes/rpc/rpc-mode.ts`

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentRpcHostTests" --no-restore --verbosity minimal` -> 54/54 passed.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed with 0 warnings and 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 449/449 passed.
* `git diff --check` -> passed; only CRLF normalization warnings were reported for existing docs.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 449, `Tau.WebUi.Tests` 44, and `Tau.Pods.Tests` 215.

### Remaining Boundaries

* TypeScript extension runtime is not implemented in this slice.
* Package-loaded extensions and custom tools remain open.
* Real extension UI calls are not wired into a production extension runner yet.
* RPC client helper and exact TypeScript schema parity remain open.
