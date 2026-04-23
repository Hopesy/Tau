## [2026-04-23 18:10] | Task: 补 Tau.Ai 的 provider / auth / registry 基线

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 先把 provider 覆盖、model registry / generated models、OAuth / auth、env auth 解析这几块补起来，其他缺失的写到 next.md。

### 🛠 Changes Overview

**Scope:** `src/Tau.Ai`, `src/Tau.CodingAgent`, `tests/Tau.Ai.Tests`, `docs/`, `next.md`

**Key Actions:**

* **[补 provider 覆盖]**：接入 `openai-responses`、`openai-codex-responses`、`azure-openai-responses`、`mistral-conversations`、`google-vertex`、`google-gemini-cli`、`bedrock-converse-stream` 的第一轮 provider 接线。
* **[补 model registry]**：新增 `ModelCatalog` 与 `BuiltInModels`，统一 provider / model 查询与默认模型入口。
* **[补统一认证解析]**：新增 `EnvironmentApiKeyResolver`、`ProviderAuthResolver`、`OAuthCredentialStore`、`OAuthProviderRegistry`、`StoredProviderAuth`，把显式 api key、环境变量、auth.json、OAuth refresh 骨架接到同一条认证解析链。
* **[扩 auth.json 读取]**：支持从 auth.json 读取 `api_key` 和 `oauth` 两类条目，不再只识别 OAuth。
* **[补 Tau.Ai 行为测试]**：把 `Tau.Ai.Tests` 从空壳替换成真实测试，覆盖 model catalog、env auth、auth.json 读取、provider auth resolver 和 built-in provider 注册。
* **[同步文档与待办]**：更新 `README.md`、`docs/QUALITY_SCORE.md`、`docs/CICD.md`、execution plan，并新增 `next.md` 记录剩余缺口。

### 🧠 Design Intent (Why)

在 CLI-first 主路径稳定之后，当前最阻塞后续产品面的不是 UI，而是底层模型/认证边界仍然过薄。先把 provider 注册、模型查询、环境变量映射、auth.json 和 OAuth 解析统一起来，后面无论继续进 `Tau.CodingAgent` 还是 `WebUi / Mom / Pods`，都能建立在更稳定的配置面之上。

这一轮没有假装把所有 OAuth browser/device-code 流程完整移植，而是先把 **provider 可发现、model 可查询、auth 可解析、测试可落地** 这四件事做实，再把真正的 login flow 和高保真 provider 行为放进 `next.md`。

### ✅ Verification

执行并通过：

* `dotnet restore tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --ignore-failed-sources -p:TreatWarningsAsErrors=false -p:NuGetAudit=false`
* `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore`
* `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore`
* `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore`
* 逐项目执行 `tests/*.csproj` 的 `dotnet test --no-build --no-restore`

当前已确认：

* `Tau.Ai.Tests` 共 16 个测试通过
* `Tau.Agent.Tests / Tau.CodingAgent.Tests / Tau.Pods.Tests / Tau.Tui.Tests` 也可通过逐项目测试方式运行

已知仍未收口：

* 当前这台机器上的 `Tau.slnx` 仍存在 .NET 10 SDK / workload 解析层面的 build 异常，MSBuild 会失败但不给出正常编译错误；但 `Tau.CodingAgent.csproj` / `Tau.CodingAgent.Tests.csproj` 已通过工程化 workaround 恢复独立 build。

### 📁 Files Modified

* `src/Tau.Ai/Abstractions/Models.cs`
* `src/Tau.Ai/Auth/EnvironmentApiKeyResolver.cs`
* `src/Tau.Ai/Auth/ProviderAuthResolver.cs`
* `src/Tau.Ai/Auth/OAuth/BuiltInOAuthProviders.cs`
* `src/Tau.Ai/Auth/OAuth/IOAuthProvider.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthCredentials.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthProviderRegistry.cs`
* `src/Tau.Ai/Auth/OAuth/StoredProviderAuth.cs`
* `src/Tau.Ai/Providers/BuiltInProviders.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs`
* `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs`
* `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs`
* `src/Tau.Ai/Registry/BuiltInModels.cs`
* `src/Tau.Ai/Registry/ModelCatalog.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.Ai.Tests/EnvironmentVariableScope.cs`
* `tests/Tau.Ai.Tests/EnvironmentApiKeyResolverTests.cs`
* `tests/Tau.Ai.Tests/OAuthCredentialStoreTests.cs`
* `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
* `tests/Tau.Ai.Tests/ModelCatalogTests.cs`
* `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/CICD.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
* `docs/histories/2026-04/20260423-1810-tau-ai-provider-auth-registry-slice.md`


### 🔁 Follow-up：继续收口可执行项目与 CI 文档

**本轮追加动作：**

* **[恢复可执行项目 build/run]**：将 `Tau.CodingAgent.csproj` / `Tau.CodingAgent.Tests.csproj` 改为显式 `Reference + HintPath` 指向 sibling 输出 DLL，避开当前 solution / project-reference 链上的 MSBuild 异常。
* **[统一验证脚本]**：新增 `scripts/verify-dotnet.sh`，把 restore / build / test 的稳定顺序固化成仓库脚本，并让主 CI 直接复用。
* **[同步真实文档]**：更新 `README.md`、`docs/ARCHITECTURE.md`、`docs/CICD.md`、`docs/QUALITY_SCORE.md`、`docs/RELIABILITY.md`、`docs/PRODUCT_SENSE.md`、execution plan、`next.md` 与 tech debt 文档，把“项目级验证已恢复、solution build 仍未恢复”的现状写实。

**追加验证：**

* `bash scripts/verify-dotnet.sh --skip-restore`
* `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`（输入 `exit`，确认可启动并退出）

**追加后的已知未收口：**

* 当前 workaround 让 `Tau.CodingAgent` 回到可 build / run / test，但 `Tau.slnx` 仍未恢复，后续还需要把 solution-level 异常与 `HintPath` 依赖一起收回到更正常的工程结构。
