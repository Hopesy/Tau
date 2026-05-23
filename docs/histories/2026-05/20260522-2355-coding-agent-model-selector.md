## [2026-05-22 23:55] | Task: CodingAgent model selector baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户持续要求继续 Tau 对 pi-mono-main 的 .NET 移植；本轮继续 CodingAgent selector/model parity 切片。

### Changes Overview

**Scope:** `Tau.CodingAgent`、`Tau.Tui`、tests、迁移计划与用户可见文档。

**Key Actions:**

* **Model selector**: 新增 `CodingAgentModelSelector`，复用 `TuiSelectList` / `TuiSelectorSession` / `TuiAnsiRenderSurface`，支持按当前 settings `enabledModels` scope 或全部可用模型单选模型。
* **Slash command**: `/model` 支持 `current`、`select [search]`、裸交互式 selector，以及原有 `provider/model`、`provider model`、唯一 model id 兼容路径。
* **Hotkey**: Ctrl+L 作为 `EditorAction.SelectModel` 返回给 `CodingAgentHost`，打开同一模型选择器，并保留当前 input draft、不写入 history。
* **Persistence**: 选择后调用 runner 切换 provider/model，保存 settings default provider/model，并同步 JSONL tree session controller；取消或 selector unavailable 不修改 runtime/settings。
* **Docs**: 同步 README、architecture、quality score、next、active plans、release notes，并记录本 history。

### Design Intent (Why)

Tau 已有 settings `enabledModels` scope、Ctrl+P/Ctrl+Shift+P model cycle baseline 和 TUI selector/session foundation。先把显式模型选择做成 Tau-native baseline，可以让用户不必记完整 provider/model 名称，同时让 `/model select`、裸交互式 `/model`、Ctrl+L 和 model cycle 复用同一候选规则与 settings 持久化 seam。

本切片刻意不搬完整上游 `ModelSelectorComponent`。完整 overlay、footer hint、provider auth filtering、scoped model thinking-level per-entry 依赖更完整 TUI host 和 provider/auth 状态整合，继续作为后续 parity 切片。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHotkeysFormatter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.Tui/Abstractions/EditorAction.cs`
* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `src/Tau.Tui/Runtime/KeyBindingMap.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentModelSelectorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.Tui.Tests/KeyBindingMapTests.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `tests/Tau.Tui.Tests/InteractiveConsoleSessionTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `git diff --check`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`

Validation passed: `Tau.CodingAgent` build 0 warnings / 0 errors, `Tau.Tui.Tests` 84/84 passed, `Tau.CodingAgent.Tests` 302/302 passed. Full PowerShell gate passed with `Tau.Ai.Tests` 194/194, `Tau.Agent.Tests` 58/58, `Tau.Tui.Tests` 84/84, `Tau.CodingAgent.Tests` 302/302, and `Tau.Pods.Tests` 32/32. `git diff --check` exited 0 with only existing CRLF normalization warnings.
