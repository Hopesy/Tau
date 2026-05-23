# 2026-05-22 19:23 | Task: CodingAgent login selector baseline

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Codex CLI / Windows PowerShell

### User Query

> 继续推进 Tau 的 pi-mono 移植进度，沿 auth/OAuth selector 缺口继续落地相邻切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` `/login` provider selector、测试与项目文档。

**Key Actions:**

* **Login selector**: 裸 `/login` 在真实交互式 selector seam 可用时打开 provider selector；`/login select` 强制打开 selector。
* **OAuth boundary**: selector 只列出当前注册且有 `IOAuthProvider` 的 provider，选择后复用现有 `LoginAsync` / `SaveOAuthCredentials` 路径保存到 `auth.json`。
* **Compatibility**: 无 selector 的裸 `/login` 继续使用当前 provider；显式 `/login <provider>` 不走 selector，保持原有命令行调用语义。
* **Regression coverage**: `Tau.CodingAgent.Tests` 增至 280 个测试，覆盖 selector selected/cancel/unavailable、无 selector current-provider login、显式 provider 不走 selector、host `/login` selector 接线和 fake OAuth provider 保存。
* **Docs sync**: 同步 README、architecture、quality score、next、两份 active execution plan 和 release notes。

### Design Intent

上游裸 `/login` 会先让用户选择 OAuth provider。Tau 已经有真实 `IOAuthProvider.LoginAsync(...)` 和 `SaveOAuthCredentials(...)` seam，因此本切片只补交互式 provider 选择和现有 OAuth 登录路径的衔接，不引入第二套 auth store，也不把完整上游 login dialog/session UI、logout selector、refresh UX 或真实外部 OAuth e2e 写成完成。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/FakeOAuthProvider.cs`
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
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，280/280。
