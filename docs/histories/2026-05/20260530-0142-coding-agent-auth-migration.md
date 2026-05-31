## [2026-05-30 01:42] | Task: CodingAgent auth migration helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, Tau harness-init repo`

### User Query

> 按照 `goal.md` 继续推进 Tau 的 pi-mono parity，不停止在文档状态，完成可验证的迁移切片。

### Changes Overview

**Scope:** CodingAgent migration helper, release planning contracts, CI smoke, parity docs.

**Key Actions:**

* **新增 auth migration helper**: 增加 `scripts/migrate-coding-agent-auth.ps1`，对照上游 `migrateAuthToAuthJson()`，把 legacy `oauth.json` provider credentials 与 `settings.json.apiKeys` 迁移到 `auth.json`。
* **固定 dry-run/apply 合同**: 默认只 dry-run，显式 `-Apply` 才写 `auth.json`、把 `oauth.json` 改名为 `oauth.json.migrated` 并删除 `settings.json.apiKeys`；已有 `auth.json` 时跳过 legacy 文件；OAuth 与 api key 冲突时 OAuth 优先。
* **补 targeted smoke**: 增加 `scripts/verify-coding-agent-auth-migration.ps1`，覆盖 dry-run、apply、二次 apply 幂等、existing auth skip、invalid oauth/settings skip、OAuth winner、secret redaction 和 remaining gap audit，当前 25 assertions 通过。
* **接入 release/CI contract**: `scripts/plan-release.ps1`、`scripts/verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml` 纳入 `coding-agent-auth-migration-smoke`。
* **同步 parity 文档**: 更新 `docs/QUALITY_SCORE.md`、`next.md` 和 active execution plans，明确本切片只关闭 legacy auth migration helper baseline。

### Design Intent (Why)

上游 `packages/coding-agent/src/migrations.ts` 已把 legacy `oauth.json` 与 `settings.json.apiKeys` 收敛到 `auth.json`。Tau 已有 auth/status/runtime 入口，但缺一个可离线、可 CI 验证、默认不破坏用户文件的迁移 helper。本轮选择 PowerShell-first 脚本，是为了匹配当前 Windows 验证链和已有 Phase 5 migration helper 风格；JSON 输出只暴露 provider 名称与 credential kind，避免把 token/API key 写进日志。

### Remaining Boundaries

* 不迁移 Tau runtime `coding-agent-settings.json`。
* 不声明 full settings/runtime auth parity 完成。
* 不覆盖 credential refresh UX、真实 OAuth e2e、actual hooks/custom tools migration 或 TypeScript extension runtime。

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-coding-agent-auth-migration.ps1`：25 assertions passed。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1`：36 assertions passed。
* `git diff --check`：passed。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed；src/test build 0 warning / 0 error，tests passed: Tau.Ai 280, Tau.Agent 115, Tau.Tui 190, Tau.CodingAgent 435, Tau.WebUi 44, Tau.Pods 166。

### Files Modified

* `.github/workflows/tau-ci.yml`
* `scripts/migrate-coding-agent-auth.ps1`
* `scripts/verify-coding-agent-auth-migration.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
