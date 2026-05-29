## [2026-05-29 19:56] | Task: Mom timestamp migration parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** root scripts, CI, release contract, Mom parity docs

**Key Actions:**

* **[Migration helper]**: 新增 `scripts/migrate-mom-timestamps.ps1`，对照上游 `packages/mom/scripts/migrate-timestamps.ts`，扫描 `<data-dir>/<channel>/log.jsonl` 并把历史毫秒 Unix `ts` 迁移到 Slack `seconds.microseconds` 格式。
* **[Guarded writeback]**: Tau 版本默认 dry-run，只有显式 `-Apply` 才写回 `log.jsonl`；`-Json` 输出 machine-readable scan/migration summary。
* **[Tau schema protection]**: 迁移脚本保留坏 JSONL 行、已有 Slack timestamp 和 Tau-native `*-bot` timestamp，避免破坏当前本地 bot log 去重语义。
* **[Verification]**: 新增 `scripts/verify-mom-timestamp-migration.ps1`，用临时 channel log fixture 固定 dry-run 不写入、apply 迁移、malformed 行保留、已有 Slack timestamp 保留、二次 apply 幂等，以及空数据目录返回数字 0 计数。
* **[Release/CI contract]**: `plan-release.ps1`、`verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml` 纳入 `mom-timestamp-migration-smoke`。
* **[Docs]**: 同步 README、quality、active plans、parity matrix 和 `next.md`，把 Mom timestamp migration helper 从 missing 推进到 partial，并保留真实 Slack smoke / session sync / Docker smoke 为剩余缺口。

### 🧠 Design Intent (Why)

上游迁移脚本是历史 channel `log.jsonl` 的 operator helper，用于把曾经写成毫秒整数的 `ts` 改成 Slack API 使用的 `seconds.microseconds`。Tau 当前已有 Slack backfill、download 和本地 channel log，但缺这个迁移入口。本切片选择 PowerShell-first、默认 dry-run 的迁移脚本，让本地数据修改可预览、可审计，并只处理纯毫秒 timestamp；带后缀的 Tau-native bot timestamp 保留原样，避免把上游 `parseInt` 风格误用到 Tau 的本地扩展记录。

### 📁 Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-05/20260529-1956-mom-timestamp-migration.md`
* `next.md`
* `scripts/migrate-mom-timestamps.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-mom-timestamp-migration.ps1`
* `scripts/verify-release-contracts.ps1`
