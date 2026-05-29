## [2026-05-29 12:10] | Task: release archive matrix baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮继续 Phase 5 release/archive parity。

### Changes Overview

**Scope:** `scripts/`, `.github/workflows/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **扩展 archive 格式合同**: `scripts/package-release-artifacts.ps1` 新增 `-ArchiveFormat auto|zip|tar.gz`，`auto` 按 RID 选择 Windows zip、Linux/macOS tar.gz。
* **区分宿主与非宿主 smoke**: release archive 解压后始终校验 `tau-<rid>` 顶层目录；只有 archive RID 等于当前宿主 RID 时默认执行 executable smoke，非宿主 RID 只做结构校验，除非显式 `-ForceSmoke`。
* **新增矩阵脚本**: `scripts/package-release-matrix.ps1` 批量归档已存在的 RID artifact；`scripts/build-release-matrix.ps1` 按 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`、`win-x64` 逐个 restore/build/package。
* **同步 CI 与文档**: GitHub Actions 改为通过 `package-release-matrix.ps1 -Runtimes win-x64` 生成 Windows zip，并额外跑 tar.gz format structure smoke（解压结构校验但跳过重复 executable smoke）；README、parity matrix、100% plan、`next.md` 和质量评分同步记录剩余非宿主 runner smoke、asset-copy、release automation 与真实外部 e2e 缺口。

### Design Intent (Why)

上游 `scripts/build-binaries.sh` 的发布合同不是单一 Windows zip，而是五个平台归档矩阵：Windows 输出 zip，macOS/Linux 输出 tar.gz，并在打包后重新解压用于测试。Tau 已有 current-RID zip + extracted smoke，本轮把归档格式和 RID 矩阵显式固化到脚本层，同时避免在 Windows 本机把 Linux/macOS 产物误标为可执行 smoke 通过。

### Files Modified

* `.github/workflows/tau-ci.yml`
* `scripts/build-release-artifacts.ps1`
* `scripts/package-release-artifacts.ps1`
* `scripts/build-release-matrix.ps1`
* `scripts/package-release-matrix.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `git diff --check` passed. Git only reported CRLF normalization warnings for edited docs files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-matrix.ps1 -Runtimes win-x64` passed. It created `artifacts/releases/tau-win-x64.zip`, extracted it to a clean temp directory, and ran the full release artifact smoke: `tau-ai list`, `pi-ai list`, `pi --mode rpc get_state`, `pi-pods --help`, WebUi health/status/catalog/session store, and Mom `--once`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64 -ArchiveFormat tar.gz -ArchiveRoot .\artifacts\release-format-smoke -SkipExecutableSmoke` passed. It created `artifacts/release-format-smoke/tau-win-x64.tar.gz`, extracted it to a clean temp directory, verified the `tau-win-x64` top-level directory, and skipped duplicate executable smoke by request.
