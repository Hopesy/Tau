## [2026-05-29 03:33] | Task: pi-test wrapper baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的 100% 可审计移植；本轮沿 Phase 5 release/no-env wrapper parity 继续。

### 🛠 Changes Overview

**Scope:** `scripts`、Phase 5 docs/plan/history

**Key Actions:**

* **[Wrapper]**: 新增 `scripts/pi-test.ps1`，作为 PowerShell-first 的 CodingAgent 直达 wrapper，默认运行 `Tau.CodingAgent`，`--no-env` 时复用 `scripts/invoke-no-env.ps1` 做子进程环境隔离和临时 Tau 状态。
* **[RPC stdin]**: `pi-test.ps1 --input-file` 通过 shell redirection 把 stdin 交给子进程，避免 Windows PowerShell / .NET stdin BOM 破坏 CodingAgent RPC JSONL。
* **[Args]**: 增加 `--argument-list-base64`，让验证脚本可以用 JSON 字符串数组传 child arguments，避开 PowerShell 5.1 对 `--` 和子命令参数的歧义。
* **[Gate wiring]**: `scripts/verify-no-env.ps1 -RunSmoke` 的 CodingAgent RPC smoke 改为通过 `pi-test.ps1 --no-env --no-build` 执行。
* **[Docs]**: 同步 README、quality、active plan、parity matrix 和 `next.md`，明确该切片关闭的是 PowerShell 直达 wrapper baseline，不声明 Unix shell wrapper、exact auth-backup parity、CI 或 release automation 完成。

### 🧠 Design Intent (Why)

上游 `pi-test.sh --no-env` 的核心职责是“直接运行 CodingAgent CLI，并在需要时清理 provider/auth 环境”。Tau 已有通用 `invoke-no-env.ps1` 和全仓 `verify-no-env.ps1`，但缺少一个对齐该职责的直达 wrapper。本轮选择复用现有隔离 runner，而不是移动用户真实 auth 文件；这和 Tau 当前 `TAU_AUTH_FILE` / session/settings/log 可重定向的设计一致，也避免测试时触碰用户凭证。

### 📁 Files Modified

* `scripts/pi-test.ps1`
* `scripts/verify-no-env.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
