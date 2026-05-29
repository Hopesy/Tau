## [2026-05-29 13:21] | Task: release plan dry-run baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** `scripts/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **Release dry-run planner**: 新增 `scripts/plan-release.ps1`，对照上游 `scripts/release.mjs` 的 clean worktree、version bump / explicit semver、changelog release section、commit/tag、publish 和 push 流程生成 Tau dry-run release plan。
* **Preflight checks**: 脚本会检查 `git status`、Tau release notes、release build/package/smoke/no-env scripts，并扫描 MSBuild version properties。
* **Version planning**: 支持 `major|minor|patch` 和显式 `x.y.z`；当前 Tau 没有 repo-owned `Version` / `VersionPrefix` / `PackageVersion`，因此 bump 目标必须传 `-CurrentVersion x.y.z` 才能计算，显式版本会记录 comparison-unchecked warning。
* **Non-execution boundary**: 输出 no-env gate、release matrix build/package command list，并明确列出不会执行的版本写入、release notes 修改、history/commit/tag/publish/push 操作。
* **Docs sync**: README、quality score、100% plan、parity matrix 和 `next.md` 同步说明本轮只关闭 planning / dry-run audit baseline，不声明真实 release execution automation。

### Design Intent (Why)

上游 `release.mjs` 会直接改版本、改 changelog、提交、打 tag、publish 并 push。Tau 当前还没有仓库内产品版本事实源，也仍有非宿主 smoke、外部 e2e 和 payload parity 缺口。直接实现执行型 release automation 会把未完成验收包装成可发布流程。因此本轮先把 release automation 的审计面落成 dry-run planner：让下一步 release execution 能基于同一组检查和命令扩展，同时避免任何 tag、publish 或 push 副作用。

### Files Modified

* `scripts/plan-release.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `git diff --check`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -CurrentVersion 0.1.0 -AllowDirty -Runtimes win-x64`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 0.2.0 -AllowDirty -Runtimes win-x64 -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -Runtimes win-x64 -AllowDirty`

### Validation Result

* `git diff --check` passed. Git only reported CRLF normalization warnings for edited docs files.
* `plan-release.ps1 patch -CurrentVersion 0.1.0 -AllowDirty -Runtimes win-x64` passed and planned `0.1.1`, reporting the dirty worktree as an allowed planning-only warning.
* `plan-release.ps1 0.2.0 -AllowDirty -Runtimes win-x64 -Json` passed and emitted JSON with `dryRun=true`, `nextVersion=0.2.0`, `currentVersion.status=missing`, `comparison-unchecked` warning, planned commands, upstream release mapping and non-executed mutations.
* `plan-release.ps1 patch -Runtimes win-x64 -AllowDirty` exited `1` as expected because Tau currently has no repo-owned semantic version source and no `-CurrentVersion` was supplied.
