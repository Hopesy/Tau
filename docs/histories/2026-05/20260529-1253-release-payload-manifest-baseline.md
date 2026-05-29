## [2026-05-29 12:53] | Task: release payload manifest baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的可审计 100% 移植；本轮继续 Phase 5 release payload parity。

### Changes Overview

**Scope:** `scripts/`, `README.md`, `docs/exec-plans/active/`, `docs/QUALITY_SCORE.md`, `next.md`

**Key Actions:**

* **Release payload copy**: `scripts/build-release-artifacts.ps1` 现在把当前 Tau 的 `README.md`、`LICENSE` 和完整 `docs/` 复制进 `artifacts/tau-<rid>/`，不再只复制少量基础文档。
* **Manifest audit**: `manifest.json.releasePayload` 新增对上游 `build-binaries.sh` payload 清单的逐项状态记录，覆盖 `readme`、`license`、`docs`、`examples`、`changelog`、`package-json`、`photon-wasm`、`theme`、`export-html`、`interactive-assets` 和 `koffi-windows-native`。
* **Tau-native mapping**: `changelog` 映射为 `docs/releases/feature-release-notes.md`，`package-json` 映射为 Tau release `manifest.json`，`theme` / `export-html` 标记为编译进 Tau 代码的 inline 实现；当前缺失的 examples、Photon image pipeline 和 interactive raster assets 继续标为 `missing`。
* **Smoke contract**: `scripts/smoke-release-artifacts.ps1` 会验证 required payload entry、release notes 文件和既有 executable smoke，防止 release artifact 漏带 docs 或漏写 payload 审计。
* **Docs sync**: README、quality score、100% plan、parity matrix 和 `next.md` 同步说明本轮只关闭 docs payload copy + manifest audit baseline，不声明 full asset-copy parity。

### Design Intent (Why)

上游 `scripts/build-binaries.sh` 的发布产物不只是单个二进制，还会把 `package.json`、`README.md`、`CHANGELOG.md`、Photon wasm、theme JSON、interactive assets、export-html、docs、examples 和 Windows koffi native module 放进每个平台目录。Tau 的实现形态不同：部分内容是 .NET manifest 或编译进代码，部分内容还没移植。与其把上游文件直接 vendoring 进 Tau，本轮先把当前真实 payload 复制完整，并用 manifest 明确审计每一项的 Tau 状态，让后续 release parity 缺口可检查、可缩小。

### Files Modified

* `scripts/build-release-artifacts.ps1`
* `scripts/smoke-release-artifacts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `git diff --check` passed. Git only reported CRLF normalization warnings for edited docs files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Configuration Release -SkipRestore -SkipSmoke` passed and rebuilt `artifacts/tau-win-x64`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64` passed, including release payload manifest checks, release notes payload check, `tau-ai list`, `pi-ai list`, CodingAgent RPC `get_state`, Pods help, WebUi smoke and Mom `--once`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-matrix.ps1 -Runtimes win-x64` passed, created `artifacts/releases/tau-win-x64.zip`, extracted it to a clean temp directory and ran full extracted artifact smoke.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64 -ArchiveFormat tar.gz -ArchiveRoot .\artifacts\release-format-smoke -SkipExecutableSmoke` passed and verified tar.gz extraction structure.
* Manifest inspection confirmed `docs` payload included, `docs/releases/feature-release-notes.md` present, `examples` / `photon-wasm` / `interactive-assets` marked `missing`, `theme` / `export-html` marked `tau-native-inline`, `package-json` marked `tau-native-manifest`, and `koffi-windows-native` marked `not-applicable`.
