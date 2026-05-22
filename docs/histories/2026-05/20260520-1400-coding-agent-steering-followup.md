## [2026-05-20 14:00] | Task: 接入 CodingAgent Steering / FollowUp CLI baseline

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows`

### 📥 User Query

> 继续推进 Tau.CodingAgent 与 pi-mono 的 parity 移植，接着当前 active plan 中未完成的 Steering/FollowUp 切片执行。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、`tests/Tau.CodingAgent.Tests`、`tests/Tau.Agent.Tests`、`tests/Tau.WebUi.Tests`、`docs/exec-plans/active/`

**Key Actions:**

* **runner seam 接线**: `ICodingAgentRunner` 新增 `Steer(string)` / `FollowUp(string)`，`RuntimeCodingAgentRunner` 将输入包装成 `UserMessage` 后转发到 `AgentRuntime` 现有 steering / follow-up 队列。
* **Host 运行中输入接线**: 新增 `ICodingAgentTurnInputSource` 与 `CodingAgentTurnInput`，`CodingAgentHost.RunSingleTurnAttemptAsync` 仅在 active runner turn 期间消费 source，并在 turn 结束后取消 listener。
* **生产 CLI baseline**: `SystemConsoleCodingAgentTurnInputSource` 用非阻塞 `Console.KeyAvailable` 轮询缓冲输入，Enter 提交 steering，Alt+Enter 提交 follow-up；`Program.cs` 只在真实交互式 editor 启用时注入该 source。
* **测试覆盖**: `CodingAgentHostTests` 新增 steering / follow-up targeted tests；fake runner 记录转发输入；Agent/WebUi 测试 fake runner 补齐接口成员，避免接口漂移。

### 🧠 Design Intent (Why)

上游 coding-agent 允许用户在 agent 运行中注入 steering 或 follow-up。Tau 的 `AgentRuntime` 已经有对应队列，缺的是 CLI host 层把运行中的用户输入转发进去。

本切片没有直接重写完整 TUI editor，而是先把 host 需要的输入源抽成可注入接口。这样测试可以 deterministic 地喂入 steering/follow-up，生产端也可以用 `Console.KeyAvailable` 避免不可取消的 `Console.ReadKey` 在 turn 结束后悬挂。输入转发不改变主 slash command loop、retry/rollback 或 session 持久化路径，后续 RPC mode 也可以复用同一个 runner seam。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTurnInputSource.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### ✅ Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：0 警告，0 错误
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：191/191 通过
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：54/54 通过
* `dotnet build tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`：0 警告，0 错误
