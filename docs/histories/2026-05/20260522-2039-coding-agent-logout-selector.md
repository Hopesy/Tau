# 2026-05-22 20:39 | Task: CodingAgent logout selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，沿 auth/OAuth selector 缺口继续落地相邻切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` `/logout` provider selector、测试与项目文档。

**Key Actions:**

* **Logout selector**: 裸 `/logout` 在真实交互式 selector seam 可用时打开 provider selector；`/logout select` 强制打开 selector。
* **OAuth boundary**: selector 只列出当前有本地 OAuth credential 且注册了 `IOAuthProvider` 的 provider，选择后复用现有 `_runner.Logout(...)` 路径删除 `auth.json` entry。
* **Compatibility**: 无 selector 的裸 `/logout` 继续使用当前 provider；显式 `/logout <provider>` 不走 selector，保持原有命令行清理语义。
* **Regression coverage**: `Tau.CodingAgent.Tests` 增至 283 个测试，覆盖 selector selected/cancel/unavailable/no-OAuth、显式 provider 不走 selector、host 裸 `/logout` selector 接线和 fake per-provider auth status。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

上游裸 `/logout` 会先让用户选择 OAuth provider，并只在存在 OAuth credential 时进入选择流程。Tau 已有 `ProviderAuthStatus.UsesOAuth` 和 `_runner.Logout(provider)` seam，因此本切片只补交互式 provider 选择与现有 `auth.json` 删除路径的衔接，不引入第二套 auth store，也不改写环境变量或 `models.json` credential 配置。完整 OAuth login dialog/session、credential refresh UX 和真实外部 OAuth e2e 继续作为后续 parity。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
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

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，283/283。
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `git diff --check`：通过，仅有已知 CRLF normalization warning。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，`Tau.Ai.Tests` 194/194、`Tau.Agent.Tests` 58/58、`Tau.Tui.Tests` 78/78、`Tau.CodingAgent.Tests` 283/283、`Tau.Pods.Tests` 32/32。
