## [2026-05-29 13:30] | Task: release version source baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** `Directory.Build.props`, `scripts/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **Repo-owned version source**: 在 `Directory.Build.props` 增加 Tau 产品版本 `VersionPrefix=0.1.0`，作为 release planner 和 artifact manifest 的当前版本事实源。
* **Release manifest version audit**: `scripts/build-release-artifacts.ps1` 会读取 `Directory.Build.props` 中的 `Version` / `VersionPrefix` / `PackageVersion`，并把 `version` 与 `versionSource` 写入 `manifest.json`。
* **Artifact smoke guard**: `scripts/smoke-release-artifacts.ps1` 会验证 release manifest version 与 `Directory.Build.props` 一致，避免 artifact 使用脱离仓库事实源的版本。
* **Planner sync**: `scripts/plan-release.ps1` 现在可直接从 MSBuild version source 计算 bump，`remainingGaps` 更新为仍不自动写回版本。
* **Docs sync**: README、quality score、100% plan、parity matrix 和 `next.md` 同步说明本轮关闭 repo-owned version source + manifest audit baseline，但不声明 release execution automation。

### Design Intent (Why)

上游 `pi-mono-main` 用 package versions 和 `sync-versions.js` 固定 lockstep 版本语义。Tau 是 .NET solution，不应继续靠命令行 `-CurrentVersion` 手动告诉 release planner 当前版本，也不能直接把上游 npm 包当前版本当成 Tau 自身版本。先把 Tau 产品版本落到 MSBuild 公共属性，再让 release artifact 和 smoke 都读同一事实源，可以为后续真实 version bump / changelog / tag automation 提供稳定起点。

### Files Modified

* `Directory.Build.props`
* `scripts/plan-release.ps1`
* `scripts/build-release-artifacts.ps1`
* `scripts/smoke-release-artifacts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `git diff --check`
* `dotnet build Tau.slnx --no-restore --verbosity minimal`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -AllowDirty -Runtimes win-x64`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 0.2.0 -AllowDirty -Runtimes win-x64 -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Configuration Release -SkipRestore -SkipSmoke`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64`

### Validation Result

* `git diff --check` passed. Git only reported CRLF normalization warnings for edited docs files.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* `plan-release.ps1 patch -AllowDirty -Runtimes win-x64` passed and detected current version `0.1.0` from `Directory.Build.props VersionPrefix`, planning `0.1.1`.
* `plan-release.ps1 0.2.0 -AllowDirty -Runtimes win-x64 -Json` passed and emitted JSON with `currentVersion.status=detected`, `path=Directory.Build.props`, `property=VersionPrefix`, `nextVersion=0.2.0`.
* `build-release-artifacts.ps1 -Configuration Release -SkipRestore -SkipSmoke` passed and rebuilt `artifacts/tau-win-x64`.
* Manifest inspection confirmed `version=0.1.0`, `versionSource.path=Directory.Build.props`, and `versionSource.property=VersionPrefix`.
* `smoke-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64` passed, including the new manifest version/source validation plus existing release artifact smoke.
