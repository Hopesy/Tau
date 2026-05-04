## [2026-05-03 22:55] | Task: powershell runtime smoke

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `scripts/verify-dotnet.ps1`

**Key Actions:**

* **[Optional smoke mode]**: 为 `scripts/verify-dotnet.ps1` 增加 `-RunSmoke`，在 restore/build/test 之后追加 `WebUi` 和 `Mom --once` 的最小运行态 smoke。
* **[WebUi smoke]**: 启动临时 `Tau.WebUi` 宿主，等待 `/healthz`，访问 `/api/status` 和 `/api/catalog`，创建 session，并校验临时 `webui-sessions.json` 已落盘。
* **[Mom smoke]**: 用临时 inbox/outbox/archive 目录运行 `Tau.Mom --once`，校验 outbox JSON 与 archive 文件都已生成。
* **[Live verification]**: 实跑 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`，确认完整通过。

### 🧠 Design Intent (Why)

前一轮已经把 Windows 本机的项目级验证脚本补上，但还停留在 build/test。继续让运行态 smoke 只能手工敲命令，不利于把真实宿主行为沉淀成可重复门禁。把最小 `WebUi` 和 `Mom` smoke 收进同一个 PowerShell 验证入口，能直接覆盖当前最有价值的两条宿主路径，同时避免先把复杂的 bash/CI 接线问题和行为验证问题混在一起。

### 📁 Files Modified

* `scripts/verify-dotnet.ps1`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/CICD.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
