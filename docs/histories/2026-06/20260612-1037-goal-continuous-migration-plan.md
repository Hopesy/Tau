## [2026-06-12 10:37] | Task: refresh GOAL continuous migration plan

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### User Query

> 更新下一步计划，把后续详细迁移计划、验收目标和最终目标写入 `GOAL.md`，确保后续能通过 `/goal` 持续运行直到完成迁移。

### Changes Overview

**Scope:** `GOAL.md`, migration planning docs

**Key Actions:**

* **Current checkpoint refresh**: Updated `GOAL.md` from the stale 2026-06-08 checkpoint to the current `f99d1ba feat(coding-agent): align session startup flags` baseline, including clean worktree state and latest verified local gate.
* **Matrix status refresh**: Recorded current matrix counts as `partial=197`, `external-e2e-needed=31`, `ported=32`, `missing=1`, `non-goal-proposed=1`, `verified=0`.
* **Continuous execution contract**: Added a per-round acceptance checklist and `/goal` slice selection algorithm so future runs can choose the next slice without relying on chat context.
* **Next priority update**: Replaced stale `missing=7` guidance with current priorities: root release/scripts parity, CodingAgent SessionManager parity, AI/Agent package boundary, WebUi branch/tree runtime, and runtime/e2e lanes.

### Design Intent (Why)

`GOAL.md` is the durable controller for long-running Tau migration. It must reflect current repo evidence, strict final acceptance, and an executable next-step policy. The update keeps the 100% parity target strict while making each future `/goal` turn small, verifiable, and reviewable.

### Files Modified

* `GOAL.md`
* `docs/histories/2026-06/20260612-1037-goal-continuous-migration-plan.md`

### Validation

* `git diff --check`
* `rg -n "missing=7|partial=191|70bdac2|2026-06-08|missing=1|f99d1ba|Per-round acceptance checklist|/goal slice selection algorithm|Root release/scripts parity" GOAL.md`

This was a docs-only controller update, so no .NET build/test gate was required for this slice.
