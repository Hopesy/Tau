## [2026-05-23 01:47] | Task: CodingAgent model selector scope/detail baseline

### Execution Context

* Agent ID: Codex
* Base Model: GPT-5
* Runtime: Codex CLI / PowerShell / .NET 10

### User Query

> 继续推进 Tau 从 pi-mono-main 的 CodingAgent 移植，沿 active plan 补齐 model selector parity 的下一片。

### Changes Overview

Scope: `Tau.CodingAgent`, docs/plans/history

Key Actions:

* 新增 `CodingAgentModelSelectorComponent`，在现有 selector foundation 上支持 `scoped` / `all` scope toggle。
* model selector 有 scoped 候选时默认 scoped，Tab 切 all/scoped，切换后仍优先定位当前模型。
* 列表下方显示 `Model Name: ...` selected detail，底部保留 auth filtering footer。
* 补充 `CodingAgentModelSelectorTests`，覆盖 scope toggle、selected detail、无 scoped 时 Tab ignored 和 `SelectAsync` all-scope 选择。
* 同步 README、ARCHITECTURE、QUALITY_SCORE、next、release notes 和 active plans。

### Design Intent

* 只补上游 `ModelSelectorComponent` 中已经清晰、低风险的 scope/detail 行为。
* 不把完整 overlay/search input chrome/theme/terminal host 一次性搬入 Tau；当前仍复用已有 TUI foundation。
* 保持 `/scoped-models` 配置入口基于全部注册模型，实际 selector/cycle/switch/RPC 继续按 auth-configured filtering 收口。
* 将 scope/detail 放在 CodingAgent 专用 component 中，避免为了单个 selector 提前扩大 `TuiSelectList` 基础组件合同。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentModelSelectorTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/releases/feature-release-notes.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter CodingAgentModelSelectorTests` -> passed, 7/7.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` -> passed, 86/86.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` -> passed, 314/314.
* `git diff --check` -> passed, exit code 0; only existing CRLF normalization warnings were printed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; project-level validation reported `Tau.Ai.Tests` 194/194, `Tau.Agent.Tests` 58/58, `Tau.Tui.Tests` 86/86, `Tau.CodingAgent.Tests` 314/314 and `Tau.Pods.Tests` 32/32.
