## [2026-06-12 23:35] | Task: AI/Agent package consumer boundary

### Execution Context

* **Agent ID**: `Codex CLI`
* **Base Model**: `GPT-5.1`
* **Runtime**: `PowerShell / .NET 10`

### User Query

> 先把 `Tau.Ai` 和 `Tau.Agent` 完成 100% 迁移，保证 agent 基座能够被其他项目正常引用使用。

### Changes Overview

**Scope:** `Tau.Ai`、`Tau.Agent`、release/package scripts、迁移文档

**Key Actions:**

* **Package metadata**: 在 `Directory.Build.props` 增加 packable 项目的 NuGet 元数据、README 入包、repository/license/tags 等字段，并给 `Tau.Ai` / `Tau.Agent` 各自补准确 description。
* **Consumer smoke**: 新增 `scripts/verify-agent-package-consumer.ps1`，本地 pack `Tau.Ai` 与 `Tau.Agent`，断言 `Tau.Agent.nuspec` 依赖 `Tau.Ai`，再创建两个临时外部 console app：一个只引用 `Tau.Ai` package 并运行 Faux provider LLM 调用，另一个只引用 `Tau.Agent` package 并运行 Agent platform 回合。
* **Release gate**: 将 package consumer smoke 接入 `verify-dotnet.ps1 -RunSmoke`、`plan-release.ps1` 和 `verify-release-contracts.ps1`，让 release contract 固定该边界。
* **Docs sync**: 同步 README、architecture、GOAL、next、quality、active parity plan/matrix，并给 completed Agent platform plan 追加后续事实。
* **Test isolation**: 复跑全仓 `-RunSmoke` 时发现 `WebUiEndpointTests` 依赖进程 cwd，容易被并行的 startup resume 测试临时目录污染；已给测试 WebApplication 显式 `ContentRootPath = AppContext.BaseDirectory`。

### Design Intent (Why)

用户要的是 `Tau.Ai` / `Tau.Agent` 作为其他 .NET 项目的 agent 基座可被正常引用，而不是只在本仓库 ProjectReference 或 examples 下能跑。上游 `pi-agent-core` 发布包依赖 `pi-ai`，因此 Tau 的等价风险点既包括 `Tau.Ai` package 能否独立消费，也包括 `Tau.Agent` NuGet package 是否能把 `Tau.Ai` 作为传递依赖带给外部 consumer。用临时本地 package source、只引用 `Tau.Ai` 的外部 app 和只引用 `Tau.Agent` 的外部 app 固定这个合同，比在仓库内继续增加 ProjectReference 示例更接近真实使用方式。

### Files Modified

* `Directory.Build.props`
* `src/Tau.Ai/Tau.Ai.csproj`
* `src/Tau.Agent/Tau.Agent.csproj`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-dotnet.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `docs/ARCHITECTURE.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`

### Validation

* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1`：最初通过，22 assertions，覆盖 `aiConsumer` 和 `agentConsumer` 两条外部 package 消费路径。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`：通过，`agentPackageConsumer.succeeded=true`，`aiConsumer` / `agentConsumer` restore/build/run exit code 均为 0。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`：通过，测试计数 `Tau.Ai.Tests` 344、`Tau.Agent.Tests` 123、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 631、`Tau.WebUi.Tests` 72、`Tau.Pods.Tests` 216，并完成 `tau-ai`、Agent examples、Agent package consumer、WebUi、Mom smoke。
* 2026-06-15 复验：首次全仓 `-RunSmoke` 在 `WebUiEndpointTests.StreamEndpoint_EmitsNdjsonAndPersistsSession` 暴露 cwd 隔离问题；修复后 `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "FullyQualifiedName~WebUiEndpointTests" --no-restore --verbosity minimal` 通过 11/11，随后 `verify-dotnet.ps1 -SkipRestore -RunSmoke` 全链路通过。
* 2026-06-18 增量复验：同一 smoke 扩展到 32 assertions，`aiConsumer` 额外固定 `configuredStatus=models.json:True`、`configuredApi=consumer-config-api`、`configuredAuth=Bearer consumer-dynamic-key`、header precedence 和 `/v1/chat/completions` request-path，证明外部 consumer 可显式注入 `ModelConfigurationStore` / `ProviderAuthResolver` 并消费 `models.json` 动态 OpenAI-compatible provider。
* 2026-06-18 再增量：`EnvironmentApiKeyResolver` 现在在 Windows 上优先读取 `%APPDATA%\gcloud\application_default_credentials.json` 判断 Vertex ADC，回退用户目录 `~/.config/gcloud/application_default_credentials.json`；`ProviderAuthResolver` 对过期 OAuth credential 的状态文案也改为“refresh/login flow is available”，与现有 `tau-ai login` / CodingAgent `/login` 能力保持一致。同轮后续 auth/OAuth focused gate 已在 `20260618-0226-ai-auth-env-status-contract.md` 收口到 68/68，并把完整 `Tau.Ai.Tests` 推进到 393/393。

### Remaining Boundaries

本轮关闭的是本地 NuGet-style 外部项目直接引用 `Tau.Ai`，以及外部项目引用 `Tau.Agent` 并传递消费 `Tau.Ai` 的边界。真实 NuGet registry 发布、真实 package signing/provenance、package/global install alias、真实 provider/OAuth e2e、真实 proxy/server e2e 和 exact TypeScript export/subpath parity 仍然保持 open，不能把这次本地 consumer smoke 解释成完整上游 package/e2e 100%。
