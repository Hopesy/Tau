# OpenAI Responses 家族迁移

## 用户诉求

继续从 `pi-mono-main` 向 Tau 执行迁移，按既有 execution plan 推进下一块高价值切片。

## 主要变更

- 新增 `src/Tau.Ai/Providers/OpenAiResponses/`：
  - `OpenAiResponsesShared`：Responses message/tool converter、tool-call id 归一、Codex JWT account id 解码、Codex event 归一、SSE event → Tau `StreamEvent` 翻译。
  - `OpenAiResponsesProvider`：`openai-responses` 专用 `/responses` SSE provider。
  - `OpenAiCodexResponsesProvider`：`openai-codex-responses` 专用 `/codex/responses` SSE provider，包含 ChatGPT account header、originator、OpenAI-Beta header 和 retry。
  - provider-specific options records。
- 更新 `BuiltInProviders`：
  - `openai-responses` 从 `OpenAiCompatibleProvider` 切到 `OpenAiResponsesProvider`。
  - `openai-codex-responses` 从 `OpenAiCompatibleProvider` 切到 `OpenAiCodexResponsesProvider`。
  - `azure-openai-responses` 与 `mistral-conversations` 继续保持 OpenAI-compatible 兜底。
- 更新 `BuiltInModels`：
  - `openai-codex` 内建模型 base URL 对齐 upstream 的 `https://chatgpt.com/backend-api`。
- 新增 Tau.Ai 单测：
  - Responses shared helper：tool-call id、synthetic tool result、JWT 解码。
  - Responses provider：request body shape、text stream event、tool call stream event、usage/stop reason。
  - Codex provider：JWT header、retry、`response.incomplete` stop reason。
- 同步 `next.md`、`docs/QUALITY_SCORE.md`、`docs/ARCHITECTURE.md` 和 active execution plans。

## 设计意图

这轮只收 OpenAI Responses 家族的 SSE 协议保真度，不把 Codex WebSocket、Copilot dynamic headers、Azure dedicated provider、service-tier pricing 或 Bedrock SigV4 混进同一轮，避免 provider 边界失控。

保留 source-generator JSON 路线，request body 继续用 `Dictionary<string, object>` 加 `OpenAiResponsesJsonContext`，避免回到反射序列化和 AOT/trim 风险。

## 关键受影响文件

- `src/Tau.Ai/Providers/OpenAiResponses/*`
- `src/Tau.Ai/Providers/BuiltInProviders.cs`
- `src/Tau.Ai/Registry/BuiltInModels.cs`
- `tests/Tau.Ai.Tests/OpenAiResponsesSharedTests.cs`
- `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
- `tests/Tau.Ai.Tests/OpenAiCodexResponsesProviderTests.cs`
- `next.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
- `docs/exec-plans/completed/2026-04-27-openai-responses-port.md`

## 验证

- `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`

项目级 build/test 总门禁已通过：`Tau.Ai.Tests` 26 passed、`Tau.Agent.Tests` 2 passed、`Tau.Tui.Tests` 4 passed、`Tau.CodingAgent.Tests` 6 passed、`Tau.Pods.Tests` 7 passed；所有 build 为 0 warnings / 0 errors。
