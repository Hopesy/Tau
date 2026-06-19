## [2026-06-17 02:00] | Task: include AI CLI tool packages in publish rehearsal

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 继续按照 `GOAL.md` 的 Tau pi-mono parity 指引推进迁移。

### Changes Overview

**Scope:** Release/package scripts, `Tau.Ai.Cli` package publish rehearsal docs

**Key Actions:**

* Extended `scripts/publish-release-packages.ps1` so the default package publish rehearsal covers `Tau.Ai.Cli.PiAiTool` / `pi-ai` and `Tau.Ai.Cli.TauAiTool` / `tau-ai` in addition to the existing `Tau.Ai`, `Tau.Agent` and `Tau.Tui` library packages.
* Added `-SkipToolPackages` and tool package metadata in the release publish JSON result, while preserving dry-run-first behavior, explicit `-Apply`, API key env redaction and application package warnings.
* Extended `scripts/verify-release-package-publish.ps1` and `scripts/verify-release-contracts.ps1` to assert the 3 library + 2 tool package command shape, `PackAsTool=true`, `ToolCommandName=pi-ai|tau-ai`, fake apply package count and dirty apply blocking.
* Synced README, `next.md`, quality score and active parity plan/matrix notes so later `/goal` continuations know this is a local command-shape rehearsal, not a real NuGet registry promotion.

### Design Intent (Why)

`Tau.Ai.Cli` already had a local dotnet tool install rehearsal, but the release publish path still defaulted to library packages only. Including the two AI CLI tool packages in the publish rehearsal makes the package registry command shape match the foundation release boundary more closely without requiring real NuGet credentials or pretending that signing/provenance/feed promotion has completed.

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-package-publish.ps1` passed 30 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json` passed with `packageCount=5`, `toolPackageCount=2`, and 71 release contract assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release-packages.ps1 -SkipPush -Json` showed the current repo dry-run planned `Tau.Ai`, `Tau.Agent`, `Tau.Tui`, `Tau.Ai.Cli.PiAiTool`, and `Tau.Ai.Cli.TauAiTool`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed the full PowerShell build/test/smoke gate.
* `git diff --check` passed with only the existing CRLF normalization warning for `docs/QUALITY_SCORE.md`.

### Files Modified

* `scripts/publish-release-packages.ps1`
* `scripts/verify-release-package-publish.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
