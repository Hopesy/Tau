## [2026-06-09 18:39] | Task: CodingAgent TypeScript extension runtime

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 / Node 24`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **[TypeScript extension loader]**: 在现有 `CodingAgentJavaScriptExtensionRuntime` 的 Node 子进程中为 `.ts` extension module 注册 Node 24 `node:module.registerHooks` loader，使用 `stripTypeScriptTypes(mode: strip)` 直接加载 erasable TypeScript，并保留原始 file URL / cache bust 路径。
* **[TypeScript command runtime]**: `CodingAgentExtensionCommandStore` 不再把 `.ts` module 仅标记为 `runtime pending`，而是执行 TypeScript factory，收集 `pi.registerCommand(...)` 命令并让 `/extensions` 与 `TryInvoke` 显示、调用同一 limited runtime。
* **[Regression coverage]**: 新增 TypeScript extension tests，覆盖 interface/type annotation、relative `.ts` import、package-loaded `node_modules` entry、handler runner message、load failure diagnostic 和 `.ts/.js` module status。
* **[Docs sync]**: 同步 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md`、active plan 和 parity matrix，明确本轮只关闭 TypeScript `registerCommand` local runtime baseline，不声明完整 jiti/custom tool parity。

### 🧠 Design Intent (Why)

上游用 `@mariozechner/jiti` 统一加载 `.ts/.js` extension factory，但当前本地 Tau 与上游 checkout 都没有可复用的 jiti 包安装。为了先关闭低歧义、可验证的 TypeScript command baseline，本轮采用 Node 24 内置 type-stripping hook，只支持 erasable TypeScript，并保留 `.ts` 原路径以让 relative `.ts` import 和 package-loaded extension path 行为可审计。这个实现刻意不声称完整 jiti import/alias/virtualModules、non-erasable TS transform、`registerTool`、flags/shortcuts/events、custom tool wrappers 或真实 extension UI parity。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionCommandStore|ExtensionsCommand" --no-restore --verbosity minimal` passed: 17/17.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed: 507/507.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "FullyQualifiedName~JavaScriptReplBridge_PollsPendingToolRequestAndPostsBrowserResult" --no-restore --verbosity normal` passed: 1/1 after the first project gate hit a one-off WebUi browser-flow timeout.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed: 61/61.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 121, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 507, `Tau.WebUi.Tests` 61, `Tau.Pods.Tests` 216.
