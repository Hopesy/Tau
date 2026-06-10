## [2026-06-10 11:22] | Task: CodingAgent extension shortcut dispatch

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 按照goal.md继续

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, parity docs/history

**Key Actions:**

* **Extension shortcut dispatch**: 对照上游 `packages/coding-agent/src/core/extensions/types.ts`、`core/extensions/runner.ts` 和 `modes/interactive/interactive-mode.ts`，把 Tau 从只展示 `registerShortcut` metadata 推进到交互式 editor 可执行 shortcut handler。
* **TUI input hook**: `InteractiveInputEditor` 新增可注入 shortcut handler，在普通 keybinding 解析前给 host 一个消费按键的机会；`InteractiveConsoleSession` 暴露当前 editor keybindings 并转发 shortcut hook。
* **JS/TS runtime invokeShortcut**: `CodingAgentJavaScriptExtensionRuntime` 新增 `invokeShortcut` payload，执行 registered shortcut handler，并复用现有 `actions` 支持 `ctx.sendMessage(...)` / `pi.sendMessage(...)` 进入 runner。
* **Conflict diagnostics**: `CodingAgentExtensionCommandStore` 新增 resolved shortcut 计算，支持 `ctrl/control`、`shift`、`alt/option`、字母、数字、Enter/Return、Esc/Escape、Tab、Space、Backspace、Delete 和方向键解析；与 reserved 内置 editor keybinding 冲突时 warning 并跳过，非 reserved 内置绑定 warning 后允许 extension 覆盖，同一 key 的 extension 重复注册时 warning 且后者胜出。
* **Host integration**: `CodingAgentHost` 启动时缓存 resolved extension shortcut map，交互式输入按键命中时调用 extension handler；handler 产出 runner message 时执行 auto-compaction check、runner turn、session persist；`/reload` 成功后刷新 shortcut cache。
* **Docs/history sync**: 更新 active parity matrix、100% active plan、`next.md`、`QUALITY_SCORE.md`，把 `interactive shortcut dispatch` / `shortcut conflict diagnostics` 从 open gap 收窄为本地 baseline，并保留 jiti、rich renderer、broader lifecycle、真实 extension UI、runner tool hot-swap、package/network smoke 等后续缺口。

### 🧠 Design Intent (Why)

上游 `registerShortcut` 不只是 extension metadata；interactive mode 会把 raw input shortcut 分发给 extension handler。Tau 前一轮只完成 flags/shortcuts 的发现与状态展示，仍无法在真实交互式输入里触发 handler。本轮选择最小可验证路径：复用现有 editor keybinding map 做冲突过滤，复用 JS/TS limited runtime 与 `ctx.sendMessage(...)` action channel，不引入新的 TUI 组件或完整 extension UI bridge。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentExtensionCommandStoreTests|CodingAgentHostTests" --no-restore --verbosity minimal`：82/82 passed
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`：251/251 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：528/528 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 280、Agent 123、Tui 251、CodingAgent 528、WebUi 61、Pods 216）
* `git diff --check`：exit 0；仅报告 `InteractiveConsoleSession.cs` 与 `CodingAgentHostTests.cs` 的 CRLF normalization warning

### 📁 Files Modified

* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/histories/2026-06/20260610-1122-codingagent-extension-shortcut-dispatch.md`
