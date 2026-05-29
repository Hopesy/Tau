## [2026-05-29 16:05] | Task: release dry-run contract smoke

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续推进 pi-mono -> Tau 100% parity；当前切片继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** release scripts, CI, README, active parity plans, quality/next docs

**Key Actions:**

* **Release contract smoke**: 新增 `scripts/verify-release-contracts.ps1`，只读取 `plan-release.ps1`、`prepare-release.ps1` 和 `validate-release.ps1` 的 JSON dry-run 输出并断言脚本契约。
* **Validation coverage metadata**: `scripts/validate-release.ps1` 现在输出 `validationLevel`、enabled/skipped validation count 和 validation name 列表；同时跳过 no-env 与 matrix 时会标为 minimal diff-only 并产生 coverage warning。
* **Planner and CI integration**: `scripts/plan-release.ps1` 把 contract smoke 纳入 required scripts 和 planned commands；`.github/workflows/tau-ci.yml` 在长 no-env/build/release artifact 步骤前先运行短 contract smoke。
* **Docs sync**: README、QUALITY、active 100% plan、parity matrix 和 `next.md` 同步说明当前只关闭 release dry-run contract smoke，不声明 release commit/tag/publish/push、非宿主 runner smoke 或真实外部 e2e 完成。

### Design Intent (Why)

Phase 5 release 脚本链已经包含 planner、preparation、validation、version helper、release notes helper、matrix build/package 和 CI artifact 上传。风险不再是缺脚本，而是 dry-run JSON contract、mutation boundary 和 validation coverage 可能在后续改动中漂移。这个切片用一个短 smoke 固定最核心的 release automation 契约，并让 CI 在长验证前快速失败。

### Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `next.md`
* `scripts/plan-release.ps1`
* `scripts/validate-release.ps1`
* `scripts/verify-release-contracts.ps1`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -Runtimes win-x64 -AllowDirty -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Runtimes win-x64 -Json`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Runtimes win-x64 -SkipNoEnv -SkipMatrix -Json`
* `git diff --check`
* `dotnet build Tau.slnx --no-restore --verbosity minimal`

### Validation Result

* `verify-release-contracts.ps1` passed with 30 assertions; it reported next version `0.1.1`, `full-local-dry-run` for default validation and `minimal-diff-only-dry-run` for the skip-no-env/skip-matrix contract case.
* `plan-release.ps1 patch -Runtimes win-x64 -AllowDirty -Json` passed and now lists `release-contract-smoke` in planned commands.
* `validate-release.ps1 -Runtimes win-x64 -Json` passed and reported `validationLevel=full-local-dry-run`, three enabled validation commands and zero skipped commands.
* `validate-release.ps1 -Runtimes win-x64 -SkipNoEnv -SkipMatrix -Json` passed and reported `validationLevel=minimal-diff-only-dry-run`, one enabled validation command, two skipped commands and a `validation-coverage` warning.
* `git diff --check` passed; Git printed the existing CRLF-to-LF normalization warnings for edited docs.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
