## [2026-06-07 21:49] | Task: restore 100 percent parity goal

### Execution Context

- Agent ID: Codex
- Base Model: GPT-5
- Runtime: Codex CLI on Windows / PowerShell

### User Query

> 下一步应该更改目标，所有能力 100% 移植，更新 goal.md 和移植计划。

### Changes Overview

Scope: `GOAL.md`、active parity plan、parity matrix handoff、`next.md`、`docs/QUALITY_SCORE.md`

Key actions:

- Restored `GOAL.md` to `Tau 100% pi-mono parity /goal`, with strict completion criteria covering all upstream capabilities, protocols, commands, config/env/log/schema/release behavior and real e2e closure.
- Marked `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md` as the active execution line again, and treated the completed Agent platform baseline as a shared foundation rather than final product parity.
- Updated the parity matrix handoff to keep Phase 1 inventory frozen and direct Phase 2 workers to the `Phase 2 Candidate Queue`.
- Updated `next.md` so the current P0 is 100% pi-mono parity, while Agent platform baseline is documented as completed prerequisite work.
- Updated `docs/QUALITY_SCORE.md` to reset the quality lens to product parity, external e2e, release/package/CI final closure and matrix completion.

### Design Intent

The Agent platform baseline is valuable infrastructure, but it does not satisfy the user's restored goal of all-capability pi-mono parity. The plan now makes the next route explicit: preserve the completed platform foundation, avoid reopening broad inventory, first respect existing dirty WIP boundaries, then dispatch implementation/e2e slices from the frozen matrix queue.

### Files Modified

- `GOAL.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `next.md`
- `docs/QUALITY_SCORE.md`
- `docs/histories/2026-06/20260607-2149-restore-100-percent-parity-goal.md`

### Validation

- Docs-only goal/plan pivot; no runtime code changed in this task.
- `git diff --check -- GOAL.md docs\exec-plans\active\2026-05-28-tau-100-percent-pi-mono-parity.md docs\exec-plans\active\2026-05-28-pi-mono-parity-matrix.md next.md docs\QUALITY_SCORE.md docs\histories\2026-06\20260607-2149-restore-100-percent-parity-goal.md` passed. PowerShell profile emitted an oh-my-posh init-file lock message, and Git reported CRLF/LF normalization warnings for existing working-copy files; no whitespace error was reported.
