## [2026-05-14 17:50] | Task: coding-agent overflow compact-and-retry

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` context overflow recovery baseline

**Key Actions:**

* **[Overflow classifier]**: Extended `CodingAgentRetryClassifier` with explicit context-overflow detection and kept overflow out of the ordinary transient retry path.
* **[Host recovery]**: Added a host-level compact-and-retry path for context overflow failures. The host restores the pre-turn snapshot, runs compaction with overflow-specific recovery instructions, records a `fromHook=true` JSONL compaction boundary, persists the compacted session, then retries the same user input once.
* **[Rollback boundary]**: After successful overflow compaction, the compacted session becomes the new rollback baseline. This keeps runner state aligned with append-only JSONL history if the retry fails after a compaction entry has already been written.
* **[Failure behavior]**: If overflow compaction fails, the host rolls back to the original snapshot and does not persist the failed user input, partial assistant output, or a bogus compaction entry.
* **[Tests/docs]**: Added targeted host tests for successful context-overflow compaction retry and compaction-failure rollback. README, ARCHITECTURE, QUALITY_SCORE, active plan, and `next.md` describe this as a Tau-native baseline, not full upstream retry/compaction parity.

### 🧠 Design Intent (Why)

Upstream `pi-coding-agent` treats context overflow differently from ordinary transient provider failures: it compacts the current session and retries the pending request. Tau already had pre-turn rollback, JSONL compaction entries, and host-level transient retry, so the smallest correct step was to route overflow errors through compaction instead of exponential retry.

The important boundary is append-only JSONL consistency. Once a successful overflow recovery writes a compaction entry, later retry rollback must not restore the runner to the pre-compaction state while leaving the tree file with a compaction boundary. Updating the rollback baseline after successful compaction keeps flat session, tree session, and runner messages coherent.

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRetryOptions.cs`
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
