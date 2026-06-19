## [2026-06-19 21:19] | Task: Bedrock models.json options contract

### Execution Context

* **Agent ID**: Codex
* **Base Model**: GPT-5
* **Runtime**: Windows PowerShell, .NET 10

### User Query

> 继续 Tau.Ai / Tau.Agent foundation-first 迁移；优先把 Agent 基座做完整，方便其它应用引用，最后再迁移其它产品模块。

### Changes Overview

**Scope:** `Tau.Ai`, package consumer verification, foundation-first docs

**Key Actions:**

* 对照上游 `packages/ai/src/providers/amazon-bedrock.ts`，把 Bedrock 第一批 provider-specific `models.json options` 接入 Tau：`region`、`profile`、`bearerToken`、字符串/tool 对象型 `toolChoice`、`reasoning`、`thinkingBudgetTokens`、`thinkingDisplay`、`interleavedThinking` 和 `requestMetadata`。
* `StreamFunctions` 在 `bedrock-converse-stream` dispatch 前把配置投影到 typed `BedrockOptions`，并保留显式代码 options 最高优先级。
* `BedrockOptions` 新增 `InterleavedThinking`；`BedrockMessageConverter` 会输出 tool choice、request metadata、thinking display 和 non-adaptive Claude `anthropic_beta` request field。
* `ModelConfigurationStoreTests` / `BedrockProviderTests` 增加 Bedrock typed dispatch、显式 typed precedence 和 request body 覆盖。
* `verify-agent-package-consumer.ps1` 增加外部 `Tau.Ai` package consumer 对 Bedrock typed options 的捕获断言；`verify-release-contracts.ps1` 同步固定新增输出。
* 同步 `GOAL.md`、`next.md`、`README.md`、`docs/QUALITY_SCORE.md`、active plan 和 parity matrix。
* 复跑 `verify-dotnet.ps1 -SkipRestore -RunSmoke` 时发现 `CodingAgentSessionFileExporterTests` 会和修改 `Environment.CurrentDirectory` 的启动恢复测试并行撞全局进程状态；本轮把 exporter 和 startup resume resolver 测试放入现有 current-directory/env 隔离 collection，稳定验证链，不改变运行时代码语义。

### Design Intent

Bedrock provider 的请求层已经具备 bearer/SigV4/profile/credential chain 和 Claude thinking 请求能力，但 foundation-first package consumer gate 之前没有证明外部 .NET 应用可以通过 `models.json` 配置把 Bedrock provider-specific 字段投影到 typed `BedrockOptions`。本轮选择这个切片，是因为它能在没有真实 AWS 凭证的情况下关闭确定性的本地 runtime config contract，同时保持真实 AWS Bedrock e2e、OIDC registration renewal、profile/cache concurrency 等外部验收继续 open，不把 fixture smoke 伪造成云端完成。

### Files Modified

* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `src/Tau.Ai/Providers/StreamFunctions.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockMessageConverter.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-release-contracts.ps1`
* `GOAL.md`
* `next.md`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionFileExporterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentStartupResumeResolverTests.cs`

### Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~ModelConfigurationStoreTests|FullyQualifiedName~BedrockProviderTests"`: 39/39 passed.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`: 447/447 passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-package-consumer.ps1 -SkipRestore -Json`: succeeded, 85 assertions.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`: passed; project test counts were `Tau.Ai` 447, `Tau.Agent` 127, `Tau.Tui` 251, `Tau.CodingAgent` 631, `Tau.WebUi` 72, `Tau.Pods` 216.
* First `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` exposed the current-directory test isolation issue described above; after isolating the tests, targeted `CodingAgentSessionFileExporterTests|CodingAgentStartupResumeResolverTests` passed 11/11 and full `-RunSmoke` passed, including `verify-agent-package-consumer.ps1` 85 assertions, agent proxy server smoke 5/5, WebUi smoke and Mom smoke.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json`: `succeeded=true`; `agentPackageConsumer.assertionCount=85`.
* `git diff --check`: no whitespace errors; only the existing `docs/QUALITY_SCORE.md` CRLF normalization warning.
