## [2026-06-17 01:05] | Task: 透传 Tau.Agent builder 的 prepareArguments

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `PowerShell`

### 📥 User Query

> 继续按照goal.md的指引移植

### 🛠 Changes Overview

**Scope:** `src/Tau.Agent/Platform`, `tests/Tau.Agent.Tests`, `scripts`, `docs/exec-plans/active`, `docs/QUALITY_SCORE.md`, `next.md`, `GOAL.md`

**Key Actions:**

* **[Action 1]**: 让 `AgentApplicationBuilder.AddTool(..., prepareArguments:)` 透传到 `DelegateAgentTool`，把平台层的 tool 参数预处理能力暴露给外部消费者
* **[Action 2]**: 增加平台测试和 public API compile sample，证明 `prepareArguments` 会在 tool 执行前生效且可编译消费
* **[Action 3]**: 扩展外部 `Tau.Agent` package consumer smoke，打印并断言 `toolResult=prepared package consumer`，让 release contract 也能观察到 package consumer 中的参数预处理结果

### 🧠 Design Intent (Why)

*把 Tau.Agent 的 builder surface 补到和现有 delegate tool 能力一致，减少 facade 和 runtime 之间的能力断层，同时用最小测试证明这个新增入口对外可用。*

### 📁 Files Modified

* `src/Tau.Agent/Platform/AgentApplicationBuilder.cs`
* `tests/Tau.Agent.Tests/AgentPlatformTests.cs`
* `tests/Tau.Agent.Tests/AgentPublicApiCompileSampleTests.cs`
* `scripts/verify-agent-package-consumer.ps1`
* `scripts/verify-release-contracts.ps1`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `GOAL.md`
