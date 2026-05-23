# 2026-05-22 23:48 | Task: CodingAgent model cycle hotkey baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，沿 scoped models / TUI keybinding / CodingAgent parity 缺口继续落地相邻切片。

### Changes Overview

**Scope:** `Tau.Tui` editor action/keybinding、`Tau.CodingAgent` host/router model cycle、测试与项目文档。

**Key Actions:**

* **Editor action**: 新增 `CycleModelForward` / `CycleModelBackward`，默认 Ctrl+P / Ctrl+Shift+P 映射到前进/后退模型循环。
* **Input result seam**: `InteractiveInputEditor` 在命中 model cycle action 时提交当前渲染、保留 draft，并返回 `InputResultKind.Action`；`InteractiveConsoleSession` 新增 `ReadInputResultAsync()` 让宿主能区分 action 与提交文本。
* **Host/router wiring**: `CodingAgentHost` 接收到 model cycle action 后调用 `CodingAgentCommandRouter.CycleModel()`，渲染 status 并持久化 session，不进入 LLM runner。
* **Scoped model cycle**: router 复用 settings `enabledModels` 有序 scope 或全部可用模型切换模型，保存默认 provider/model，并在候选不足两个时返回明确状态。
* **Hotkeys output**: `/hotkeys` 新增 `cycle-model-forward` / `cycle-model-backward` action 名与描述。
* **Regression coverage**: 新增 TUI keybinding/action/draft preservation 测试，以及 CodingAgent router/host scoped cycle、backward wrap、单候选状态和 settings persistence 回归。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes，明确本切片是 baseline，不是完整 model selector overlay parity。

### Design Intent

上游 `ctrl+p` / `shift+ctrl+p` 是交互式应用 action，而不是文本输入。本切片让 `Tau.Tui` 只负责识别按键并保留用户输入草稿，把模型切换语义集中在 `CodingAgentCommandRouter`，并复用已经稳定下来的 `enabledModels` scope 合同。这样 Ctrl+P、`/scoped-models` 和 RPC `cycle_model` 共享同一个事实源，同时避免提前搬完整 overlay、footer hint、auth filtering 或 per-entry thinking level。

### Files Modified

* `src/Tau.Tui/Abstractions/EditorAction.cs`
* `src/Tau.Tui/Runtime/KeyBindingMap.cs`
* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHotkeysFormatter.cs`
* `tests/Tau.Tui.Tests/KeyBindingMapTests.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `tests/Tau.Tui.Tests/InteractiveConsoleSessionTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`：通过，81/81。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，293/293。
* `git diff --check`：通过，退出码 0；仅输出既有 CRLF normalization warnings。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，`Tau.Ai.Tests` 194/194、`Tau.Agent.Tests` 58/58、`Tau.Tui.Tests` 81/81、`Tau.CodingAgent.Tests` 293/293、`Tau.Pods.Tests` 32/32。
