## [2026-05-29 03:51] | Task: release archive baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮聚焦 Phase 5 release delivery parity。

### Changes Overview

**Scope:** `scripts/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **新增 release archive 打包脚本**: 新增 `scripts/package-release-artifacts.ps1`，消费已有 `artifacts/tau-<rid>/`，校验 `manifest.json` 后生成 `artifacts/releases/tau-<rid>.zip`。
* **补 clean extract smoke**: 打包脚本会把 zip 解压到全新临时目录，并复用 `scripts/smoke-release-artifacts.ps1` 验证 extracted artifact 中的 `tau-ai` / `pi-ai`、CodingAgent RPC、Pods help、WebUi 和 Mom `--once` smoke。
* **同步 Phase 5 文档状态**: 更新 README、parity matrix、100% parity plan、`next.md` 和质量评分，明确本轮关闭的是 current-RID zip archive + extraction smoke baseline，不等于全平台 archive matrix、Unix tar.gz、release automation 或真实外部 e2e 完成。

### Design Intent (Why)

上游 `scripts/build-binaries.sh` 不只生成可执行目录，还创建 release archive 并重新解压测试。Tau 已有 unpacked current-RID artifact 与 smoke，本轮补上独立 archive 层，避免把原目录可运行误当成交付归档可运行。脚本保持 PowerShell-first，使用 .NET `System.IO.Compression`，不依赖外部 `zip` 工具；删除临时解压目录前确认路径位于系统 temp 根下。

### Files Modified

* `scripts/package-release-artifacts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
