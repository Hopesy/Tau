# Codex WebSocket transport 支持计划

## 目标

把 `openai-codex-responses` 从当前 SSE-only provider 扩展到上游已有的 `transport: "websocket" | "auto"` 路径，支持会话级 WebSocket 连接缓存，并保留现有 SSE 作为默认与 fallback。

## 范围

包含：

- 在 Tau.Ai options 层加入 provider 可忽略的 transport 选项
- 为 Codex Responses provider 新增 WebSocket transport seam，默认用 .NET `ClientWebSocket`
- WebSocket 请求发送 `response.create` frame，并复用 Responses JSON event parser
- 支持 `SessionId` 级 socket 复用与空闲 TTL
- `auto` transport 在 WebSocket 未开始前失败时回退 SSE；显式 `websocket` 失败时直接报错
- 补 Stub/Fake WebSocket 单测覆盖 URL、headers、request frame、stream event、auto fallback 与 session reuse
- 同步 `next.md`、architecture、quality、baseline plan 与 history

不包含：

- OpenAI Codex OAuth login / refresh
- 真实 ChatGPT WebSocket e2e
- WebSocket 断线自动重连和半包恢复策略
- service-tier cost multiplier（后续单独切片）

## 背景

上游 `packages/ai/src/providers/openai-codex-responses.ts` 已支持 `transport` 选项：默认 SSE，`auto` 先试 WebSocket 未开始则回退 SSE，`websocket` 强制走 WebSocket。WebSocket 路径使用 `response.create` frame、`x-client-request-id` / `session_id` 与会话级 socket cache。Tau 当前 Codex provider 已有专用 SSE provider、JWT account header 和 retry，但还没有 WebSocket transport。

## 风险

- 风险：真实 Codex WebSocket handshake 细节可能随 ChatGPT 后端变化。
  - 缓解：本轮固定可测试 seam、headers、URL 和 frame 结构，不做真实 e2e；真实 endpoint 变化留给后续 e2e 切片。
- 风险：WebSocket cache 引入生命周期复杂度。
  - 缓解：只按 `SessionId` 复用空闲且 `WebSocketState.Open` 的连接；busy 同 session 请求临时新建非缓存连接；错误或 aborted 路径关闭连接。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 Codex WebSocket transport seam 与 session cache
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 决策记录

- 2026-04-30：先实现 Codex WebSocket 的最小可验证传输层，不引入第三方 WebSocket SDK；默认仍为 SSE，避免改变现有调用路径。
- 2026-04-30：`StreamOptions.Transport` 作为通用选项加入，但默认值保持 `Sse`，其他 provider 暂时忽略；Codex provider 才消费 `WebSocket` / `Auto`。
- 2026-04-30：WebSocket 成功路径在推送 `DoneEvent` 前释放 session lease，避免调用方读到 done 后立即发下一轮时仍看到连接 busy。
- 2026-04-30：Codex request body 顺手对齐上游关键语义：system prompt 走 `instructions`，`input` 不再混入 system prompt，默认 `text.verbosity=medium`，`prompt_cache_key` 直接跟随 `SessionId`。

## 验证结果

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`：通过，58 passed
- 项目级顺序 build/test：通过，source/test projects 均 0 warning / 0 error；测试结果为 `Tau.Ai.Tests` 58 passed、`Tau.Agent.Tests` 2 passed、`Tau.Tui.Tests` 4 passed、`Tau.CodingAgent.Tests` 6 passed、`Tau.Pods.Tests` 7 passed
