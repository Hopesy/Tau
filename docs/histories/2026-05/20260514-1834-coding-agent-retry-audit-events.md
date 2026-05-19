## [2026-05-14 18:34] | Task: coding-agent retry audit events

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` Tau-native auto-retry JSONL audit baseline

**Key Actions:**

* **[Retry audit entries]**: Added JSONL `auto_retry_start` and `auto_retry_end` persistence for ordinary transient retry attempts, carrying attempt, maxAttempts, delayMs, errorMessage, success, and finalError.
* **[Ordering]**: On recovered retry, the host now syncs the successful user/assistant attempt into the tree before appending `auto_retry_end`. This keeps the audit timeline as start -> successful attempt messages -> end.
* **[Failure semantics]**: Retry exhaustion and retry-delay cancellation keep retry audit entries, but still do not persist failed user input or partial assistant output.
* **[Tree/HTML visibility]**: `/tree --search` and standalone HTML transcript export can surface retry start/end entries, including branch outline/search text and retry timeline styling.
* **[Tests/docs]**: Extended CodingAgent targeted tests for retry JSONL fields, successful ordering, exhausted retry audit retention, tree search, and HTML retry event rendering. README, ARCHITECTURE, QUALITY_SCORE, active plan, and `next.md` now describe this as a Tau-native retry audit event baseline, not full upstream settings/RPC parity.

### 🧠 Design Intent (Why)

The previous host-level retry baseline proved the rollback behavior but left retry attempts invisible in append-only session history. Upstream `pi-coding-agent` emits retry lifecycle events, so Tau needs an auditable equivalent before richer UI or settings control can be credible.

The ordering detail matters: if `auto_retry_end` is appended before syncing the successful attempt, the JSONL timeline implies the retry finished before the recovered user/assistant messages existed. Syncing the runner first preserves a reviewable causal order while still avoiding failed attempt pollution.

This remains a baseline. It does not claim upstream settings/RPC control, retry cancellation UI, or extension event parity.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`

### ✅ Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
* `git diff --check`
