## [2026-05-29 17:25] | Task: release local execution

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 按 `goal.md` 继续 100% pi-mono parity 移植；本切片继续 Phase 5 `scripts/release.mjs` parity，把本地 release execution 从 dry-run / preparation / validation 推进到受控 commit/tag baseline。

### Changes Overview

**Scope:** `scripts/`, `README.md`, `docs/QUALITY_SCORE.md`, active parity plans, `next.md`

**Key Actions:**

* 新增 `scripts/execute-release.ps1`，默认 dry-run；显式 `-Apply` 且工作树干净时，按 contract smoke -> preparation apply -> optional validation -> narrow stage -> release commit -> release tag 顺序执行本地 release。
* 更新 `scripts/plan-release.ps1`，把 `scripts/execute-release.ps1` 纳入 required script 和 planned command，让 release plan 显示 local execution preview。
* 更新 `scripts/verify-release-contracts.ps1`，把 `execute-release.ps1` 的 dry-run JSON 输出纳入短 contract smoke，并断言 local mutation plan 包含 contract smoke、preparation、validation、stage、commit、tag。
* 同步 README、质量评分、active plan、parity matrix 和 `next.md`，明确 Tau 现在覆盖本地 version/notes/commit/tag，但仍不执行 publish/push，也不生成上游第二个 `[Unreleased]` changelog commit。

### Design Intent (Why)

Phase 5 之前已经有 release planning、version source、version writeback、release notes writeback、preparation、validation 和 contract smoke，但还缺上游 `scripts/release.mjs` 中本地 commit/tag 执行段。这里选择单独新增 `execute-release.ps1`，而不是把执行逻辑塞进 `plan-release.ps1`，因为 release planning 必须继续保持只读可审计，真实 mutation 必须通过显式 `-Apply` 和 clean-worktree gate 触发。

执行层只允许 `Directory.Build.props` 与 `docs/releases/feature-release-notes.md` 进入 release commit，避免 release validation 或 artifact output 被误提交。publish、push 和外部 e2e 仍留在后续切片；Tau release notes 当前是日期表格，不是上游每包 `CHANGELOG.md` section，因此没有实现上游 release 后再添加 fresh `[Unreleased]` section 的第二个 commit。

### Files Modified

* `scripts/execute-release.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/histories/2026-05/20260529-1725-release-local-execution.md`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1` passed with 36 assertions during implementation smoke.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\execute-release.ps1 patch -Runtimes win-x64 -AllowDirty -Json` passed during implementation smoke and returned `dryRun=true`, `applied=false`, `succeeded=true`, `nextVersion=0.1.1`, `releaseTag=v0.1.1`.
* Temp-repo apply smoke passed after docs synchronization: `execute-release.ps1 patch -Runtimes win-x64 -Apply -SkipValidation -Json` created `Release v0.1.1` commit and `v0.1.1` tag in a copied repository.
* `git diff --check` passed; output only reported CRLF-to-LF normalization warnings for touched Markdown files.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
