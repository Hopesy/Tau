## [2026-05-14 16:21] | Task: coding-agent failed turn rollback

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` host/session failure handling baseline

**Key Actions:**

* **[Turn snapshot]**: `CodingAgentHost` now captures a pre-turn snapshot of messages, provider, model, and session name before invoking the runner for a normal user message.
* **[Rollback]**: Runner exceptions, cancellation during a normal turn, and error-bearing `AgentEndEvent` now restore that snapshot and report a rollback status before session persistence.
* **[Session persistence]**: Flat JSON session and JSONL tree session persistence now see the restored state, so failed user input, partial assistant messages, or partial tool results are not written as successful session history.
* **[Tests/docs]**: Added host regression coverage for exception rollback and error-event rollback across flat/tree session stores; README, ARCHITECTURE, QUALITY_SCORE, active plan, and `next.md` now describe this as a Tau-native failed-turn rollback baseline.

### 🧠 Design Intent (Why)

Upstream `pi-coding-agent` has richer retry and recovery behavior, but Tau first needed to close a more basic correctness hole: `RuntimeCodingAgentRunner.RunAsync()` appends the user message before provider/runtime work starts. If the turn then fails, the old host path reported the error and persisted the mutated runtime state, which made a failed prompt look like accepted history in both flat and JSONL sessions.

This change deliberately stops at a local, testable rollback boundary. It does not claim full upstream auto-retry parity; retryable error backoff, `auto_retry_start` / `auto_retry_end`, and overflow compact-and-retry remain explicit follow-up work.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`

### ✅ Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
* `git diff --check`
