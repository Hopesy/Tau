## [2026-05-29 18:03] | Task: Session audit scripts parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** root scripts, CI, parity docs

**Key Actions:**

* **[Script parity]**: 新增 `scripts/export-session-transcripts.ps1`，对照上游 `scripts/session-transcripts.ts` 提供 Tau JSONL session transcript 导出，支持默认 `.tau` session 扫描、显式 `-SessionPath` / `-SessionsDirectory`、user/assistant 文本抽取和按字符上限切片。
* **[Cost audit]**: 新增 `scripts/report-session-costs.ps1`，对照上游 `scripts/cost.ts` 汇总已持久化 `usage.cost` 和 token records；当前 Tau session 未持久化 cost 时只报告 warning，不反推历史美元成本。
* **[Verification]**: 新增 `scripts/verify-session-audit-scripts.ps1`，用临时 JSONL fixture 固定坏行跳过、toolResult 忽略、string content 兼容、cost/token 汇总和 JSON array shape；`plan-release.ps1`、`verify-release-contracts.ps1` 与 GitHub Actions 已纳入该 smoke。
* **[Docs]**: 同步 README、quality、active plans、parity matrix 和 `next.md`，保留 transcript `--analyze` 子 agent 流程与 CodingAgent usage/cost 持久化为后续缺口。

### 🧠 Design Intent (Why)

上游 root script matrix 仍有 `session-transcripts.ts` 和 `cost.ts` 的 invocation surface 缺口。Tau 已有 JSONL/HTML/WebUi export 能力和模型计价 helper，但缺少可直接运行、可审计的 root script 等价入口。本切片选择 PowerShell-first 脚本，直接解析 Tau 当前 JSONL tree session，避免引入新 .NET tool 或伪造当前 session 中不存在的 cost 数据。

### 📁 Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `scripts/export-session-transcripts.ps1`
* `scripts/report-session-costs.ps1`
* `scripts/verify-session-audit-scripts.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
