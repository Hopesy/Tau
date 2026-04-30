## [2026-04-30 00:16] | Task: Bedrock shared credentials profile

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4 Codex`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau 从 pi-mono-main 移植，持续推进 Bedrock provider 后续缺口。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` Bedrock credential resolution

**Key Actions:**

* **[Resolver]**: 新增 `BedrockProfileCredentialsResolver`，读取 AWS shared credentials/config profile 中的 `aws_access_key_id`、`aws_secret_access_key`、`aws_session_token` 和 `region`。
* **[Options]**: `BedrockOptions` 增加 `Profile / CredentialsFile / ConfigFile`，用于测试和显式指定 profile 文件。
* **[Provider]**: Bedrock SigV4 credential 解析顺序扩展为 explicit/env credentials 优先，其次 shared profile；region 解析扩展为 explicit/env/shared profile/默认 `us-east-1`。
* **[Tests]**: `BedrockProviderTests` 新增 shared profile 测试，固定 region endpoint、session token header 和 SigV4 credential scope。
* **[Docs]**: 同步 architecture、quality、next、baseline plan 与本切片 execution plan。

### 🧠 Design Intent (Why)

Bedrock 常见本地开发路径依赖 `AWS_PROFILE` 和 `~/.aws/credentials`。在保持 Tau.Ai 零外部依赖的前提下，先支持静态 shared credentials/profile，可以覆盖最常见 SigV4 使用方式，并把 SSO、AssumeRole、credential_process、IMDS/ECS/web identity 留给后续 credential chain 切片。

### 📁 Files Modified

* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockProfileCredentialsResolver.cs`
* `tests/Tau.Ai.Tests/BedrockProviderTests.cs`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/active/2026-04-29-bedrock-shared-credentials.md`
