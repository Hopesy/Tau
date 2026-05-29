## [2026-05-29 18:51] | Task: CodingAgent startup profiler parity

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植。

### 🛠 Changes Overview

**Scope:** root scripts, CI, release contract, parity docs

**Key Actions:**

* **[Startup profiler]**: 新增 `scripts/profile-coding-agent-startup.ps1`，对照上游 `scripts/profile-coding-agent-node.mjs` 的 RPC startup path，运行 `Tau.CodingAgent.dll --mode rpc --no-context-files --no-themes`，发送 `get_state` JSONL 并计时到 successful response。
* **[Windows stdin hardening]**: profiler 使用无 BOM JSONL input file + shell redirection，避免 Windows PowerShell/.NET redirected stdin 自动写入 UTF-8 BOM 导致 Tau RPC JSONL parser 拒绝请求。
* **[Verification]**: 新增 `scripts/verify-coding-agent-startup-profile.ps1`，固定 warmup/measured run shape、positive median、assembly path 和 TUI gap audit；缺少 Debug assembly 时会自动构建 `Tau.CodingAgent`。
* **[Release/CI contract]**: `plan-release.ps1`、`verify-release-contracts.ps1` 和 `.github/workflows/tau-ci.yml` 纳入 `coding-agent-startup-profile-smoke`，让 profiler 成为 release dry-run / CI 的短检查项。
* **[Docs]**: 同步 README、quality、active plans、parity matrix 和 `next.md`，把 `scripts/profile-coding-agent-node.mjs` 从 missing 推进为 Tau RPC partial，并保留 TUI first-frame/CPU profile/runtime comparison 缺口。

### 🧠 Design Intent (Why)

上游 startup profiler 同时覆盖 RPC 与 TUI startup。Tau 当前已有稳定 RPC mode 和 `get_state` 合同，但没有 `PI_STARTUP_BENCHMARK` 等价 TUI 首帧退出 hook，因此本切片先关闭可本地验证的 RPC startup benchmark，不用伪造 TUI timing。Profiler 直接运行已构建 assembly，避免把 `dotnet run` 项目解析/编译时间混入 startup 数据。

### 📁 Files Modified

* `.github/workflows/tau-ci.yml`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/histories/2026-05/20260529-1851-coding-agent-startup-profile.md`
* `next.md`
* `scripts/profile-coding-agent-startup.ps1`
* `scripts/verify-coding-agent-startup-profile.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
