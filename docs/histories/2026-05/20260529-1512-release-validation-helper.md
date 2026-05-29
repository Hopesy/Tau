## [2026-05-29 15:12] | Task: guarded release validation helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 pi-mono -> Tau 100% parity；当前切片继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** release scripts, README, active parity plans, quality/next docs

**Key Actions:**

* **Guarded validation script**: 新增 `scripts/validate-release.ps1`，把 `git diff --check`、`verify-no-env.ps1` 和 `build-release-matrix.ps1` 编排成一个本地 release validation flow。
* **Dry-run by default**: 脚本默认只输出计划命令；只有显式 `-Run` 才执行本地验证。
* **Clean-worktree guard**: `-Run` 默认要求工作树干净，`-AllowDirty` 只用于本地 WIP validation。
* **Planner integration**: `scripts/plan-release.ps1` 把 `validate-release.ps1` 纳入 required scripts 和 planned commands。
* **Docs sync**: README、QUALITY、active 100% plan、parity matrix 和 `next.md` 同步说明当前只关闭 release validation 编排 baseline，不声明 release preparation apply、commit/tag/publish/push、非宿主 runner smoke 或真实外部 e2e 完成。

### Design Intent (Why)

Tau 已经有 no-env gate、release matrix build/package 和 release preparation helper，但缺少一个 release 级本地验证入口来表达“正式 release 前该跑哪些本地 checks”。本切片把验证动作组合成一个默认无副作用的脚本，避免把 release planner、release preparation 和 release validation 混成一个会意外改版本或远端状态的命令。

### Files Modified

* `scripts/validate-release.ps1`
* `scripts/plan-release.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `next.md`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Runtimes win-x64 -AllowDirty -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Run -AllowDirty -SkipNoEnv -SkipMatrix -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -AllowDirty -Runtimes win-x64 -Json`
* `git diff --check`
* `dotnet build Tau.slnx --no-restore --verbosity minimal`

### Validation Result

* `validate-release.ps1` dry-run passed on the real dirty worktree with `runtimes=["win-x64"]`, planned diff/no-env/matrix commands, and no command execution.
* `validate-release.ps1 -Run -AllowDirty -SkipNoEnv -SkipMatrix -Runtimes win-x64 -Json` passed and executed only `git diff --check`; Git reported the existing CRLF normalization warnings for edited docs but returned exit code 0.
* `plan-release.ps1 patch -AllowDirty -Runtimes win-x64 -Json` passed and now lists `release-validation` in planned commands.
* `git diff --check` passed with the same CRLF normalization warnings for edited docs.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
