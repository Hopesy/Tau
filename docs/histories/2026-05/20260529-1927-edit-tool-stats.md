## [2026-05-29 19:27] | Task: Edit tool stats parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** root scripts, CI, release contract, parity docs

**Key Actions:**

* **[Edit stats report]**: 新增 `scripts/report-edit-tool-stats.ps1`，对照上游 `scripts/edit-tool-stats.mjs` 的 JSONL edit tool usage audit，建立 Tau-native PowerShell 审计入口。
* **[Tau/upstream tool support]**: 默认同时识别 Tau 真实工具名 `edit_file` 和上游工具名 `edit`，解析 Tau `old_string/new_string`、上游 `oldText/newText` 以及 `edits[]` multi-edit 参数风格。
* **[Audit output]**: 报告 success/failure/unresolved、single/multi edit、参数风格、provider/model、文件扩展名、same-file cluster、context inflation、巨大 replacement、失败类型和 worst examples；支持 `-Json`、`-IncludeRecords`、`-FailedOnly`、`-Model`、`-Extension`、`-Since`、显式 `-SessionPath` 和 `-SessionsDirectory`。
* **[Verification]**: 新增 `scripts/verify-edit-tool-stats.ps1`，用临时 JSONL fixture 固定 Tau `edit_file`、上游 `edit`、multi-edit、失败分类、same-file cluster、过滤和坏 JSONL 行跳过。
* **[Release/CI contract]**: `plan-release.ps1`、`verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml` 纳入 `edit-tool-stats-smoke`。
* **[Docs]**: 同步 README、quality、active plans、parity matrix 和 `next.md`，把上游 edit stats root utility 推进到 Tau local audit partial，并保留 edit tool runtime/e2e 为剩余缺口。

### 🧠 Design Intent (Why)

上游 `edit-tool-stats.mjs` 是读取 agent session JSONL 的本地审计工具，不是 edit tool runtime 本身。Tau 的真实内置编辑工具名是 `edit_file`，而历史 fixture 或上游导入数据可能仍出现 `edit`。本切片选择在审计脚本里同时支持两种工具名和两类参数形状，保证脚本能审计 Tau 当前会话，也能对照上游样本；同时不修改 runtime，不把 fixture smoke 当作真实 provider/tool e2e。

### 📁 Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-05/20260529-1927-edit-tool-stats.md`
* `next.md`
* `scripts/plan-release.ps1`
* `scripts/report-edit-tool-stats.ps1`
* `scripts/verify-edit-tool-stats.ps1`
* `scripts/verify-release-contracts.ps1`
