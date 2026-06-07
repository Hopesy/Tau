## [2026-06-07 23:28] | Task: update 100% migration goal plan

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows PowerShell in C:\Users\zhouh\Desktop\Tau`

### User Query

> 用户要求把当前目标明确为 100% 移植，先全面审视还未完善的缺口，并把执行计划更新到 `GOAL.md`，验收标准为 100% 移植。

### Changes Overview

Scope: repository goal and collaboration history only.

Key Actions:

* Updated `GOAL.md` with a current 100% gap map across global scripts/release, Tau.Ai, Tau.Agent, Tau.CodingAgent, Tau.Tui, Tau.WebUi, Tau.Mom and Tau.Pods.
* Added a pass-based 100% migration execution plan covering status calibration, contract closure, runtime parity, external e2e, release/package parity and final audit.
* Tightened the next execution priority so future work starts with stale status cleanup and then proceeds through the Phase 2 Candidate Queue without reopening broad inventory.

### Design Intent

The previous goal file already restored the 100% pi-mono parity objective, but it mixed checkpoint guidance with completion criteria and did not expose a current module-by-module closure plan. This update turns the goal into a stricter integrator checklist: local tests, fake providers and planning baselines remain useful evidence, but final acceptance requires verified behavior or user-confirmed non-goals.

### Files Modified

* `GOAL.md`
* `docs/histories/2026-06/20260607-2328-goal-100-percent-migration-plan.md`
