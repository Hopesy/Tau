## [2026-05-21 19:52] | Task: CodingAgent RPC queue / auto-compaction controls

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `gpt-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### 📥 User Query

> 继续下一步，按仓库计划继续 Tau.CodingAgent 的 pi-mono RPC parity 迁移；完成一个任务后同步文档/history 并指定下一步计划。

### 🛠 Changes Overview

**Scope:** `Tau.Agent`、`Tau.CodingAgent`、`Tau.CodingAgent.Tests`、`Tau.Agent.Tests`、repo docs

**Key Actions:**

* **Agent queue mode**: 新增 `AgentQueueMode`，让 `AgentRuntime` 的 steering / follow-up queues 支持 `all` 与 `one-at-a-time`；默认对齐上游 `one-at-a-time`。
* **RPC controls**: `CodingAgentRpcHost` 新增 `set_steering_mode`、`set_follow_up_mode`、`set_auto_compaction`；active prompt 期间拒绝运行时设置变更；`get_state` 改为返回 runner/settings backed `steeringMode`、`followUpMode`、`autoCompactionEnabled`。
* **Settings contract**: `CodingAgentSettingsStore` 新增 `steeringMode`、`followUpMode`、`autoCompactionEnabled`，并兼容旧 `queueMode -> steeringMode` 读取迁移。
* **Startup / reload**: `Program.cs` 启动时恢复 queue modes；`/reload` 同步 settings queue modes 到当前 runner；`/settings` summary 展示 queue modes 和 auto-compaction 设置。
* **Tests**: 新增 runtime queue mode tests、RPC queue/auto-compaction tests、settings migration tests，并更新 settings/reload summary 回归。
* **Docs**: 同步 README、Architecture、Quality Score、两份 active plan、next 和 release notes，明确 `autoCompactionEnabled=true` 不会生成 threshold budget，实际自动触发仍依赖 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS`。

### 🧠 Design Intent (Why)

上游 RPC queue modes 是运行时行为，不应只保存在 settings 或只在 `get_state` 中硬编码。Tau 这次把 queue mode 落到 `AgentRuntime` 的 drain 语义，并补 targeted tests 防止 `all` 模式因本轮已 drain 的 steering message 触发空转 LLM turn。

Auto-compaction 在 Tau 里已经是 threshold-driven hook；RPC boolean 与 threshold budget 是两个不同事实。`set_auto_compaction` 因此只控制 settings/state boolean，`false` 会禁止生产入口自动触发，`true` 不会凭空生成 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS`。RPC prompt 自动 compaction hook 没有并入本切片，避免和 retry rollback snapshot / JSONL compaction entry 写入顺序交错。

### 📁 Files Modified

* `src/Tau.Agent/Runtime/AgentQueueMode.cs`
* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentAutoCompactionOptions.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.Agent.Tests/AgentRuntimeQueueModeTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：通过，58/58。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，238/238。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；测试计数 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 238、`Tau.Pods.Tests` 32。
* `git diff --check`：退出码 0；仅输出既有 CRLF normalization warnings。
