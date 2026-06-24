# Tau

Tau 是 [pi](https://github.com/earendil-works/pi) 当前主线的 .NET 10 移植仓库，参考项目位于 `C:\Users\zhouh\Desktop\pi-main`。当前仓库只保留与参考项目 `packages` 目录对齐的四个模块：

| pi-main package | Tau project | 说明 |
| --- | --- | --- |
| `packages/ai` | `src/Tau.Ai` | 多 provider LLM / image API、模型目录、provider collection、认证和流式协议 |
| `packages/agent` / `pi-agent-core` | `src/Tau.AgentCore` | Agent runtime、tool calling、AgentHarness、状态管理和应用底座 |
| `packages/coding-agent` | `src/Tau.CodingAgent` | Coding agent CLI/runtime |
| `packages/tui` | `src/Tau.Tui` | Terminal UI 基础组件 |

旧上游已经移除的 `web-ui`、`mom`、`pods` 以及本仓库对应的历史迁移项目不再作为当前结构目标维护。
