## [2026-06-08 00:34] | Task: 更新 100% 移植目标计划

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 目标是 100% 移植；先全面审视还有哪些没完善，然后把计划更新到 GOAL.md，验收标准是 100% 移植。

### 🛠 Changes Overview

**Scope:** `GOAL.md` parity 目标与执行计划

**Key Actions:**

* **[Audit refresh]**: 重新核对当前 git 状态、上游 package/root script 集合、active parity matrix 状态计数和当前未完成类别。
* **[Goal plan update]**: 把 `GOAL.md` 的审计快照更新为 2026-06-08 当前事实，明确 `partial=187`、`external-e2e-needed=31`、`ported=31`、`missing=12`、`non-goal-proposed=1`、`verified=0`。
* **[Acceptance tightening]**: 明确 100% 验收只能由 matrix 全 `verified` 或用户确认 `non-goal`、`next.md` parity backlog 清零、本地/外部/release gate 全通过来关闭。
* **[Follow-up calibration]**: 追加 `41f1ec6 feat(pods): align default config loading with upstream` 后的当前状态：`main` 与 `origin/main` 对齐，本轮没有实现 WIP，仅保留外部 `.github/workflows/tau-ci.yml` 删除脏项；该删除不能作为本轮 100% parity 证据处理。
* **[Incomplete ledger]**: 在 `GOAL.md` 增补当前未完成 surface ledger，按 Global/Ai/Agent/CodingAgent/Tui/WebUi/Mom/Pods 汇总 100% 验收前必须关闭的合同、运行态、外部 e2e 和 release/package 缺口。

### 🧠 Design Intent (Why)

把目标从“局部能力完成”收紧为可审计的 100% parity 合同，避免后续把 fake/stub tests、本地 planning baseline、`ported` 状态或缺环境的 e2e 当作最终完成证据。

本轮追加校准的动机是修正旧计划中的 stale 当前态：Pods config path/env 已在后续提交关闭，但 upstream record-shaped `pods` / `models` config schema、active/model state round-trip、真实 Pods remote e2e 和所有 matrix `verified` 证据仍未完成。

### 📁 Files Modified

* `GOAL.md`
* `docs/histories/2026-06/20260608-0034-goal-100-percent-audit-plan.md`
