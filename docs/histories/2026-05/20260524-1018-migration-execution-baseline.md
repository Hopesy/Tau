## [2026-05-24 10:18] | Task: migration execution baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### 📥 User Query

> 多agent并行，降低文档同步速度，少做单元测试，尽快推进移植速度，写到核心基线里
> 后续更正：应该写到agents.md

### 🛠 Changes Overview

**Scope:** migration execution rules

**Key Actions:**

* **[Execution policy]**: Added a 2026-05-24 execution strategy baseline to the complete pi-mono port plan.
* **[Agent entrypoint]**: Promoted the same execution defaults into `AGENTS.md` so future agents see the rule before reading deeper plans.
* **[Parallel workflow]**: Recorded that isolated module slices should default to 4-6 parallel workers, with docs/scripts/shared validation centralized by the main orchestrator.
* **[Lower process drag]**: Narrowed default documentation sync and unit-test expectations to risk-based, behavior-facing updates instead of broad per-slice churn.

### 🧠 Design Intent (Why)

The migration has reached a stage where repeated broad docs updates and exhaustive unit-test expansion can consume more time than moving parity forward. The new baseline preserves evidence and safety where it matters: public behavior, shared runtime contracts, persistence/auth/security boundaries, and batch-level validation.

### 📁 Files Modified

* `AGENTS.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1018-migration-execution-baseline.md`
