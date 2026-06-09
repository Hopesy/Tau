# CodingAgent RPC assistant event schema

## 用户诉求

继续按照 `GOAL.md` 推进 Tau 对 `C:\Users\zhouh\Desktop\pi-mono-main` 的 100% parity，不停在上一轮局部切片。

## 主要改动

- 对照上游 `packages/coding-agent/src/modes/rpc/rpc-types.ts`、`rpc-mode.ts`、`docs/rpc.md` 和 `packages/ai/src/types.ts`，将 Tau RPC `message_update` 输出从 Tau 私有 `streamEvent` 改为上游字段 `assistantMessageEvent`。
- `assistantMessageEvent` payload 补齐上游 assistant stream event 的本地 baseline：`partial`、`text_end/thinking_end.content`、`toolcall_end.toolCall` 和 `done/error.reason`。
- 新增 RPC host 回归，断言 `message_update` 不再输出旧 `streamEvent`，并固定 text/thinking/toolcall/done 的上游字段形状。
- 同步 `GOAL.md`、100% active plan、parity matrix、`next.md` 和 `docs/QUALITY_SCORE.md`，明确该切片只关闭 `message_update` assistant event shape baseline；完整 TypeScript extension runtime、package-loaded extensions、custom tools、全量 response/message schema、toolCall arguments object shape、settings/package/terminal contracts 和最终 RPC `verified` 状态仍保持 open。

## 设计动机

上游 RPC 协议把 streaming delta 放在 `message_update.assistantMessageEvent`，并沿用 `AssistantMessageEvent` union shape。Tau 之前输出 `streamEvent`，严格按上游协议解析的客户端会拿不到 streaming delta。这个切片只在 RPC 序列化边界做转换，保留底层 `Tau.Agent` 的 .NET-native `MessageUpdateEvent(StreamEvent, Message)` 模型，避免影响非 RPC 路径。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
- `GOAL.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `next.md`
- `docs/QUALITY_SCORE.md`

## 验证

- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentRpcHostTests" --no-restore --verbosity minimal`：57/57 passed。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter Rpc --no-restore --verbosity minimal`：64/64 passed。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：490/490 passed。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 280、Agent 120、Tui 251、CodingAgent 490、WebUi 61、Pods 216）。
