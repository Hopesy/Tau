## [2026-05-14 17:17] | Task: coding-agent auto-retry baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` host-level retryable error recovery baseline

**Key Actions:**

* **[Retry options]**: Added `CodingAgentRetryOptions` with `TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` and `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` environment configuration. Production `Program.cs` now wires these options into `CodingAgentHost`.
* **[Retry classifier]**: Added a Tau-native retry classifier for transient provider/runtime errors such as overloaded, rate limit, 429, 5xx, service unavailable, network/connection/fetch, socket, timeout, and terminated errors. Context overflow is intentionally excluded for the later compact-and-retry path.
* **[Host orchestration]**: Normal user turns now run through a retry-aware helper. Each retryable failure restores the pre-turn snapshot before retrying the same input. A successful retry persists only the successful attempt; non-retryable failures and exhausted retries continue to roll back.
* **[Tests/docs]**: Added host tests for retryable `AgentEndEvent` recovery, non-retryable error no-retry behavior, and retryable exception exhaustion. README, ARCHITECTURE, QUALITY_SCORE, active plan, and `next.md` were updated to describe this as a Tau-native host-level auto-retry baseline.

### 🧠 Design Intent (Why)

Upstream `pi-coding-agent` has richer `auto_retry_start` / `auto_retry_end` event semantics, settings/RPC toggles, retry cancellation, and overflow compact-and-retry orchestration. Tau now has a reliable pre-turn snapshot and flat/tree persistence boundary, so the smallest useful step is to retry transient failures without letting failed attempts pollute session history.

This change deliberately keeps retry at the host boundary. It improves correctness and operator ergonomics for transient provider failures, while leaving full upstream retry event parity and context-overflow compact-and-retry as explicit follow-up work.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRetryOptions.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
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
