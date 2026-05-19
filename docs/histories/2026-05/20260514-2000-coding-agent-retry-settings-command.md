## [2026-05-14 20:00] | Task: CodingAgent retry settings command baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau.CodingAgent P2 parity，完成 in-flight retry settings command/session visibility baseline。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` CLI runtime、settings store、tests、docs

**Key Actions:**

* **`/retry` 命令**: 新增 `/retry [current|default|off|<max attempts> [base delay ms]]`，支持查看、关闭、恢复 env/default、设置 attempts/base delay，并共用 command catalog usage。
* **Settings 持久化**: `CodingAgentSettingsStore` 保存 `retryMaxAttempts` 和 `retryBaseDelayMilliseconds`，`/model` / `/provider` 保存默认模型时保留 retry 字段。
* **运行态热更新**: `CodingAgentHost` 通过 router callback 接收 retry options 变更，让同进程下一轮普通输入立即使用新策略；`Program.cs` 改为 settings 优先、environment fallback。
* **Session 可见性**: `/session` 输出当前 retry policy，方便不用打开 settings JSON 就能确认运行态策略。
* **回归测试**: 覆盖 `/retry` current/off/default/numeric/invalid usage、settings retry fields、host 同进程关闭 retry 后不再自动重试、`/session` retry policy 展示。

### 🧠 Design Intent (Why)

上一个切片已经让 retry 行为可审计，但用户仍只能通过 env 控制 retry。这里先做 Tau-native CLI/settings baseline：命令面可见、settings 可持久化、host 可热更新，同时明确不把它写成完整上游 RPC/settings UI/cancellation parity。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRetryOptions.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
