## [2026-05-21 05:00] | Task: CodingAgent logout command baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` parity。

### 🛠 Changes Overview

**Scope:** `Tau.Ai`、`Tau.CodingAgent`、相关测试、README / architecture / quality / next / active plans

**Key Actions:**

* **OAuth auth store removal**: 在 `OAuthCredentialStore` 中新增 `Remove(providerId)`，按大小写不敏感 provider id 删除首个存在 `auth.json` 中的对应 credential entry；文件不存在、JSON malformed、非 object 或 provider 未命中时返回 `false` 且不重写文件。
* **Auth resolver logout seam**: 在 `ProviderAuthResolver` 中新增 `Logout(providerId)`，委托 credential store 删除，并写入不含 secret 的 `auth/logout` 观测事件。
* **CodingAgent command**: 在 `ICodingAgentRunner` / `RuntimeCodingAgentRunner` / command catalog / command router 中接入 `/logout [provider]`；未传 provider 时使用当前 runner provider，显式 provider 时先解析 auth status。
* **User-facing boundary**: `/logout` 只删除 `auth.json` provider credential entry；环境变量和 `models.json` credential 配置保持不变；输出不回显 access token、refresh token、API key 或 credential header 值。
* **Tests and docs**: 补 `Tau.Ai.Tests`、`Tau.CodingAgent.Tests`、fake runner drift 修复，并同步 README、architecture、quality score、next 和 active execution plans。

### 🧠 Design Intent (Why)

上游 `AuthStorage.logout(provider)` 的核心行为是删除 auth storage 中该 provider 的本地 credential entry。Tau 当前还没有完整 OAuth selector UI / login-session parity，因此本切片先落 Tau-native CLI baseline：`/logout [provider]` 只清理 `auth.json`，不修改 env 或 `models.json`，避免越界删除用户外部配置，也避免任何 secret 进入终端输出、日志或文档。

### 📁 Files Modified

* `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs`
* `src/Tau.Ai/Auth/ProviderAuthResolver.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.Ai.Tests/OAuthCredentialStoreTests.cs`
* `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，194/194。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，211/211。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests build 和 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 211、`Tau.Pods.Tests` 32 全部通过。
* `git diff --check -- src\Tau.Ai src\Tau.CodingAgent src\Tau.Tui tests\Tau.Ai.Tests tests\Tau.CodingAgent.Tests tests\Tau.Agent.Tests tests\Tau.WebUi.Tests README.md docs next.md`：通过，仅有已知 CRLF normalization warning。
