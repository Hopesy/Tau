# Azure OpenAI Responses 专用 provider 支持计划

## 目标

把 `azure-openai-responses` 从 `OpenAiCompatibleProvider` chat-completions fallback 切换为专用 Responses API provider，使请求体使用 `input` / Responses SSE 语义，并支持 Azure base URL、resource name、deployment name map 与 api-version 配置。

## 范围

包含：

- 新增 `AzureOpenAiResponsesProvider`
- 复用 `OpenAiResponsesShared` 的 Responses input/tools/request 参数与 SSE 解析
- 支持 `AZURE_OPENAI_BASE_URL` / `AZURE_OPENAI_RESOURCE_NAME` / `AZURE_OPENAI_API_VERSION`
- 支持 `AZURE_OPENAI_DEPLOYMENT_NAME_MAP` 与显式 deployment option
- 请求 header 使用 `api-key`，并合并 model/options headers
- `StreamSimple` 映射 reasoning effort
- 单测覆盖 provider 注册类型、URL/baseUrl/resourceName、deployment map、request body、reasoning、stream usage/error
- 同步 `next.md`、architecture、quality、baseline plan 与 history

不包含：

- Azure AD / Managed Identity bearer token
- 真实 Azure e2e
- generated Azure model 全量同步
- service-tier pricing / cost multiplier

## 背景

上游 `packages/ai/src/providers/azure-openai-responses.ts` 已有独立 Azure OpenAI Responses provider：model 参数使用 deployment name，base URL 从 explicit/env/resourceName/model 解析，默认 API version 为 `v1`，请求走 Responses `input`，reasoning 模型会设置 reasoning。Tau 当前仍把该 api 注册到 `OpenAiCompatibleProvider`，会走 chat-completions 风格 `messages`，与 Responses 语义不符。

## 风险

- 风险：Azure endpoint 形态在不同部署上可能既有 `/openai/v1/responses`，也可能需要 query `api-version`。
  - 缓解：本轮按上游默认 `/openai/v1` base URL + `/responses` 走，并保留 `api-version` option/env 在 query 上显式传递，测试固定 URL。
- 风险：deployment name 与 model id 混用。
  - 缓解：按 explicit option > env map > model id 顺序解析。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 Azure OpenAI Responses provider
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 决策记录

- 2026-04-30：优先把 Azure 从 OpenAI-compatible fallback 拆出来，保持与 OpenAI Responses 共享 payload/parser，但把 Azure base URL、api-key header 与 deployment name 解析独立封装。
- 2026-04-30：Azure provider 不调用 `OpenAiResponsesShared.AddBaseParameters`，而是手动写入 `max_output_tokens` / `temperature` / `top_p` / `prompt_cache_key`。原因是 Azure 上游没有 `store=false` / `prompt_cache_retention` 语义，本轮避免把 OpenAI 专有参数带到 Azure 请求。
- 2026-04-30：Azure reasoning 仅在 `model.Reasoning` 为 true 时写入；Simple options 的 thinking level 映射到 `reasoning.effort`，并同步 `include=["reasoning.encrypted_content"]`。

## 验证结果

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`：通过，55 passed
- 项目级顺序 build/test：通过，source/test projects 均 0 warning / 0 error；测试结果为 `Tau.Ai.Tests` 55 passed、`Tau.Agent.Tests` 2 passed、`Tau.Tui.Tests` 4 passed、`Tau.CodingAgent.Tests` 6 passed、`Tau.Pods.Tests` 7 passed
