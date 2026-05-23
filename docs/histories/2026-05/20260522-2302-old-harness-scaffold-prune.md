## [2026-05-22 23:02] | Task: 清理旧 harness-init 脚手架

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> Tau 仓库是用旧版 harness-init 初始化的，删除不需要的文件。

### 🛠 Changes Overview

**Scope:** 旧 harness-init 通用脚手架、仓库导航文档、稳定性/安全说明。

**Key Actions:**

* **[删除旧脚手架]**: 删除 `Makefile`、`CONTRIBUTING.md`、旧通用仓库检查/初始化/打包脚本，以及与当前仓库事实不一致的 CI/CD 和供应链占位文档。
* **[删除旧占位]**: 删除空的 generated docs 占位和仍在描述模板本身的 design-docs 目录。
* **[保留项目资产]**: 保留 Tau 的真实 .NET 验证脚本、模型生成脚本、产品/架构/质量/设计/安全/稳定性文档和迁移计划。
* **[同步引用]**: 更新 `AGENTS.md`、`docs/SECURITY.md`、`docs/RELIABILITY.md`、`README.md`、release notes 和 active plan 中指向旧脚手架或模板仓库的引用。

### 🧠 Design Intent (Why)

旧版 harness-init 默认铺了 Makefile、GitHub workflow、仓库卫生检查、release package 和供应链占位文档。当前瘦身版已经不再默认带这些文件；Tau 也没有对应 `.github/workflows`，旧脚本继续存在会制造错误的验证入口和过期上下文。本轮只删除明确旧模板遗留且不再代表 Tau 真实状态的文件，避免误删已经承载项目事实的文档和脚本。

### 📁 Files Modified

* `Makefile`
* `CONTRIBUTING.md`
* `AGENTS.md`
* `README.md`
* `docs/SECURITY.md`
* `docs/RELIABILITY.md`
* `docs/releases/feature-release-notes.md`
* `docs/CICD.md`
* `docs/SUPPLY_CHAIN_SECURITY.md`
* `docs/generated/README.md`
* `docs/design-docs/core-beliefs.md`
* `docs/design-docs/index.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/completed/2026-05-22-old-harness-scaffold-prune.md`
* `scripts/check-action-pinning.sh`
* `scripts/check-docs.sh`
* `scripts/check-repo-hygiene.sh`
* `scripts/ci.sh`
* `scripts/init-project.sh`
* `scripts/new-exec-plan.sh`
* `scripts/new-history.sh`
* `scripts/release-package.sh`
