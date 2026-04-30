# GitHub Copilot Responses 动态头与图片输入支持计划

## 目标

把 `github-copilot -> openai-responses` 这条 Tau.Ai 调用链补到更接近上游的可用状态：请求自动生成 Copilot 动态 headers，Copilot 内建 model 带上静态 headers 与 image input modality，Responses message converter 支持带图片的 `ToolResultMessage`。

## 范围

包含：

- `OpenAiResponsesProvider` 为 `github-copilot` 生成 `X-Initiator` / `Openai-Intent` / `Copilot-Vision-Request`
- Copilot 内建 Responses model 带上静态 headers 与 `InputModalities = ["text", "image"]`
- `OpenAiResponsesShared` 为支持图片输入的 model 编码 `ToolResultMessage` 图片内容
- Tau.Ai 单测覆盖：
  - user/agent initiator
  - Copilot vision header
  - tool result image request body
  - Copilot built-in model 静态 headers 生效
- 同步 `next.md`、architecture、quality、baseline plan 与 history

不包含：

- GitHub Copilot device flow / OAuth login
- generated model catalog 全量同步
- Anthropic Copilot 路径回归
- 真实 Copilot e2e

## 背景

上游 `openai-responses.ts` 对 `github-copilot` 会按上下文动态生成 headers：最后一条消息不是 user 时 `X-Initiator=agent`，发送图片时加 `Copilot-Vision-Request=true`。同时 Copilot model 自带 VS Code/Copilot Chat 静态 headers。Tau 当前虽然已有 `github-copilot` provider/model 映射，但 Responses provider 还没有这层行为，而且 `ToolResultMessage` 图片会在转换阶段被丢弃。

## 风险

- 风险：把 Copilot 专属 header 逻辑散进 provider，后续 Anthropic / Completions 路径重复实现。
  - 缓解：抽成独立 `GitHubCopilotHeaders` helper，后续其他 provider 可复用。
- 风险：tool-result 图片编码改变 Responses converter 共享行为。
  - 缓解：仅在 model 支持 `image` input modality 时输出 `input_image` 数组，否则保留文本或 `(see attached image)` fallback，并用回归测试固定。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 Copilot Responses headers/tool-result image 支持
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 验证结果

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`：通过，63 passed。
- 项目级顺序 build/test（7 个 source 项目 build、5 个 test 项目 build、5 个 test 项目 test）：通过；测试计数为 Tau.Ai 63、Tau.Agent 2、Tau.Tui 4、Tau.CodingAgent 6、Tau.Pods 7。

## 决策记录

- 2026-04-30：把 Copilot 动态 headers 抽成共享 helper，而不是继续塞进 `OpenAiResponsesProvider` 私有方法。原因是同一套 header 语义后续还会在 Anthropic / 其他 Copilot provider 路径复用。
