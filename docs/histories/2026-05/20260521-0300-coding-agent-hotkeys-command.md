## [2026-05-21 03:00] | Task: CodingAgent hotkeys command

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 继续推进 Tau 的 pi-mono parity 移植。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, docs/history

**Key Actions:**

* **Hotkeys command**: 新增 `/hotkeys` command catalog/router 路径，输出当前交互式 editor 注入的 `IKeyBindingMap`。
* **Formatter**: 新增 `CodingAgentHotkeysFormatter`，按 action 分组列出当前有效按键组合和说明，并隐藏被禁用的默认 binding。
* **Host wiring**: `Program.cs` 把交互式 editor 的 `KeyBindings` 传给 `CodingAgentHost`，host 再传给 router；无 editor 的 print/RPC/redirected 模式返回 unavailable。
* **Tests/docs**: 新增 targeted tests，并同步 README、Architecture、Quality、next 与 active plans 的 hotkeys baseline 状态。

### Design Intent (Why)

`/hotkeys` 的第一刀只暴露 Tau 当前真实存在的 editor keybinding map，不把上游 app/session/tree/extension shortcut registry 写成完成。这样能立即让用户确认自定义 keybindings 与禁用项是否生效，同时保持完整 TUI footer hints、shortcut provenance 和 extension-contributed shortcut registry 作为后续独立 parity 切片。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHotkeysFormatter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> 0 warnings, 0 errors
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> 205/205 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> project-level validation passed; tests: Tau.Ai 191, Tau.Agent 54, Tau.Tui 56, Tau.CodingAgent 205, Tau.Pods 32
