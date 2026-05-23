# 2026-05-22 18:53 | Task: CodingAgent auth selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，按计划继续落地相邻 TUI selector 切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` provider auth status selector、测试与项目文档。

**Key Actions:**

* **Auth selector**: 新增 `CodingAgentAuthSelector`，把 provider auth status 转换为 `TuiSelectList` items，并通过 `TuiSelectorSession` / `TuiAnsiRenderSurface` 提供 console selector factory。
* **Command routing**: `/auth` usage 扩展为 `/auth [current|select|provider]`；`/auth select` 在 selector 可用时打开 provider status selector，取消和无 selector 会话返回明确状态。
* **Host seam**: `CodingAgentHost` 和生产入口接入 auth selector seam，只有真实交互式 editor 存在时启用 ANSI selector。
* **Status boundary**: `/auth select` 只检查并展示 configured/missing、credential source、OAuth/login capability 和 provider status message，不写 `auth.json`，不执行 OAuth login，不回显 secret。
* **Regression coverage**: `Tau.CodingAgent.Tests` 增至 275 个测试，覆盖 selector list state、router selected/cancel/unavailable、`/auth current`、显式 provider status 和 host `/auth select` 接线。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

先把 auth status inspection 接入已有 TUI selector foundation，而不是把 provider 选择和 OAuth login-session 流程混在同一刀里。这样 `/auth select` 可以复用当前 `ProviderAuthStatus` 事实源，提供可测试的 provider 状态选择体验，同时保持 credential mutation 只发生在 `/login` / `/logout` 等明确命令路径中。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentAuthSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentAuthSelectorTests.cs`
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
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，275/275。
* `git diff --check`：退出码 0，仅有既有 CRLF normalization warnings。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；repo-level test counts 为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 78、`Tau.CodingAgent.Tests` 275、`Tau.Pods.Tests` 32。
