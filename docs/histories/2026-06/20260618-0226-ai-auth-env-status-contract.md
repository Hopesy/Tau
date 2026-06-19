## [2026-06-18 02:26] | Task: close AI auth env/status local contract

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `Tau.Ai`, AI foundation tests, parity/history docs

**Key Actions:**

* **[Windows Vertex ADC path]**: `EnvironmentApiKeyResolver` 现在在 `google-vertex` 的 ADC 检测中优先读取 `APPDATA` / `SpecialFolder.ApplicationData` 下的 `gcloud\application_default_credentials.json`，再回退到 Unix 风格 `~/.config/gcloud/application_default_credentials.json`。
* **[Auth status wording]**: `ProviderAuthResolver` 对“auth.json 中存在但已过期的 OAuth credential”不再返回“refresh/login flow is not yet fully ported”，改为与当前实现一致的“refresh/login flow is available”。
* **[Provider-level models.json status]**: `ProviderAuthResolver.GetStatus(provider)` 现在识别 provider 级 `models.json` credential 配置；model-only credential headers 不会被误报为 provider-wide configured，仍需通过 `GetStatus(model)` 检查。
* **[OAuth registry/helper contract]**: `OAuthProviderRegistry` 现在固定 built-in restore、custom register/unregister/reset、provider info listing；新增 `OAuthProviderInfo`、`OAuthApiKeyResult` 和 `OAuthHelpers`，提供 async-first refresh/get-api-key helper 与同步 wrapper。
* **[Targeted evidence]**: 扩展 `EnvironmentApiKeyResolverTests`、`ProviderAuthResolverTests`、`OAuthProviderRegistryTests` 和 `AiPublicApiCompileSampleTests`，固定 Windows Vertex ADC path、Vertex ambient auth status、Copilot token precedence、Google/Gemini precedence、Bedrock ambient marker、完整 provider env alias 合同、expired OAuth status 文案、OAuth registry/helper 和 public API sample。
* **[Matrix closure]**: 把 parity matrix 中 `packages/ai/src/env-api-keys.ts`、`Known provider ids and env keys`、`packages/ai/src/utils/oauth/types.ts` 与 `packages/ai/src/utils/oauth/index.ts` 从 `partial` / local contract gap 收口为 `verified`，并同步 `GOAL.md`、`next.md`、active plan 与 `QUALITY_SCORE.md`。

### 🧠 Design Intent (Why)

这一刀继续服务 `Tau.Ai` 作为可被外部 .NET 宿主引用的基础能力，而不是去碰需要真实云凭证的 provider e2e。Windows 上 Vertex ADC 的默认凭证路径如果只按 Unix 风格查找，会让本地宿主在已完成 `gcloud auth application-default login` 的情况下仍被错误判成未配置；同样，OAuth status 文案如果继续声称 login/refresh “未移植完成”，会直接误导 `tau-ai` / CodingAgent / 外部 consumer 的诊断面。两者都属于纯本地、可审计、无网络依赖的 foundation 合同，应该先收口。

### 📁 Files Modified

* `src/Tau.Ai/Auth/EnvironmentApiKeyResolver.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthApiKeyResult.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthHelpers.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthProviderInfo.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthProviderRegistry.cs`
* `src/Tau.Ai/Auth/ProviderAuthResolver.cs`
* `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs`
* `tests/Tau.Ai.Tests/EnvironmentApiKeyResolverTests.cs`
* `tests/Tau.Ai.Tests/OAuthProviderRegistryTests.cs`
* `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
* `docs/histories/2026-06/20260612-2335-ai-agent-package-consumer.md`
* `docs/histories/2026-06/20260618-0226-ai-auth-env-status-contract.md`

### Validation

* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "EnvironmentApiKeyResolverTests|ProviderAuthResolverTests" --no-restore --verbosity minimal -m:1`：通过，52/52。
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "OAuthProviderRegistryTests|OAuthCredentialStoreTests|ProviderAuthResolverTests|AiPublicApiCompileSampleTests|EnvironmentApiKeyResolverTests" --no-restore --verbosity minimal -m:1`：通过，68/68。
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal -m:1`：通过，393/393。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`：继续通过，33 assertions，确认现有外部 package consumer 边界未被回归，并证明 provider-level `models.json` auth status 可被外部 `Tau.Ai` consumer 识别。
* `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter "ProviderAuthResolverTests|ModelConfigurationStoreTests|AiPublicApiCompileSampleTests" --no-restore --verbosity minimal -m:1`：通过，21/21。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`：通过，确认 package consumer、AI provider/OAuth matrix、AI/Agent export shape、AI CLI tool install、Agent proxy server e2e、release package publish、provenance/signing 和 AI test image contract 仍在 release contract 中。

### Remaining Boundaries

本轮关闭的是 `env-api-keys.ts` / auth-status / OAuth registry-helper 的本地 Windows + provider env + .NET-native helper 合同，并已把 `packages/ai/src/env-api-keys.ts`、`Known provider ids and env keys`、`packages/ai/src/utils/oauth/types.ts` 与 `packages/ai/src/utils/oauth/index.ts` 提升为 `verified`。provider-level `models.json` auth status 属于 runtime config UX 的局部推进，不关闭真实 provider/OAuth e2e、真实 NuGet registry/feed 安装、签名/溯源、provider-specific option 全量映射或完整上游 auth UX parity。
