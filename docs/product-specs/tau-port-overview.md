# 产品规格：Tau — pi-mono .NET 10 移植

## 背景

[pi-mono](https://github.com/badlogic/pi-mono) 是一个 TypeScript AI Agent 工具集，包含统一 LLM API、Agent 运行时、编码 Agent CLI 等。Tau 将其移植到 .NET 10 生态，使 C# 开发者能直接使用相同的 Agent 能力。

## 目标用户

- 使用 .NET 技术栈的开发者和团队
- 希望在 C# 项目中嵌入 AI Agent 能力的产品
- 需要自托管/定制编码 Agent 的 .NET 团队

## 功能范围

### P0 — 核心（MVP）

| 功能 | 对应上游 | 验收标准 |
|---|---|---|
| 统一 LLM 流式 API | pi-ai | 已有 OpenAI、Anthropic、Google，以及 Azure OpenAI Responses、OpenAI Codex、Mistral、Vertex、Gemini CLI、Bedrock 的第一轮 provider / auth / model registry 接线；流式输出文本和工具调用事件 |
| Agent 运行时 | pi-agent-core | 支持工具注册、执行循环、多轮对话状态管理 |
| 编码 Agent CLI | pi-coding-agent | 终端交互式会话，能读写文件、执行命令、搜索代码 |
| 终端 UI | pi-tui | 当前先完成最小 transcript / input buffer / session 层，后续再补 Markdown 渲染、差分刷新、输入编辑 |

### P1 — 扩展

| 功能 | 对应上游 | 验收标准 |
|---|---|---|
| Web 聊天界面 | pi-web-ui | Blazor Server 实现，实时流式显示 Agent 响应 |
| Slack 机器人 | pi-mom | 接收 Slack 消息并委派给 Agent 处理 |
| Pod 管理 CLI | pi-pods | 管理 vLLM GPU Pod 的生命周期 |

### P2 — 增强

- Native AOT 发布，缩减二进制体积
- MCP (Model Context Protocol) 服务端/客户端支持
- 插件/扩展系统
- 更高保真的 provider 调用、OAuth 流程和 generated models 管线

## 非目标

- 不做 1:1 API 兼容（接口设计遵循 .NET 习惯）
- 不移植 pi-mono 的构建工具链（用 .NET 原生工具替代）
- 不支持 Node.js 运行时

## 技术约束

- 目标框架：net10.0
- 最低 C# 版本：14
- 尽量减少第三方依赖，优先用 BCL
- 序列化必须支持 source generator（AOT 友好）
- 所有 HTTP 调用必须支持 CancellationToken

## 验收标准（整体）

1. 当前阶段至少保持 `scripts/verify-dotnet.sh` 这一条项目级验证链稳定通过，并单独追踪 `Tau.slnx` build debt
2. 核心库测试覆盖率 ≥ 80%
3. 编码 Agent CLI 能完成：读文件、写文件、执行 shell 命令、多轮对话
4. 流式响应延迟不超过上游 TypeScript 版本的 2 倍
5. 支持 Windows / Linux / macOS
