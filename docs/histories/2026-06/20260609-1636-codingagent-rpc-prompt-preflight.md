## [2026-06-09 16:36] | Task: CodingAgent RPC prompt preflight timing

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续 `GOAL.md` 的 100% pi-mono parity 迁移主线。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` RPC host and parity tracking docs.

**Key Actions:**

* **RPC prompt response timing**: Moved `prompt` success response from command acceptance to the local preflight boundary: after the runner produces the first agent event and before that event is written.
* **Preflight error surface**: Runner construction failures and async enumerable failures before the first event now return `response command=prompt success=false error=...` instead of pretending the prompt was accepted.
* **Abort race guard**: Accepted prompt response is written with a non prompt-abort cancellation token so a following `abort` cannot swallow a response that already crossed the preflight boundary.
* **Regression coverage**: Added coverage for synchronous runner startup failure, async first-event preflight failure, successful response ordering before `agent_start`, and existing active prompt `steer` / `follow_up` / `abort` flow.
* **Docs sync**: Updated `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and active parity plan/matrix entries while keeping the overall RPC status `partial`.

### 🧠 Design Intent (Why)

Upstream RPC mode reports `prompt` success only after `AgentSession.prompt(... preflightResult)` accepts the prompt. Tau does not currently expose the same session-level preflight hook, so this slice uses the earliest stable local runtime signal: the first agent event. That keeps command errors available for runner startup and first-event failures, while preserving runtime `agent_end` failure handling after the prompt has been accepted.

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentRpcHostTests" --no-restore --verbosity minimal`：60/60 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter Rpc --no-restore --verbosity minimal`：67/67 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：493/493 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed；Ai 280、Agent 120、Tui 251、CodingAgent 493、WebUi 61、Pods 216.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
