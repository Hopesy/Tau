## [2026-05-23 13:03] | Task: coding-agent model selector search chrome

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### User Query

> 继续沿 Tau.CodingAgent 与上游 pi-mono coding-agent 的 selector parity 链推进。

### Changes Overview

**Scope:** `Tau.CodingAgent`、`Tau.Tui`、迁移计划与 release 文档。

**Key Actions:**

* **Model selector chrome**: `CodingAgentModelSelectorComponent` 在模型选择器顶部渲染 `Model Selector` 和 `Search:`，让用户能看到当前搜索状态。
* **Interactive filtering**: 普通字符输入会追加到当前 filter，Backspace 会回退 filter，并实时更新内部 `TuiSelectList` 的候选列表。
* **Regression coverage**: `CodingAgentModelSelectorTests` 覆盖标题/搜索行渲染、字符过滤、Backspace 回退和真实 `SelectAsync` 搜索选择路径。
* **Docs sync**: README、active plans、quality、next 和 release notes 已同步为 search/chrome baseline 已完成，剩余边界仍是完整 theme/dynamic-border/terminal-host parity 和 scoped model thinking-level per-entry。

### Design Intent (Why)

上游 model selector 的用户心智不是一次性过滤参数，而是可见、可编辑的搜索框。Tau 当前还没有完整 terminal host / themed border，因此先把低风险的标题、搜索状态和键入过滤收口在 `CodingAgentModelSelectorComponent` 专用 wrapper 内，不扩大 `TuiSelectList` 的通用合同，也不把完整上游 UI parity 写成完成。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentModelSelectorTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` passed: 86/86.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed: 316/316.
