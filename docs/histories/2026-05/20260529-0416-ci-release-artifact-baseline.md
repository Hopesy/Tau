## [2026-05-29 04:16] | Task: CI release artifact baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity，当前切片继续 Phase 5 release、CI 与安装交付 parity。

### Changes Overview

**Scope:** Phase 5 CI / release artifact delivery baseline.

**Key Actions:**

* **GitHub Actions baseline**: 新增 `.github/workflows/tau-ci.yml`，在 Windows runner 上按 `global.json` 设置 .NET，运行 no-env smoke、Release artifact build、archive package/extract smoke，并上传 `tau-win-x64.zip`。
* **Docs sync**: 同步 `README.md`、`docs/QUALITY_SCORE.md`、`next.md` 和 active execution plans，明确当前关闭的是 Windows current-RID CI + release zip artifact baseline。
* **Scope boundaries**: 保留全平台 archive matrix、Unix wrapper/auth-backup parity、version/changelog/tag/publish automation 和真实外部 e2e release smoke 为后续缺口。

### Design Intent (Why)

Phase 5 的完成标准要求 CI 和本地 PowerShell gate 对同一套 public behavior 给出一致信号。这个切片不另建 CI-only 流程，而是让 GitHub Actions 直接复用已经通过本地验证的 `verify-no-env.ps1`、`build-release-artifacts.ps1` 和 `package-release-artifacts.ps1`，从而减少本地与 CI 漂移。

### Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Verification

* `git ls-remote --tags` confirmed the workflow action tags used by `.github/workflows/tau-ci.yml`: `actions/checkout@v6`, `actions/setup-dotnet@v5` and `actions/upload-artifact@v7`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed, including `tau-ai list`, WebUi smoke and Mom `--once` smoke.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore -RunSmoke` passed, including isolated `tau-ai list` and `pi-test.ps1 --no-env --no-build` CodingAgent RPC smoke.
* `dotnet restore Tau.slnx -r win-x64 --verbosity minimal` followed by `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Configuration Release -SkipRestore` passed; the explicit RID restore supplies the `net10.0/win-x64` publish assets needed by the skip-restore path.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64` passed and smoke-tested a clean extracted `tau-win-x64.zip`.
