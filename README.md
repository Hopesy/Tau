# Tau

Tau 是 [pi](https://github.com/earendil-works/pi) 当前主线的 .NET 10 移植仓库，参考项目位于 `C:\Users\zhouh\Desktop\参考源码\pi-main`。当前仓库只保留与参考项目 `packages` 目录对齐的四个模块：

| pi-main package | Tau project | 说明 |
| --- | --- | --- |
| `packages/ai` | `src/Tau.Ai` | 多 provider LLM / image API、模型目录、provider collection、认证和流式协议 |
| `packages/agent` / `pi-agent-core` | `src/Tau.AgentCore` | Agent runtime、tool calling、AgentHarness、状态管理和应用底座 |
| `packages/coding-agent` | `src/Tau.CodingAgent` | Coding agent CLI/runtime |
| `packages/tui` | `src/Tau.Tui` | Terminal UI 基础组件 |

旧上游已经移除的 `web-ui`、`mom`、`pods` 以及本仓库对应的历史迁移项目不再作为当前结构目标维护。

CLI 默认每次启动创建独立会话，保存在用户 `.tau/sessions/--工作目录编码--/` 下。使用 `--continue` 继续当前项目最近有消息的会话，`--session <文件路径或ID前缀>` 打开指定会话，`--session-dir <目录>` 指定存储位置，`--no-session` 禁止保存与恢复。SDK 默认目录位于其 `AgentDirectory` 下，同样按工作目录隔离。

旧项目目录中的 `.tau/coding-agent-session.jsonl`、`.tau/coding-agent-session.json` 保留原文件，需要用 `--session` 显式打开；原有会话文件环境变量仍作为显式路径生效。恢复时，思考等级按显式参数、会话记录、全局设置、`medium` 默认值选择，并限制到模型支持范围；保存的 `off` 不会被全局设置覆盖。

扩展现可通过 `pi.appendEntry` 保存不发送给模型的 JSON 状态，通过 `pi.setSessionName` / `pi.setLabel` 保存名称和标签，并用 `ctx.sessionManager` 读取会话树。JSONL 支持跨进程恢复；`--no-session` 只保留内存状态。旧平面 JSON 仍支持名称，自定义条目和标签需使用 JSONL。

认证命令可独立运行，无需启动聊天会话：`tau auth check --provider <提供方> --json` 返回 `ready`、`not_ready` 或 `invalid`，退出码分别为 0、1、2。检查默认刷新过期 OAuth；追加 `--no-refresh` 只检查已有配置。默认输出不含凭据，只有显式添加 `--credentials` 才返回凭据值。

`tau auth print-api-key --provider <提供方>` 输出 API key；`tau auth print-bearer-token --provider <提供方>` 输出 OAuth bearer，并默认要求至少 30 分钟剩余有效期，可用 `--min-expiry 1h` 调整。两种打印命令也支持 `--model <模型>`；多个已配置提供方匹配时需补充 `--provider`。`tau auth --help` 查看完整参数。

迁移进展见 [迁移记录](迁移.md)，真实 Responses 与 CLI 验证步骤见 [实测说明](tools/Tau.ResponsesSmoke/README.md)，会话扩展示例见 [session-metadata.example.js](tools/Tau.ResponsesSmoke/session-metadata.example.js)。
