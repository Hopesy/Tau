# AI event-stream primitive row closed to verified

- 时间：2026-06-12 19:01
- 主线：`GOAL.md` 100% pi-mono parity；integer row-closure pilot 继续。
- 角色：Main Integrator。

## 本轮 slice

把 parity matrix 的 `packages/ai/src/utils/event-stream.ts` 行从 `ported` 提升到 `verified`，machine 计数 verified 从 5 -> 6（其余 ported 26、partial 197、missing 1、external-e2e-needed 31、non-goal-proposed 1，total 262 不变）。

## 缺口与依据

`EventStream<TEvent,TResult>` 和 `AssistantMessageStream` 是 channel-backed 的 push/pull 流原语，纯确定性、无网络依赖，此前没有针对原语自身契约的专项测试——只被 provider 测试间接使用。matrix 旧 note 把“final acceptance 依赖每个 provider 发出同样的 terminal event contract”作为该行的 open 项，但那部分实际上属于 provider-family e2e（由 line 73 stream/event abstractions 行与各 `external-e2e-needed` provider 行管理），不是该原语自身的行为契约。

## 新增测试

新增 `tests/Tau.Ai.Tests/EventStreamTests.cs` 12 个直接单元测试，把 `Tau.Ai.Tests` 从 332 提到 344：

- Push 事件经 `IAsyncEnumerable` 按序消费；
- `isComplete` 命中后 channel 关闭、`ResultAsync` 以 `extractResult` 结果完成；
- 完成后再次 Push 抛 `InvalidOperationException("Stream already completed.")`；
- `isComplete` 命中但 `extractResult` 返回 null 时 `ResultAsync` 以 `InvalidOperationException` 失败；
- `End(result)` 直接完成 channel 与 result；
- `Fault(ex)` 让 channel 枚举与 `ResultAsync` 都抛同一异常；
- enumerator 尊重 `CancellationToken`；
- `AssistantMessageStream`：`DoneEvent` -> `done.Message`；`ErrorEvent` 带 `Message` 时用该 message；`ErrorEvent` 仅带 `Partial`/`Error` 时合成 `AssistantMessage`（保留 `ErrorMessage`、`Content`、`StopReason`、`Usage`、`Api`、`Provider`、`Model`、`ResponseId`、`Timestamp`）；非终态事件不提前完成 `ResultAsync`。

## 验证

- focused 复跑 `Tau.Ai.Tests` 344/344；
- 项目级 `verify-dotnet.ps1 -SkipRestore` 全绿：Ai 344、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216。

## 边界

该行只收口流原语自身的 push/pull/complete/result/fault/cancellation 与 `AssistantMessageStream` 终态映射契约。跨 provider 真实 SSE/eventstream 的 terminal-event 一致性仍由 stream/event abstractions 行与各 provider `external-e2e-needed` 行管理，不被此行误标完成。
