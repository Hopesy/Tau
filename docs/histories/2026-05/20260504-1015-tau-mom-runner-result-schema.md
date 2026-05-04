## [2026-05-04 10:15] | Task: tau-mom runner / result schema seam

### 🤖 Execution Context

* **Agent ID**: `Claude Code`
* **Base Model**: `Opus 4.7 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 按照计划继续执行
> 按这个走，参考项目路径你知道吗

### 🛠 Changes Overview

**Scope:** `Tau.Mom`, `Tau.Agent.Tests`

**Key Actions:**

* **[Structured tool events]**: 把 `DelegationExecution.ToolEvents` 从 `IReadOnlyList<string>` 升级为 `IReadOnlyList<DelegationToolEvent>`，每个事件携带 `Phase` (`start`/`end`)、`ToolName`、`ToolCallId`、`IsError?`、`DurationMs?`，由 runner 通过 `Stopwatch` 与 `pendingTools` 字典关联起止时间。
* **[Stop reason / usage]**: 给 `DelegationExecution` 与 `DelegationResult` 补 `StopReason`（沿用 Tau.Ai `StopReason` 枚举的 snake_case 映射，加 `cancelled` / `error`）和 `DelegationUsage`（input/output/cache token 与可选总成本，使用 `ModelCatalog.CalculateCost` 计算）。
* **[Runner factory seam]**: `RuntimeDelegationAgentRunner` 改为接受 `Func<string, string, ICodingAgentRunner>` 工厂；保留无参构造默认调用 `RuntimeCodingAgentRunner.Create`。这样 Slack/workspace/sandbox 适配层后续可以注入自己的 runner，并让单测能用 fake 直接驱动事件流。
* **[JSON context / processor]**: `MomJsonContext` 注册 `DelegationToolEvent` 与 `DelegationUsage`；`FileDelegationProcessor` 透传 stop reason / usage，runner 抛错回退路径补 `StopReason: "error"`。
* **[Tests]**: 升级 `FileDelegationProcessorTests` 的 fake runner 使用结构化事件并断言 outbox JSON 含 `stopReason`/`phase`/`durationMs`/`inputTokens` 字段；新增 `RuntimeDelegationAgentRunnerTests` 覆盖 token 聚合 + 总成本、`AgentEnd` 错误路径与默认 provider/model 解析。
* **[Live verification]**: `dotnet build src/Tau.Mom/Tau.Mom.csproj`、`dotnet test tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj` 全部通过（5/5）。

### 🧠 Design Intent (Why)

`Tau.Mom` 切片 D 的第一步是把 runner / result schema 收口为更稳定的 seam，再去铺 Slack / workspace / sandbox。现在 ToolEvents 是 `List<string>`，丢失了 toolCallId 与持续时间，未来 Slack 适配层既无法给每条 tool call 单独渲染 `→ tool / ✓ tool (1.2s)`，也无法把 thread 内的 result 与原 call 对应起来。同时 stop reason 与 usage 都没有暴露，`AgentEnd` 错误已能感知，但调用方拿不到 token / cost。本轮在不引入 Slack SDK / sandbox 的前提下：

- 让 ToolEvents 携带 phase / id / name / 错误位 / 持续毫秒，未来可直接喂给任意 UI；
- 把 stop reason 升级为字符串，沿用 Tau.Ai 的 `StopReason`，并对接 OperationCanceled / AgentEnd 错误两条特殊路径；
- 在 `MessageEnd` 处聚合 token 与 `ModelCatalog.CalculateCost`，避免上层重复实现成本计算；
- 把 runner 解耦为可注入工厂，单测可以用 `ICodingAgentRunner` fake 验证事件聚合逻辑，而不是只能跑真实 provider。

这套 seam 与 pi-mono 上游 `agent.ts` 的 `run() → { stopReason, errorMessage }` 与 `runState.totalUsage` 思路对齐，后续可以在不重写底层契约的前提下，分别接 Slack 渲染、workspace memory 注入与 sandbox 进程隔离。

### 📁 Files Modified

* `src/Tau.Mom/DelegationExecution.cs`
* `src/Tau.Mom/DelegationResult.cs`
* `src/Tau.Mom/DelegationToolEvent.cs` (new)
* `src/Tau.Mom/DelegationUsage.cs` (new)
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `src/Tau.Mom/FileDelegationProcessor.cs`
* `src/Tau.Mom/MomJsonContext.cs`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs` (new)
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
