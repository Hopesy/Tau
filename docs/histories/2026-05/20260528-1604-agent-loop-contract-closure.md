# 变更历史：Agent loop / schema contract closure

## 用户诉求

继续 Tau 对齐 `pi-mono-main` 的 100% parity 计划，推进 Phase 2 的 Agent loop / schema 公共合同收口，并同步 plan、next、quality 和 history。

## 本次变更

- 扩展 `Tau.Agent` 的事件合同，补上 `AgentEndEvent.Messages`、`TurnEndEvent.Message/ToolResults`、`MessageUpdateEvent.Message`、`ToolExecutionStartEvent.Args`、`ToolExecutionUpdateEvent.ToolName/Args/PartialResult`、`ToolExecutionEndEvent.ToolName/IsError`。
- 新增 `ToolArgumentValidator`，在 Tau.Agent 内部实现当前工具 schema 需要的 object/array/required/anyOf/enum/基本类型校验与简单 coercion。
- 调整 `ToolExecutor`，把参数解析、prepare/before/execute/after 阶段的异常收敛为 error tool result，而不是直接打断 runtime 枚举；同时把 tool update callback 变成可观测的 `tool_execution_update` 事件。
- 调整 `AgentRuntime`，让 `turn_end` 和 `agent_end` 带上当前 turn message/tool results 与最终消息列表。
- 同步 `Tau.CodingAgent`、`Tau.WebUi`、`Tau.Mom` 的消费者逻辑以适配新增事件字段，并补齐 RPC 序列化字段。
- 新增 `Tau.Agent.Tests` 合同测试，覆盖 schema 校验、参数 coercion、prepare/before/execute/after 异常收敛、tool update 事件和 turn/end payload。
- 新增 `AgentPublicApiCompileSampleTests`，用外部消费者样例冻结 `AgentOptions` / `Agent` facade、`AgentRuntime` / `AgentLoopConfig`、`ProxyStreamProvider` / `ProxyStreamOptions` 和 Agent event payload 的公共入口。
- 对照上游 `agent.ts handleRunFailure`，补齐 high-level `Agent` facade 的 failure/cancel synthesis：stream fault 会追加空文本 assistant failure message，cancellation 会追加空文本 assistant aborted message，并让 `agent_end.messages` 只报告本轮 synthetic assistant message。
- 新增 `StopReason.Aborted`，并同步 proxy stop reason、OpenAI Responses `cancelled/aborted` 状态映射和 Mom stop reason normalization。
- 新增 `AgentFacadeTests` 覆盖 stream fault 与 cancellation 两条 facade failure message contract。
- 对照上游 `agent-loop.ts createAgentStream()`，新增 low-level `AgentRuntime.RunStream(...)`，返回 `EventStream<AgentEvent, ChatMessage[]>`，并从 `agent_end.messages` 提取最终 `ResultAsync`。
- 扩展 `AgentRuntimeContractTests` 和 `AgentPublicApiCompileSampleTests`，覆盖 low-level EventStream wrapper 入口和 public API 使用样例。
- 对照上游 parallel tool execution，调整 Tau parallel timing：所有 runnable tool 先按 assistant source order 发 `tool_execution_start` / prepare，再并发执行；tool update 可在前序 tool 未完成时实时流出，最终 `tool_execution_end` / tool result 仍按 source order 发出。
- 补 low-level assistant stream cancellation terminal contract：`RunAsync` 在 assistant stream cancellation 时合成 aborted assistant message，并发 `turn_end` / `agent_end`；`RunStream.ResultAsync` 通过 `agent_end.messages` 正常完成而不是 fault。
- 保留 aborted `ErrorEvent` partial 的 `StopReason.Aborted`，避免 low-level runtime 把 provider 已表达的 abort 误归为 `StopReason.Error`。
- 调整 high-level `Agent` facade 的 runtime event normalization，低层 cancellation 已发 terminal event 时仍保持 facade `agent_end.messages` 只报告本轮 failure assistant 的既有合同。
- 补 tool cancellation cleanup：tool prepare/before/execute/after 阶段因运行 token 取消触发的 `OperationCanceledException` 会转成 `Operation canceled.` error tool result，不再让 runtime 枚举直接 fault。
- 调整 `AgentRuntime` pending tool calls 清理为 `finally`，避免 tool executor 早退后留下 stale pending 状态。
- 调整 parallel tool update wait，使 cancellation 不会在 sibling tool result 归集前打断枚举；新增测试固定一个 tool 成功、一个 tool 取消时，`tool_execution_end` 与 `turn_end.toolResults` 仍按 assistant source order 输出。

## 设计意图

这次关闭 Agent loop / schema contract 的第一层公共边界，避免继续让工具参数错误、hook 异常或 tool update 丢失把 runtime 直接打断。实现保持在 Tau.Agent 内部，优先让公共事件和测试稳定；随后用 public API compile-sample 固定 facade/runtime/proxy/event 入口。failure/cancel synthesis 先放在 high-level facade catch 路径，因为这是上游 `Agent` 用户可见的状态与 `agent_end` 合同；low-level EventStream wrapper 则放在 `AgentRuntime.RunStream(...)`，不替换现有 `RunAsync` 枚举入口，避免打断当前 CodingAgent/WebUi/Mom 消费路径。parallel timing 按上游“prepare 顺序、execute 并发、final result 源顺序”收敛；assistant stream cancellation 固定为 aborted terminal event；tool cancellation 则按上游 `executePreparedToolCall` / `finalizeExecutedToolCall` 的 catch-as-tool-result 语义收敛为 error tool result，并保留 terminal tool event 和 pending cleanup。剩余 Agent 缺口继续保留为 `TransformContext` cancellation parity。

## 验证

- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentRuntimeContractTests" --no-restore --verbosity minimal`
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPublicApiCompileSampleTests" --no-restore --verbosity minimal`
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "FullyQualifiedName~AgentFacadeTests|FullyQualifiedName~ProxyStreamProviderTests" --no-restore --verbosity minimal`，通过 10/10。
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeContractTests|FullyQualifiedName~AgentPublicApiCompileSampleTests" --no-restore --verbosity minimal`，通过 8/8。
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeContractTests" --no-restore --verbosity minimal`，通过 12/12。
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeContractTests" --no-restore --verbosity minimal`，本阶段通过 14/14。
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`，本阶段通过 114/114。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`，上一阶段通过 `Tau.Ai.Tests` 221、`Tau.Agent.Tests` 107、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 433、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`，本阶段通过 `Tau.Ai.Tests` 221、`Tau.Agent.Tests` 114、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 433、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166，并完成 WebUi 与 Mom `--once` smoke。

## 受影响文件

- `src/Tau.Agent/Abstractions/AgentEvents.cs`
- `src/Tau.Agent/Abstractions/IAgentTool.cs`
- `src/Tau.Agent/Agent.cs`
- `src/Tau.Agent/Proxy/ProxyStreamProvider.cs`
- `src/Tau.Agent/Runtime/AgentRuntime.cs`
- `src/Tau.Agent/Runtime/ToolExecutor.cs`
- `src/Tau.Agent/Runtime/ToolArgumentValidator.cs`
- `src/Tau.Ai/Abstractions/Messages.cs`
- `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
- `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
- `src/Tau.WebUi/Services/WebChatService.cs`
- `tests/Tau.Agent.Tests/AgentFacadeTests.cs`
- `tests/Tau.Agent.Tests/AgentRuntimeContractTests.cs`
- `tests/Tau.Agent.Tests/AgentPublicApiCompileSampleTests.cs`

## [2026-05-28 19:28] | Task: Agent TransformContext cancellation closure

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, C:\Users\zhouh\Desktop\Tau`

### User Query

> 接续 Tau 100% pi-mono parity 的 Phase 2 Agent lane，继续把剩余 Agent loop contract 缺口收口。

### Changes Overview

**Scope:** `src/Tau.Agent`, `tests/Tau.Agent.Tests`, `docs/ARCHITECTURE.md`, `docs/QUALITY_SCORE.md`, `docs/exec-plans/active`, `next.md`

**Key Actions:**

* **Context transformer cancellation**: 将 `ContextTransformer` 改成 cancellation-aware build flow，支持 `TransformContextAsync` / `TransformContext` 在构建 LLM context 前接收 `CancellationToken`，并在取消时直接走 aborted terminal path。
* **Runtime cancellation terminal**: `AgentRuntime` 在 transform 阶段取消时会合成 aborted assistant failure message，发出 `MessageStartEvent` / `MessageEndEvent` / `TurnEndEvent` / `AgentEndEvent`，并跳过 provider 调用。
* **Tests**: 新增 `AgentRuntimeContractTests.RunAsync_TransformContextCancellationEmitsAbortedTurnAndSkipsProvider`，固定 transform 阶段取消时 provider 不会被调用，且 `turn_end` / `agent_end` 仍正确携带 aborted assistant。
* **Docs sync**: 把 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`、`docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md` 和 `next.md` 中过期的 `TransformContext` cancellation gap 改成已闭合，并同步测试计数。

### Design Intent (Why)

上游 `agent-loop.ts` 的 transform 阶段本来就应接收取消信号。Tau 之前虽然已经把 assistant stream cancellation、tool cancellation cleanup 和 parallel sibling result preservation 收口，但 transform 阶段仍然绕开了取消语义，导致 agent loop contract 在 provider 调用前就可能出现不一致的状态。把这一步补上之后，Tau.Agent 的本地 loop 合同就闭合了，剩余工作可以更清楚地转到 facade option pass-through、public export 形状和真实 proxy/e2e 验证。

### 验证

- `dotnet test tests\\Tau.Agent.Tests\\Tau.Agent.Tests.csproj --filter AgentRuntimeContractTests --no-restore --verbosity minimal`，通过 15/15。
- `dotnet test tests\\Tau.Agent.Tests\\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`，通过 115/115。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-dotnet.ps1 -SkipRestore -RunSmoke`，仓库级 build/test/smoke 全绿，覆盖 `Tau.Ai.Tests` 221、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 433、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166，并完成 WebUi 与 Mom `--once` smoke。

### 受影响文件

- `src/Tau.Agent/Runtime/ContextTransformer.cs`
- `src/Tau.Agent/Runtime/AgentRuntime.cs`
- `tests/Tau.Agent.Tests/AgentRuntimeContractTests.cs`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
