# 2026-05-22 22:21 | Task: CodingAgent thinking selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，沿 TUI selector / CodingAgent parity 缺口继续落地相邻切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` `/thinking select` selector、测试与项目文档。

**Key Actions:**

* **Thinking selector**: 新增 `CodingAgentThinkingSelector`，复用 `TuiSelectList` / `TuiSelectorSession` / `TuiAnsiRenderSurface` 展示 `off/minimal/low/medium/high/xhigh` reasoning level。
* **Command routing**: `/thinking select` 在 selector seam 可用时打开交互式选择器；裸 `/thinking` 和 `/thinking current` 保留 status 查询语义。
* **Settings persistence**: 选择非 `off` level 后更新 runner `ThinkingLevel` 并写回 settings `defaultThinkingLevel`；选择 `off` 清空 runtime 和 settings；取消、无 selector 或 selector 返回无效值都不改 settings。
* **Production wiring**: `Program.cs` 只在真实交互式 editor 存在时注入 thinking selector，保持 print/RPC/redirected 模式不创建 TUI selector。
* **Regression coverage**: `Tau.CodingAgent.Tests` 增至 289 个测试，覆盖 selector item/description/current selection、selected/off/cancel/unavailable/invalid、host 接线和 command catalog usage。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

上游 thinking selector 的用户价值是让交互式会话直接选择 reasoning 档位。Tau 已有 `/thinking` CLI 命令、runner `ThinkingLevel` 和 settings `defaultThinkingLevel` 合同，因此本切片只补显式 `/thinking select` 交互路径，不改变裸 `/thinking` 的脚本友好查询语义，也不提前伪造模型能力 clamp。完整 settings UI parity 和基于 model metadata 的 thinking capability clamp 继续后置。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentThinkingSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentThinkingSelectorTests.cs`
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
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，289/289。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，`Tau.Ai.Tests` 194/194、`Tau.Agent.Tests` 58/58、`Tau.Tui.Tests` 78/78、`Tau.CodingAgent.Tests` 289/289、`Tau.Pods.Tests` 32/32。
