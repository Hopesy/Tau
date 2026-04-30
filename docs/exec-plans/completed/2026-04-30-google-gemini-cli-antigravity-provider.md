# Google Gemini CLI / Antigravity provider fidelity 支持计划

## 目标

把 `google-gemini-cli` provider 从当前简化 Cloud Code Assist 请求推进到更接近上游 pi-mono 的真实行为，覆盖 Gemini CLI 与 Antigravity 共享 provider 的关键差异：headers、Antigravity endpoint fallback、retry delay、empty SSE retry、Claude thinking beta header、Antigravity request body。

## 范围

包含：

- Gemini CLI 默认 headers：`User-Agent`、`X-Goog-Api-Client`、`Client-Metadata`
- Antigravity 默认 headers：只发送可配置版本的 `User-Agent`，不带 Gemini CLI fingerprint headers
- Antigravity baseUrl 为空时的 endpoint fallback：daily sandbox -> autopush sandbox -> prod
- 403/404 endpoint cascade，429/5xx transient retry，`Retry-After` / rate-limit header / body retry delay 解析与 `MaxRetryDelay` 约束
- empty SSE stream retry，避免重复 start/done
- Claude thinking Antigravity model 加 `anthropic-beta: interleaved-thinking-2025-05-14`
- Antigravity request body 增加 `requestType: agent`，并注入上游 compact system instruction bridge
- Tau.Ai 单测固定 request body、headers、fallback、retry-delay、empty-stream 行为

不包含：

- Google Gemini CLI / Antigravity OAuth login flow
- image/tool multimodal routing 全量对齐
- abort/cancellation 细节全量对齐
- 真实 Cloud Code Assist e2e

## 背景

上游 `packages/ai/src/providers/google-gemini-cli.ts` 已经把 Gemini CLI 与 Antigravity 的协议差异收口到同一 provider：Antigravity 使用 sandbox endpoint fallback、不同 User-Agent、`requestType: agent`、Claude thinking beta header，并对空 SSE、429/5xx、server retry delay 做专门处理。Tau 当前实现只做了最小 payload 与单 endpoint 请求，本切片补齐最影响真实可用性的协议差异。

## 风险

- 风险：一次性搬完整 provider 会把 image routing、abort、model generated metadata 等大块逻辑混进来。
  - 缓解：本切片只补请求/headers/retry/empty stream，保持 GoogleStreamParser 与现有 message converter 不大改。
- 风险：测试里真实等待 retry delay 会拖慢。
  - 缓解：server delay 超过 `MaxRetryDelay` 时直接报错；普通 retry 使用很小的内部 backoff 并在测试中固定短路径。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 provider fidelity 增量
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 决策记录

- 2026-04-30：按上游 `packages/ai/src/providers/google-gemini-cli.ts` 做窄范围移植，先补真实请求和恢复能力，不在本切片实现 OAuth login 或 generated model 全量同步。

## 验证结果

- 2026-04-30：项目级顺序 build/test 通过。
- `Tau.Ai.Tests`：50 passed。
- `Tau.Agent.Tests`：2 passed。
- `Tau.Tui.Tests`：4 passed。
- `Tau.CodingAgent.Tests`：6 passed。
- `Tau.Pods.Tests`：7 passed。
- 所有项目级 build 均为 0 warnings / 0 errors。