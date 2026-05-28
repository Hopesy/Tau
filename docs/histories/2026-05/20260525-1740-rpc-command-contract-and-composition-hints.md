## [2026-05-25 17:40] | Task: RPC command contract and composition hints

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续下一轮 Tau <- pi-mono-main 快速移植，少做低收益单元测试和大段文档同步，用多 Agent 并行定位缺口并推进核心 baseline。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, migration tracking docs

**Key Actions:**

* **RPC command listing contract**: `CodingAgentRpcHost.get_commands` now follows upstream semantics by returning only extension / prompt / skill dynamic commands. Local slash catalog commands stay owned by `/help` and are no longer mixed into the RPC dynamic command list.
* **Composition input hints**: `TuiCompositionInteractiveRenderer` now appends a short default keybinding hint line to the main input overlay, improving discoverability for send, follow-up, model selection, model cycling and reverse search.
* **Plan sync**: `next.md` and the two active migration plans were updated only with the facts that affect future Agent decisions.

### Design Intent

`get_commands` in upstream pi-mono is a dynamic command discovery surface for prompt-invocable extension / prompt / skill commands, not a full built-in slash command catalog. Keeping local command catalog in RPC responses made Tau less compatible with upstream clients. The TUI hint line is a low-conflict baseline for interactive usability without pulling in the full keybinding registry, footer, terminal host or theme system.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter CodingAgentRpcHostTests` passed: 47 / 47.
* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed again after the TUI hint change.
* `git diff --check` returned only existing CRLF normalization warnings.
