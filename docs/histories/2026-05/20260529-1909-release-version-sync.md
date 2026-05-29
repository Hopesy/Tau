## [2026-05-29 19:09] | Task: Release version sync parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** root scripts, CI, release contract, parity docs

**Key Actions:**

* **[Version sync]**: 新增 `scripts/sync-release-versions.ps1`，对照上游 `scripts/sync-versions.js` 的 lockstep version audit，建立 Tau-native MSBuild 单源版本同步入口。
* **[Drift audit]**: 脚本读取 `Directory.Build.props` 中唯一的 `Version` / `VersionPrefix` / `PackageVersion`，扫描 `src/**/*.csproj` 的显式版本属性；dry-run 发现项目版本漂移时返回非零，`-Apply` 可同步到中心版本。
* **[Verification]**: 新增 `scripts/verify-release-version-sync.ps1`，验证当前仓库 8 个 src 项目无版本漂移，并用临时 fixture 固定 drift detection、ProjectReference 审计和 apply 修复行为。
* **[Release/CI contract]**: `plan-release.ps1`、`verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml` 纳入 `release-version-sync-smoke`。
* **[Docs]**: 同步 README、quality、active plans、parity matrix 和 `next.md`，把 `scripts/sync-versions.js` 从“需要 Phase 5 决策”推进到 Tau MSBuild 版本同步 partial，并保留 NuGet/package publish synchronization 为剩余缺口。

### 🧠 Design Intent (Why)

上游 `sync-versions.js` 针对 npm workspace：要求 `packages/*/package.json` lockstep，并把内部 `@mariozechner/*` dependency ranges 改成同一版本。Tau 不维护 npm workspace 或内部 NuGet package dependency ranges，当前正确模型是一个 repo-owned MSBuild 版本源，所有项目继承它。本切片选择审计和修复显式项目版本漂移，避免为了形式一致引入无意义的 package.json 或重复版本字段。

### 📁 Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-05/20260529-1909-release-version-sync.md`
* `next.md`
* `scripts/plan-release.ps1`
* `scripts/sync-release-versions.ps1`
* `scripts/verify-release-contracts.ps1`
* `scripts/verify-release-version-sync.ps1`
