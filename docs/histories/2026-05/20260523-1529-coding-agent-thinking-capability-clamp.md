## [2026-05-23 15:29] | Task: CodingAgent thinking capability clamp

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续推进 Tau.CodingAgent 与 pi-mono parity，收口 thinking/reasoning level 在不同模型能力下的有效档位。

### Changes Overview

**Scope:** `Tau.CodingAgent` runtime / RPC / settings / selector / docs

**Key Actions:**

* **[Shared helper]**: 新增 `CodingAgentThinkingLevels`，集中处理 thinking level parse/format、当前模型可用档位、cycle 顺序和模型能力 clamp。
* **[CLI/runtime clamp]**: `/thinking` 显式设置、cycle、`/thinking select`、settings selector、`/reload`、启动恢复、显式 `/model` / `/provider`、Ctrl+L model selector、Ctrl+P/Ctrl+Shift+P model cycle 均按当前模型能力归一 effective thinking。
* **[RPC clamp]**: RPC `set_thinking_level` / `cycle_thinking_level`、`set_model`、`cycle_model`、`update_settings.defaultThinkingLevel` / `settings.model` 复用同一规则，并把返回状态与 settings 中的 default thinking 写成 effective value。
* **[Scoped override]**: scoped model `provider/model:thinking` suffix 保留原始配置表达，运行态切换时才按目标模型能力 clamp。
* **[Docs/history]**: 同步 `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan 和 release notes，明确 capability clamp 已完成，剩余是 per-entry thinking UI editor、完整 settings UI 和 terminal host parity。

### Design Intent (Why)

上游 `setThinkingLevel()` 不是简单保存用户输入，而是受当前模型能力约束。Tau 之前已能设置 `/thinking`、保存 default thinking、用 scoped model suffix 切换 thinking，但 CLI/RPC/settings state 可能在请求发出前显示无效档位。本切片把 clamp 放在 CodingAgent runtime 共享 helper，而不是只依赖 provider payload 层兜底，使用户可见状态、settings 持久化、RPC 返回值和最终 provider 请求保持一致。

规则保持简单：

* 非 reasoning 模型：任何 requested thinking 都归一为 `off`。
* reasoning 但不支持 xhigh 的模型：`xhigh` 归一为 `high`，cycle 从 `high` 直接回 `off`。
* 支持 xhigh 的模型：保留 `xhigh`。
* `minimal` 仍只通过显式设置或 selector 选择进入；cycle 从 `off` 进入 `low`，保持 Tau 现有交互语义。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentThinkingLevels.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentThinkingSelector.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentThinkingLevelsTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentThinkingSelectorTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "CodingAgentThinkingLevelsTests|CodingAgentCommandRouterTests|CodingAgentRpcHostTests|CodingAgentThinkingSelectorTests|CodingAgentScopedModelPatternsTests"`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
