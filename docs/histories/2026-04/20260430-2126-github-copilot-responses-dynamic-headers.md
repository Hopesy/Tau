## [2026-04-30 21:26] | Task: GitHub Copilot Responses dynamic headers

### 🤖 Execution Context

* **Agent ID**: `codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 pi-mono 到 Tau 的迁移，推进下一块 Tau.Ai provider/API fidelity：GitHub Copilot dynamic headers / vision behavior 的完整 Responses 路径。

### 🛠 Changes Overview

**Scope:** `Tau.Ai` GitHub Copilot Responses provider 行为、Responses message converter、built-in model 元数据、Tau.Ai 回归测试、迁移计划与仓库状态文档。

**Key Actions:**

* **[Copilot helper]**: 新增 `GitHubCopilotHeaders`，统一生成 Copilot Chat 静态 headers，以及基于最后一条消息角色和图片输入生成动态 headers。
* **[Responses headers]**: `OpenAiResponsesProvider` 现在会在 `github-copilot` 请求上自动附加 `X-Initiator`、`Openai-Intent` 与 `Copilot-Vision-Request`，并继续允许 `options.Headers` 最后覆盖。
* **[Vision payload]**: `OpenAiResponsesShared` 现在支持把带图片的 `ToolResultMessage` 编码成 `function_call_output` 数组，保留 `input_text` + `input_image` 组合，而不是只丢文本。
* **[Model metadata]**: 现有 Copilot 内建 Responses model 增加 VS Code/Copilot Chat 静态 headers，并声明 `InputModalities = ["text", "image"]`。
* **[Regression tests]**: 补 Copilot static/dynamic headers、最后一条 user/toolResult 对 `X-Initiator` 的影响，以及 tool-result image request body 回归。
* **[Docs sync]**: 更新 `next.md`、architecture、quality score、baseline plan，并归档本切片 execution plan。

### 🧠 Design Intent (Why)

这轮不是单纯补几个 header 常量，而是把 `github-copilot -> openai-responses` 的上下文语义补齐：Copilot 计费和能力路由依赖 `X-Initiator` 与 `Copilot-Vision-Request`，而图片 tool-result 则决定多模态工具链能否真的走通。把这些逻辑抽到共享 helper 和 Responses converter 层，能保持 provider 代码简单，同时给后续 Copilot 相关路径复用同一套事实源。

### 📁 Files Modified

* `src/Tau.Ai/Providers/GitHubCopilotHeaders.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs`
* `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesShared.cs`
* `src/Tau.Ai/Registry/BuiltInModels.cs`
* `tests/Tau.Ai.Tests/OpenAiResponsesProviderTests.cs`
* `docs/exec-plans/completed/2026-04-30-github-copilot-responses-dynamic-headers.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
