# 2026-06-12 17:52 — AI GitHub Copilot headers row closed to verified

## 切片

继续 integer row-closure pilot：把 parity matrix `packages/ai/src/providers/github-copilot-headers.ts`
行从 `ported` 提升到 `verified`，matrix machine count 的 `verified` 从 4 -> 5（其余 ported 27、partial 197、
missing 1、external-e2e-needed 31、non-goal-proposed 1，total 262）。

## 背景

`GitHubCopilotHeaders`（`src/Tau.Ai/Providers/GitHubCopilotHeaders.cs`）是纯确定性、无网络依赖的
header 构造工具，对照上游 `packages/ai/src/providers/github-copilot-headers.ts` 的
`inferCopilotInitiator` / `hasCopilotVisionInput` / `buildCopilotDynamicHeaders` 合同。此前该 surface
仅在 `OpenAiResponsesProviderTests` 中通过 request header 断言被间接覆盖（X-Initiator user/agent、
Copilot-Vision-Request 存在/缺失各一处），helper 自身的合同没有直接单元测试。

## 改动

新增 `tests/Tau.Ai.Tests/GitHubCopilotHeadersTests.cs`，12 项直接单元测试，把 `Tau.Ai.Tests`
从 320 提到 332：

- `CreateStaticHeaders`：User-Agent / Editor-Version / Editor-Plugin-Version / Copilot-Integration-Id
  四个固定值，且字典为 case-insensitive。
- `InferInitiator`：空列表 -> `user`；最后一条 user -> `user`；最后一条 assistant -> `agent`；
  最后一条 toolResult -> `agent`（即使更早有 user message）。
- `HasVisionInput`：user message 含 ImageContent -> true；toolResult 含 ImageContent -> true；
  纯文本 -> false；**assistant message 含 ImageContent -> false**（上游只检查 user / toolResult role，
  这是容易被误实现的细节）。
- `BuildDynamicHeaders`：无图片时只含 X-Initiator + Openai-Intent，不含 Copilot-Vision-Request；
  有图片时追加 `Copilot-Vision-Request=true`；X-Initiator 随末条消息 role 变化。

## 验证

- focused `GitHubCopilotHeadersTests` 12/12。
- 完整 `Tau.Ai.Tests` 332/332。
- 项目级 `verify-dotnet.ps1 -SkipRestore`：Ai 332、Agent 123、Tui 251、CodingAgent 631、WebUi 70、Pods 216。

## 边界

该 surface 为纯确定性 header 构造，`verified` 反映本地证据已对其完整覆盖且无 external-e2e 依赖。
真实 GitHub Copilot OAuth/token/endpoint 行为仍由其它 `external-e2e-needed` 行管理，与本 header
构造 surface 无关。
