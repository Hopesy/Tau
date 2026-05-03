# CodingAgent 最小 session reset

## 用户诉求

继续推进 Tau 对 pi-mono 的 CodingAgent 移植下一块。

## 主要变更

- 为 `ICodingAgentRunner` / `RuntimeCodingAgentRunner` 新增 `ResetSession()` seam，直接清空当前 runtime conversation state。
- 在 `CodingAgentCommandRouter` 中增加 `/new`，作为最小 session lifecycle 命令。
- `/new` 命令通过现有 `CodingAgentHost` 持久化链把空快照立即写回当前 session store，同时保留当前 model/provider。
- 扩充 `Tau.CodingAgent.Tests` 到 27 个测试，覆盖 router `/new`、host `/new` 和 runner reset 行为。
- 同步更新 architecture、quality、next 和 active execution plan。

## 设计意图

Tau 当前只有单文件 session snapshot，没有上游那套多 session 索引、resume、tree、fork/clone 结构。先把 `/new` 做实，能让用户立即清空当前对话并开始新一轮任务，同时保持当前模型选择和本地持久化一致。这一块直接复用现有 `AgentRuntime.Reset()` 和 session store，比现在就引入半成品的 resume/tree 更稳。

## 关键文件

- `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
- `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`

## 验证

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet build tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore --verbosity minimal`
