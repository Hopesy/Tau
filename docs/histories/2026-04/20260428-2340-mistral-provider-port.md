# Mistral provider 专用迁移

## 用户诉求

继续从 `pi-mono-main` 向 Tau 执行迁移，不停在上一轮 OpenAI Responses 切片。

## 主要变更

- 新增 `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`。
- 将 `mistral-conversations` 从 `OpenAiCompatibleProvider` 切换到专用 `MistralProvider`。
- 补 Mistral 专属行为：
  - assistant/tool result 的 tool-call id 归一为 9 位字母数字 ID。
  - `SessionId` 自动写入 `x-affinity` header。
  - `SimpleStreamOptions.Reasoning` 映射到 Mistral `reasoning_effort` / `prompt_mode`。
  - stream 中的 Mistral usage 补写到 Tau `AssistantMessage.Usage`。
- 新增 `tests/Tau.Ai.Tests/MistralProviderTests.cs`，覆盖 provider 注册、payload/header、tool-call id 归一和 reasoning 参数。
- 同步 `next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md` 和 active execution plans。

## 设计意图

Mistral 的 wire 协议仍然接近 OpenAI chat streaming，但 provider 层有明确 Mistral 特性，继续用 OpenAI-compatible 兜底会丢失 `x-affinity`、tool-call id 长度约束和 reasoning 参数。本轮用直接 HTTP + StubHandler 测试收口，不引入 Mistral SDK，也不打真实网络。

## 关键受影响文件

- `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
- `src/Tau.Ai/Providers/BuiltInProviders.cs`
- `tests/Tau.Ai.Tests/MistralProviderTests.cs`
- `next.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
- `docs/exec-plans/completed/2026-04-28-mistral-provider-port.md`

## 验证

- `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`

项目级 build/test 总门禁已通过：`Tau.Ai.Tests` 30 passed、`Tau.Agent.Tests` 2 passed、`Tau.Tui.Tests` 4 passed、`Tau.CodingAgent.Tests` 6 passed、`Tau.Pods.Tests` 7 passed；所有 build 为 0 warnings / 0 errors。
