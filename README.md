# Tau

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植仓库，目标是在 C# / .NET 生态中重建其核心 AI Agent 能力，而不是简单包一层兼容壳。

当前仓库处于 **CLI-first 的移植收口阶段**：核心 `Tau.Ai` 和 `Tau.Agent` 已有基础实现，`Tau.CodingAgent` 已恢复最小可运行 CLI、独立 `csproj` build / run 与基础测试；`Tau.Tui`、`Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 仍在分阶段补齐。

## 当前状态

- 已有：
  - `Tau.Ai`：消息抽象、流事件、EventStream、基础 provider 注册
  - `Tau.Ai`：已补一轮 provider / model registry / OAuth / env auth 基线，覆盖 OpenAI、Anthropic、Google、Azure OpenAI Responses、OpenAI Codex、GitHub Copilot、Mistral、Vertex、Gemini CLI、Bedrock 等主干 provider
  - `Tau.Agent`：双层循环 runtime、工具执行、状态与事件骨架
  - `Tau.CodingAgent`：最小可运行 CLI、基础文件/命令工具，以及和 `ModelCatalog` 对齐的默认 provider / model 接线
- 未完成：
  - 完整 TUI 交互层
  - 完整 Coding Agent 会话/模式/配置系统
  - Web UI / Slack bot / Pods 管理 CLI 的真实产品实现
  - solution build 收口与更高层行为回归测试

更细的状态请看：

- `docs/ARCHITECTURE.md`
- `docs/product-specs/tau-port-overview.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`

## 仓库结构

```text
src/
├── Tau.Ai/
├── Tau.Agent/
├── Tau.CodingAgent/
├── Tau.Tui/
├── Tau.WebUi/
├── Tau.Mom/
└── Tau.Pods/

tests/
├── Tau.Ai.Tests/
├── Tau.Agent.Tests/
├── Tau.CodingAgent.Tests/
├── Tau.Tui.Tests/
└── Tau.Pods.Tests/
```

## 本地开发

首选验证链：

```bash
bash scripts/verify-dotnet.sh
```

如果本机依赖已经 restore 过，当前最稳的本地验证方式是：

```bash
bash scripts/verify-dotnet.sh --skip-restore
```

当前已确认的运行 / 验证现实是：

- `Tau.CodingAgent.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.CodingAgent` 已可 `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`
- 主验证链已经改成 `scripts/verify-dotnet.sh`，按显式项目顺序 restore / build / test
- 当前仍有已知问题：`dotnet build Tau.slnx --no-restore` 在这台机器上仍会因为 solution metaproj / workload resolver 链路异常而失败，并出现“0 error / 0 warning 但构建失败”的现象

最小启动验证：

```bash
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
```

## 当前模型与认证入口

- 默认 provider / model 由：
  - `TAU_PROVIDER`
  - `TAU_MODEL`
- provider 凭证解析顺序：
  - 显式 `options.ApiKey`
  - 环境变量
  - `.tau/auth.json`
  - `~/.tau/auth.json`
- 当前已支持的主要认证形态：
  - API key
  - OAuth 凭证读取与 refresh 骨架
  - Vertex / Bedrock 的 authenticated marker 检测

> `auth.json` 目前支持两类条目：
>
> - `{"type":"api_key","key":"..."}`
> - `{"type":"oauth","refresh":"...","access":"...","expiresAt":"..."}`

## 协作入口

每轮开始先读：

- `AGENTS.md`
- `docs/REPO_COLLAB_GUIDE.md`
- `docs/ARCHITECTURE.md`
- `docs/design-docs/core-beliefs.md`

复杂任务默认落 execution plan：

- `docs/PLANS_GUIDE.md`
- `docs/exec-plans/active/`

完成实质性代码变更后再补：

- `docs/HISTORY_GUIDE.md`
- `docs/histories/`

## 当前实施顺序

当前按以下顺序推进：

1. 先收口 `Tau.CodingAgent` 的第一条用户路径。
2. 为这条路径继续补齐 `Tau.Tui`、会话、配置和 auth 管理层。
3. 维持项目级 build/test/运行闭环，并继续定位 `Tau.slnx` 的 solution-level 构建问题。
4. 最后再进入 `Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 等扩展面。
