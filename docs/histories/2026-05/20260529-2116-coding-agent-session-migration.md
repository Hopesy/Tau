## [2026-05-29 21:16] | Task: CodingAgent session migration helper parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Codex CLI`

### User Query

> 持续推进 Tau 对 pi-mono-main 的 100% 可审计移植；本轮选择 CodingAgent session migration helper parity 切片。

### Changes Overview

**Scope:** `scripts/`, release/CI docs, parity matrix, active plan, `next.md`

**Key Actions:**

* 新增 `scripts/migrate-coding-agent-sessions.ps1`，对照上游 misplaced root JSONL relocation helper，扫描 agent 根目录直接子 `.jsonl`，读取第一行 `type=session` + `cwd`，按上游 cwd 编码规则迁移到 Tau 当前可发现的 `coding-agent-sessions/<encoded-cwd>/`。
* 新增 `scripts/verify-coding-agent-session-migration.ps1`，用临时 fixture 固定 dry-run 不移动、apply 迁移、Windows cwd 编码、target conflict、坏 header 跳过、nested 不扫描和二次 apply 幂等。
* 将 `coding-agent-session-migration-smoke` 接入 `plan-release.ps1`、`verify-release-contracts.ps1` 和 GitHub Actions Windows gate。
* 同步 README、质量记录、active plan、parity matrix 和 `next.md`，明确本切片只关闭 Tau-native root JSONL relocation helper，不关闭 auth/settings/extensions/keybindings/tools migrations 或 exact session schema parity。

### Design Intent

上游 `migrate-sessions.sh` 把 `~/.pi/agent/*.jsonl` 移到 `sessions/<encoded-cwd>/`，但 Tau 当前 session discovery 搜索的是 default tree session 旁的 `coding-agent-sessions/`。本轮选择 Tau-native discoverable target，避免生成 Tau 无法 `/resume` 发现的文件，同时保留上游直接 root 扫描、cwd 编码、target conflict skip 和 dry-run/apply 分界。

### Files Modified

* `scripts/migrate-coding-agent-sessions.ps1`
* `scripts/verify-coding-agent-session-migration.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-coding-agent-session-migration.ps1` passed with 20 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1` passed with 36 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-session-audit-scripts.ps1` passed with 15 assertions.
* `git diff --check` passed; Git only reported existing CRLF normalization warnings for edited docs.
* `dotnet build Tau.slnx --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* `Get-Command actionlint -ErrorAction SilentlyContinue` found no `actionlint` executable in PATH, so workflow lint was not run locally.
