## [2026-05-23 01:28] | Task: CodingAgent model selector footer hint baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户持续要求继续 Tau 对 pi-mono-main 的 .NET 移植；本轮继续 CodingAgent model selector parity，补齐模型选择器只展示已配置凭证模型时的用户可见提示。

### Changes Overview

**Scope:** `Tau.Tui`、`Tau.CodingAgent` runtime、targeted tests、README、architecture、quality、next、release notes 和 active plans。

**Key Actions:**

* **TUI footer hint**: `TuiSelectListLayout` 新增通用 `FooterHint`，`TuiSelectList` 在普通列表、滚动提示之后以及 no-match 状态下渲染 footer hint。
* **Model selector hint**: `CodingAgentModelSelector` 新增 `AuthFilteringFooterHint`，并在 `/model select`、交互式裸 `/model` 和 Ctrl+L 的模型单选 selector 底部显示 `Only showing models with configured auth`。
* **Tests**: 新增 TUI footer 渲染回归，覆盖滚动列表和 no-match；新增 CodingAgent model selector footer hint 回归。
* **Docs**: 同步 README、ARCHITECTURE、QUALITY_SCORE、next、feature release notes 和两份 active plan；清理旧切片里容易误读为“footer hint 仍未完成”的状态文案，保留历史范围并标注 2026-05-23 已补 baseline。

### Design Intent (Why)

上游 model selector 会提示只展示已配置凭证的模型。Tau 前一刀已经完成 provider auth filtering，但列表本身缺少可见解释，用户看到模型列表变短时容易误判为模型丢失。

本切片选择先把提示抽象成 `TuiSelectList` 的通用 footer hint，而不是直接搬完整上游 `ModelSelectorComponent` overlay。原因是 Tau 当前 selector host 仍是单组件列表，没有 search input chrome、scope toggle、selected model detail 或 per-entry thinking level；通用 footer 能以最小 UI surface 补齐当前用户价值，同时为后续 selector 复用保留简单边界。

### Files Modified

* `src/Tau.Tui/Components/TuiSelectList.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentModelSelectorTests.cs`
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

Validation passed: `Tau.CodingAgent` targeted build completed with 0 warnings / 0 errors; `Tau.Tui.Tests` passed 86/86; `Tau.CodingAgent.Tests` passed 310/310. `git diff --check` exited 0 with only existing CRLF normalization warnings. Full PowerShell gate passed with all src/test builds at 0 warnings / 0 errors and test counts `Tau.Ai.Tests` 194/194, `Tau.Agent.Tests` 58/58, `Tau.Tui.Tests` 86/86, `Tau.CodingAgent.Tests` 310/310, and `Tau.Pods.Tests` 32/32.
