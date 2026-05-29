## [2026-05-29 23:44] | Task: CodingAgent keybindings migration helper

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 按照 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** `scripts`, `docs/exec-plans`, `next.md`, CI

**Key Actions:**

* 新增 `scripts/migrate-coding-agent-keybindings.ps1`，对照上游 `migrateKeybindingsConfigFile()` / `KEYBINDING_NAME_MIGRATIONS`，把 upstream object-style `keybindings.json` 旧键名迁移到 canonical `tui.*` / `app.*` ids；默认 dry-run，显式 `-Apply` 才写回。
* 新增 `scripts/verify-coding-agent-keybindings-migration.ps1` fixture smoke，覆盖 dry-run、apply、canonical conflict winner、custom key preserved、Tau array-style keybindings no-op、invalid/missing skip 和 idempotence。
* 将 `coding-agent-keybindings-migration-smoke` 纳入 `plan-release.ps1`、`verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml`，并同步 parity matrix、`next.md` 与 `docs/QUALITY_SCORE.md`。

### 🧠 Design Intent (Why)

上游 `packages/coding-agent/src/migrations.ts` 仍有 keybindings migration gap。这个切片先关闭低耦合、可审计的 legacy keybinding id migration helper：它只处理上游 object-style `keybindings.json` 键名迁移，不改 Tau 当前 array-style runtime keybinding schema，也不把 package/extension shortcut、auth/settings 或 custom tools migration 混进同一提交。

### 📁 Files Modified

* `scripts/migrate-coding-agent-keybindings.ps1`
* `scripts/verify-coding-agent-keybindings-migration.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `.github/workflows/tau-ci.yml`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
