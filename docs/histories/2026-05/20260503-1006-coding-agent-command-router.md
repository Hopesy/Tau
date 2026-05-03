# CodingAgent slash command router 抽离

## 用户诉求

继续推进 Tau 对 pi-mono 的 CodingAgent 移植下一块。

## 主要变更

- 新增 `CodingAgentCommandRouter`，把 `/model`、`/provider`、`/models`、`/providers`、`/auth`、`/login` 的解析与本地命令执行从 `CodingAgentHost` 抽出。
- 新增 `CodingAgentCommandResult`，用 status/error/not-command 三类结果表达命令处理结果，避免 router 直接依赖 TUI。
- `CodingAgentHost` 收窄为输入循环、命令结果渲染、runner event 渲染和 session 持久化。
- 新增 `CodingAgentCommandRouterTests`，固定非 slash 输入、provider 列表、未知命令和模型切换持久化行为。
- 同步更新 active execution plan、architecture、quality score 与 next。

## 设计意图

`CodingAgentHost` 已经承载 session/settings/auth 三块能力，继续把后续 `/compact`、真实 login flow 或更多 slash command 直接堆进 host 会让主循环难以评审和测试。本轮先抽一个不依赖 UI 的 command seam，保持现有输出行为不变，同时让后续命令可以在 router 层单独测试。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandResult.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`

## 验证

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore --verbosity minimal`
