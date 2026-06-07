## [2026-06-07 23:56] | Task: 更新 100% 移植验收账本

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau harness-init collaboration mode`

### User Query

> 用户要求全面审视 Tau 距离 pi-mono 100% 移植还缺什么，并把目标计划更新到 `GOAL.md`，验收标准固定为所有能力 100% 移植。

### Changes Overview

**Scope:** `GOAL.md`、`docs/histories/2026-06/**`

**Key Actions:**

* **[Audit Snapshot]**: 在 `GOAL.md` 增加当前审计快照，记录上游 7 个 package、root scripts、matrix 未闭合状态统计和当前工作树边界。
* **[Acceptance Ledger]**: 增加 100% 验收账本，明确 inventory、contract、runtime、external e2e、release/package、docs/history、final validation 的完成门槛。
* **[Gap Classification]**: 增加后续执行分类计划，把后续 `/goal` 切片固定为 `contract`、`runtime`、`external-e2e`、`release-package`、`final-audit` 五类。

### Design Intent (Why)

用户目标是 100% 移植，不接受把本地 baseline、fake/stub tests 或局部 planning 误判为完成。`GOAL.md` 需要把当前未完善项、验收门槛和后续执行分类写成可复用的主控合同，保证后续 agent 按 matrix 和真实验证推进。

### Files Modified

* `GOAL.md`
* `docs/histories/2026-06/20260607-2356-goal-acceptance-ledger.md`
