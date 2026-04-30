# Tau.Ai Mistral provider 专用移植

## 目标

把 `mistral-conversations` 从 `OpenAiCompatibleProvider` 兜底切到专用 `MistralProvider`，补上 upstream `pi-mono-main/packages/ai/src/providers/mistral.ts` 中 Tau 当前缺失的 Mistral 特有语义：

- Mistral tool-call id 归一为 9 位字母数字 ID
- `sessionId` → `x-affinity` header
- reasoning model 的 `prompt_mode` / `reasoning_effort`
- Mistral stream usage / finish reason / tool call event 翻译

## 范围

- 包含：
  - 新增 `src/Tau.Ai/Providers/Mistral/MistralProvider.cs`
  - `BuiltInProviders.RegisterAll` 将 `mistral-conversations` 切到 `MistralProvider`
  - StubHandler 测试覆盖 request body、x-affinity、tool-call id 归一、text/tool stream event
  - 同步 `next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、history
- 不包含：
  - 真实 Mistral 网络 e2e
  - Mistral SDK 引入
  - Bedrock SigV4、Google Vertex ADC、OAuth login flows
  - `Tau.slnx` / HintPath workaround 收口

## 风险

- Mistral SDK 的 TypeScript 类型是 camelCase，但 HTTP wire 通常是 snake_case；Tau 本轮直接打 HTTP，所以测试以 wire 形状为准。
- 现有 `OpenAiStreamParser` 可解析大部分 OpenAI-style chunk，但 Mistral tool id 归一、`x-affinity`、reasoning 参数必须放在专用 provider 层，不应继续塞进 OpenAI-compatible provider。

## 验证方式

- `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 最终跑项目级 build/test 总门禁。

## 进度记录

- [x] 新增 `MistralProvider`
- [x] 切换 builtin 注册
- [x] 新增 Tau.Ai.Tests 覆盖 Mistral payload / stream
- [x] 文档、next、quality、history 同步
- [x] build/test 全绿后归档

## 决策记录

- 2026-04-28：选择先移植 Mistral 专用 provider，而不是直接做 Bedrock SigV4。原因是 Bedrock 需要 AWS SigV4 与 EventStream parser，风险和体量更大；Mistral 可以用本地 StubHandler 明确证明 payload/header/stream 行为，并能从 P0 backlog 中移除一个 OpenAI-compatible 兜底项。
