## [2026-06-09 17:26] | Task: CodingAgent extension entry discovery/status

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / Windows`

### 📥 User Query

> 继续继续，按当前 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Extension entry discovery**: 对照上游 `core/extensions/loader.ts` / `core/package-manager.ts`，让 package/local extension resources 按 direct `.ts/.js`、目录 `index.ts/js`、`package.json pi.extensions` entry 发现 module entry，同时保留 Tau 现有 JSON extension command file 兼容。
* **Status observability**: `CodingAgentExtensionCommandStore.LoadStatus()` 新增 module status，`/extensions` 显示 module path、scope、runtime 与 `runtime pending`。
* **Tests/docs**: 补 package manager 和 extension command store targeted tests，并同步 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md`、active plan 和 parity matrix。

### 🧠 Design Intent (Why)

这是 TypeScript extension runtime/custom tools 的前置合同切片。它先关闭上游式 discovery 与状态可见性，避免继续把 convention directory 或 JSON command baseline 误当作 package-loaded TypeScript runtime。TS/JS module 只报告为 discovered/runtime pending，不执行、不注册 tools/commands/flags，保留后续切片边界。

### ✅ Validation

* `git diff --check`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "PackageManager|ExtensionCommandStore" --no-restore --verbosity minimal`（21/21）
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`（498/498）
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 121、Tui 251、CodingAgent 498、WebUi 61、Pods 216）

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentPackageManager.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPackageManagerTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
