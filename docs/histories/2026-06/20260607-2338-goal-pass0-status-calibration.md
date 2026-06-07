## [2026-06-07 23:38] | Task: GOAL Pass 0 status calibration

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows PowerShell in C:\Users\zhouh\Desktop\Tau`

### User Query

> Continue according to `GOAL.md`.

### Changes Overview

Scope: goal-related planning/status documents only.

Key Actions:

* Updated the active 100% parity plan checkpoint so old dirty WIP is no longer treated as the current execution entry.
* Updated `next.md` so the direct execution entry now starts with `GOAL.md` Pass 0 status calibration before selecting the next Phase 2 Candidate Queue slice.
* Corrected stale Pods matrix rows for local `--gpus` / `--memory` / `--context` planning, while keeping round-robin allocation, command compatibility and real SSH/HF/GPU/vLLM e2e open.
* Updated `docs/QUALITY_SCORE.md` with this docs-only Pass 0 calibration and corrected older stale WIP wording.

### Design Intent

Pass 0 exists to prevent the long-running 100% parity plan from drifting into stale or contradictory status claims. This change makes current documents agree with HEAD evidence: recent local Pods planning work is real, but it is not final product parity; old dirty WIP boundaries are closed, but the overall 100% migration remains open.

### Files Modified

* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-06/20260607-2338-goal-pass0-status-calibration.md`
