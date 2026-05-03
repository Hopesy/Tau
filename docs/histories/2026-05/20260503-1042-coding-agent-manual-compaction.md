# CodingAgent 最小手动 compaction

## 用户诉求

继续推进 Tau 对 pi-mono 的 CodingAgent 移植下一块。

## 主要变更

- 为 `ICodingAgentRunner` / `RuntimeCodingAgentRunner` 新增 `CompactAsync(...)` seam 和 `CodingAgentCompactionResult`。
- 在 `CodingAgentCommandRouter` 增加异步命令处理和 `/compact [instructions]`，支持把额外压缩指令透传给 runner。
- 在 `RuntimeCodingAgentRunner` 中实现最小手动 compaction：使用当前模型生成会话摘要，随后 `AgentRuntime.Reset()`，并把摘要保留为单条 user summary message。
- 更新 `CodingAgentHost` 以支持异步 slash command，同时保持命令结果渲染和 session 持久化职责不变。
- 扩充 `Tau.CodingAgent.Tests` 到 24 个测试，覆盖 router `/compact`、host `/compact` 和 runner compact 行为。
- 同步更新 architecture、quality、next 和 active execution plan。

## 设计意图

Tau 当前没有上游那套 JSONL session tree、branch summary、compaction entry 和 auto-compaction 基建，但已经具备稳定的消息持久化和 `AgentRuntime.Reset()`。因此这一块先提供一个真实可用的最小 `/compact`：让模型生成可继续工作的摘要，再把 session 压成单条 summary message。这样可以先把命令面和 runtime seam 建起来，后续再继续向 auto-compaction、branch/tree 和更完整 metadata 收口。

## 关键文件

- `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandResult.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCompactionResult.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
- `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`

## 验证

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore --verbosity minimal`
