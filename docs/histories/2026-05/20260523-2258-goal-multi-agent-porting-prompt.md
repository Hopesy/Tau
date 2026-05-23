## [2026-05-23 22:58] | Task: Goal multi-agent porting prompt

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 新建 `GOAL.md`，把 `/goal` 多 agent 并行移植提示词写进去。

### Changes Overview

**Scope:** repo collaboration docs

**Key Actions:**

* Added root `GOAL.md` with a reusable `/goal` prompt for continuously porting `pi-mono-main` into Tau.
* Captured the main-agent / worker-agent split: integrator owns shared architecture, plan, merge, verification, docs, and history; workers own disjoint module implementation slices.
* Added worker ownership rules, default shared-file restrictions, suggested initial module workers, a worker task template, architecture-change rules, and acceptance criteria.

### Design Intent (Why)

Tau is a porting project with clear source and target repos, so long-running `/goal` work should not rely on chat memory alone. The prompt now makes multi-agent parallelism explicit while preserving harness-init discipline: shared docs and architecture stay under one integrator, module workers get bounded write scopes, and every slice must end with evidence.

### Files Modified

* `GOAL.md`
* `docs/histories/2026-05/20260523-2258-goal-multi-agent-porting-prompt.md`
