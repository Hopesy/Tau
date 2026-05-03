## [2026-05-03 10:00] | Task: Tau.CodingAgent auth status command skeleton

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 下一块。

### 🛠 Changes Overview

**Scope:** `src/Tau.Ai`, `src/Tau.CodingAgent`, `tests/`, `docs/`

**Key Actions:**

* **[Auth Status API]**: 新增 `ProviderAuthStatus`，并在 `ProviderAuthResolver` 增加 `GetStatus(...)`，统一报告 provider 凭证是否配置、来源、OAuth 状态和安全提示。
* **[Source Coverage]**: auth 状态覆盖 explicit、environment / ambient credentials、auth.json api_key、auth.json OAuth、models.json request auth 和 none，不回显 secret 原文。
* **[CodingAgent Commands]**: `ICodingAgentRunner` / `RuntimeCodingAgentRunner` 暴露 `GetAuthStatus(...)`；`CodingAgentHost` 新增 `/auth [provider]` 和 `/login [provider]`。
* **[Login Boundary]**: `/login` 当前只做已配置提示或未移植说明，不伪装成已完成 OAuth/device flow。
* **[Tests]**: 补 `ProviderAuthResolver.GetStatus(...)` 环境变量和 models.json 状态测试，以及 CodingAgent `/auth`、`/login` host 测试。
* **[Docs]**: 同步 `next.md`、architecture、quality score 与 active execution plan，把“auth 状态入口已完成，真实 OAuth 仍在 backlog”的边界落仓库。

### 🧠 Design Intent (Why)

* Tau.Ai 已有 env/auth.json/OAuth credential resolver 和 models.json request auth 合并，但 CodingAgent 之前没有安全可见的凭证状态面。
* 本轮先做 `/auth` 和 `/login` 骨架，给用户明确知道“当前 provider 是否可调用、凭证来自哪里、为什么不能 login”的入口。
* 不在本切片内实现 Anthropic/Copilot/Codex/Gemini 的真实 OAuth/device flow，避免把 provider auth 协议迁移和 CLI 命令面混在同一评审单元。

### 📁 Files Modified

* `src/Tau.Ai/Auth/ProviderAuthResolver.cs`
* `src/Tau.Ai/Auth/ProviderAuthStatus.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
