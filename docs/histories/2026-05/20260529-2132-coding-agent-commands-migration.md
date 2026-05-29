## [2026-05-29 21:32] | Task: CodingAgent commands migration helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / Windows`

### User Query

> 按照 `goal.md` 继续推进 Tau 对 pi-mono-main 的 100% 可审计移植。

### Changes Overview

**Scope:** `scripts`, CI workflow, release contracts, README, parity plans, `next.md`, quality/history docs.

**Key Actions:**

* **Added CodingAgent commands migration helper**: 新增 `scripts/migrate-coding-agent-commands.ps1`，对照上游 `packages/coding-agent/src/migrations.ts` 的 `commands/` -> `prompts/` migration，在 `commands/` 存在且 `prompts/` 不存在时支持 dry-run / `-Apply` rename。
* **Added fixture smoke**: 新增 `scripts/verify-coding-agent-commands-migration.ps1`，覆盖 dry-run、apply、内容保留、target-exists、no-commands、file source、missing base、idempotent apply 和 remaining gap audit。
* **Wired release and CI contracts**: `.github/workflows/tau-ci.yml`、`scripts/plan-release.ps1` 与 `scripts/verify-release-contracts.ps1` 纳入 `coding-agent-commands-migration-smoke`。
* **Synced audit docs**: README、parity matrix、100% active plan、`next.md` 和 `docs/QUALITY_SCORE.md` 记录 helper 覆盖范围，并明确 auth/settings/session/keybindings/tools-to-bin migrations 与 hooks/tools deprecation warnings 仍未关闭。

### Design Intent (Why)

上游 CodingAgent 启动 migration 会把旧 extension `commands/` 目录迁移成 `prompts/`，同时作用于 global agent dir 和 project config dir。Tau 当前已有 prompt discovery baseline，但缺少这个可审计迁移入口。这里先用 PowerShell-first helper 固定用户可运行、CI 可验证的迁移行为：默认只报告，不改文件；显式 `-Apply` 才执行 rename；遇到 `prompts/` 已存在或 source 不是目录时保守跳过。该切片只关闭 commands-to-prompts helper baseline，不伪装完整 `migrations.ts` parity。

### Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-05/20260529-2132-coding-agent-commands-migration.md`
* `next.md`
* `scripts/migrate-coding-agent-commands.ps1`
* `scripts/verify-coding-agent-commands-migration.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
