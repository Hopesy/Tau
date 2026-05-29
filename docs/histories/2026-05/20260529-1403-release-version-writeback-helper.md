## [2026-05-29 14:03] | Task: release version writeback helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** `scripts/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **Version writeback helper**: 新增 `scripts/update-release-version.ps1`，读取 `Directory.Build.props` 中唯一的 `Version` / `VersionPrefix` / `PackageVersion`，计算 `major|minor|patch` 或显式 `x.y.z` 下一版本。
* **Dry-run by default**: 脚本默认只输出当前版本、下一版本和版本来源；只有显式传 `-Apply` 才写回 MSBuild version source。
* **Planner integration**: `scripts/plan-release.ps1` 的 planned commands 增加 version update preview，并把非执行 mutation 改为未来 release execution flow 中显式 `update-release-version.ps1 -Apply`。
* **Docs sync**: README、quality score、100% plan、parity matrix 和 `next.md` 同步说明当前只关闭 version writeback helper baseline，不声明 changelog、tag、publish 或 push 自动化。

### Design Intent (Why)

上游 `release.mjs` 的 version step 会直接通过 npm workspace 版本命令修改包版本并同步依赖。Tau 当前只有一个 solution-wide MSBuild version source，因此更简单的做法是先提供一个只负责版本写回的脚本，并保持 dry-run 默认。这样后续 release execution script 可以组合该 helper，但本轮不会提前修改仓库真实版本、release notes、tag 或远端状态。

### Files Modified

* `scripts/update-release-version.ps1`
* `scripts/plan-release.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `git diff --check`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-release-version.ps1 patch -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-release-version.ps1 0.2.0 -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-release-version.ps1 patch -PropsPath <temp props> -Apply -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -AllowDirty -Runtimes win-x64`
* `dotnet build Tau.slnx --no-restore --verbosity minimal`

### Validation Result

* `git diff --check` passed. Git only reported CRLF normalization warnings for edited docs files.
* `update-release-version.ps1 patch -Json` passed with `currentVersion=0.1.0`, `nextVersion=0.1.1`, `dryRun=true`, source `Directory.Build.props` / `VersionPrefix`.
* `update-release-version.ps1 0.2.0 -Json` passed with `nextVersion=0.2.0`.
* `update-release-version.ps1 patch -PropsPath <temp props> -Apply -Json` passed on a temp copy and wrote `VersionPrefix=0.1.1`; the real repository `Directory.Build.props` stayed at `0.1.0`.
* `plan-release.ps1 patch -AllowDirty -Runtimes win-x64` passed and now lists `update-release-version.ps1 patch` as a planned command plus `update-release-version.ps1 -Apply` as a deliberately non-executed mutation.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
