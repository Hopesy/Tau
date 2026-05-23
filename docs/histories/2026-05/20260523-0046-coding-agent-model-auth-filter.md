## [2026-05-23 00:46] | Task: CodingAgent model auth filtering baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户持续要求继续 Tau 对 pi-mono-main 的 .NET 移植；本轮继续 CodingAgent model selector / model cycle / RPC model switching 的 provider auth filtering parity 切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` runtime、`Tau.CodingAgent.Tests`、迁移计划、release notes 和质量文档。

**Key Actions:**

* **Shared availability helper**: 新增 `CodingAgentModelAvailability`，统一从 runner 注册模型列表中按 provider auth status 过滤实际可用模型，并提供 `provider/model` 格式化 helper。
* **CLI router filtering**: `/model select`、交互式裸 `/model`、Ctrl+L、Ctrl+P / Ctrl+Shift+P model cycle、显式 `/model` 和 `/provider` 都只展示、循环或接受 auth-configured provider/model；未配置凭证时返回明确状态。
* **RPC filtering**: `get_available_models` 只返回 auth-configured models；`set_model`、`cycle_model` 和 `update_settings.settings.model` 统一拒绝未配置凭证的 provider/model。
* **Scoped models boundary**: `/scoped-models` 继续基于全部注册模型维护 settings `enabledModels`，作为配置入口允许用户预先加入尚未登录的 provider；实际 selector/cycle/switch/RPC 再按 auth status 过滤。
* **Tests**: 扩展 fake runner auth status 控制，并新增 CLI/RPC auth-configured filtering 回归，覆盖 selector 过滤、selector 返回未授权模型、显式 `/model`、`/provider`、model cycle 和 RPC availability/switching。
* **Docs**: 同步 README、architecture、quality score、active plans、next、release notes，并记录本 history。

### Design Intent (Why)

上游 `ModelRegistry.getAvailable()` 和 `AgentSession.setModel()` 都以 `hasConfiguredAuth(model)` 作为实际模型可用性的边界。Tau 之前已经有 model selector、scoped models 和 model cycle baseline，但候选仍可能包含未配置凭证的 provider，导致用户选择后运行时才失败。

本切片把运行时使用入口全部收口到 auth-configured provider/model，同时刻意不把 `/scoped-models` 收窄。`/scoped-models` 是配置入口，不是运行入口；用户应该能提前维护尚未登录 provider 的 scope，等凭证配置后自然生效。共享 helper 让 CLI router 与 RPC host 复用同一规则，避免 headless 和 interactive 路径漂移。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelAvailability.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
* `git diff --check`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`

Validation passed: `Tau.CodingAgent` build 0 warnings / 0 errors, `Tau.CodingAgent.Tests` 309/309 passed, and `Tau.Tui.Tests` 84/84 passed. Full PowerShell gate passed with all src/test builds at 0 warnings / 0 errors and test counts `Tau.Ai.Tests` 194/194, `Tau.Agent.Tests` 58/58, `Tau.Tui.Tests` 84/84, `Tau.CodingAgent.Tests` 309/309, and `Tau.Pods.Tests` 32/32. `git diff --check` exited 0 with only existing CRLF normalization warnings.
