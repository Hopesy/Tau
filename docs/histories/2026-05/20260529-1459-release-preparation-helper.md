## [2026-05-29 14:59] | Task: guarded release preparation helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 pi-mono -> Tau 100% parity；当前切片继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** release scripts, README, active parity plans, quality/next docs

**Key Actions:**

* **Guarded preparation script**: 新增 `scripts/prepare-release.ps1`，组合 `update-release-version.ps1` 与 `update-release-notes.ps1`，默认 dry-run，显式 `-Apply` 才进入写入路径。
* **Clean-worktree guard**: `-Apply` 要求工作树干净；`-AllowDirty` 只允许 dry-run 在脏工作树里继续预览。
* **Helper preflight**: apply 前先跑 version helper 与 release notes helper 的 dry-run 预检，再写回 `Directory.Build.props` 和 `docs/releases/feature-release-notes.md`，降低 release notes 表结构错误导致半写入的风险。
* **Planner integration**: `scripts/plan-release.ps1` 把 `prepare-release.ps1` 纳入 required scripts 和 planned commands，并把非执行 mutation 指向 guarded preparation flow。
* **Docs sync**: README、QUALITY、active 100% plan、parity matrix 和 `next.md` 同步说明当前只关闭本地 release preparation 编排 baseline，不声明 no-env gate、release matrix、commit/tag/publish/push 或外部 e2e release 完成。

### Design Intent (Why)

上游 `scripts/release.mjs` 是完整 release flow：clean worktree、版本写回、changelog、commit、tag、publish 和 push。Tau 当前还没有完成发布执行自动化，也不能让 planning script 产生远端或版本副作用。因此本切片只把已经存在的两个窄 helper 串成一个本地准备层，保持 dry-run 默认和显式 apply 边界，为后续 release execution 脚本保留可审计的组合入口。

### Files Modified

* `scripts/prepare-release.ps1`
* `scripts/plan-release.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `next.md`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-release.ps1 patch -AllowDirty -Date 2026-05-29 -Runtimes win-x64 -Json`
* Temp clean git fixture: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-release.ps1 patch -Date 2026-05-29 -Apply -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -AllowDirty -Runtimes win-x64 -Json`
* `git diff --check`
* `dotnet build Tau.slnx --no-restore --verbosity minimal`

### Validation Result

* `prepare-release.ps1` dry-run passed on the real dirty worktree with `currentVersion=0.1.0`, `nextVersion=0.1.1`, `dryRun=true`, and release notes status `would-insert`.
* Temp clean fixture `-Apply` passed, changed only `Directory.Build.props` and `docs/releases/feature-release-notes.md`, and inserted the expected `v0.1.1` release notes row.
* `plan-release.ps1` dry-run passed and now lists `release-preparation` plus deliberate remaining gaps for commit/tag/publish/push.
* Final validation commands were rerun after docs/history synchronization before committing.
