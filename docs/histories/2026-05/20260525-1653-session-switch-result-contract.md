# Session switch result contract

## 用户诉求

继续快速移植 Tau.CodingAgent，对齐 pi-mono 的 session switch / `session_before_switch` 语义；少做低收益文档和单测，多 Agent 并行确认上游 contract 与当前覆盖后直接推进。

## 主要变更

- 将 `CodingAgentSessionSwitchSummaryResult` 从 `Cancelled + Summary` 扩展为统一的 session switch audited outcome，携带：
  - `Reason`
  - `PreviousSessionPath`
  - `TargetSessionPath`
  - `UsedHook`
  - `UsedPrompt`
  - `Decision`
  - `Summary`
  - `SummaryEntryCount`
  - `TokensBeforeSummary`
- `CodingAgentSessionSwitchCoordinator` 现在统一生成上述 result，CLI `/new`、`/resume`、`.jsonl /import` 和 RPC `new_session` / `switch_session` 都消费同一个 result。
- `CodingAgentRpcHost` 新增统一 mapper 生成 session switch RPC data，继续保留上游兼容主合同 `cancelled`，同时保留现有 Tau 扩展字段 `summarizedCurrentBranch`、`branchSummary` 和 `alreadyCurrent`。
- 补充 RPC focused tests：
  - hook decision 在没有 `summarizeCurrentBranch` command flag 时仍可触发 switch summary。
  - already-current `switch_session` 分支保持 `cancelled=false`、`summarizedCurrentBranch=false`、`alreadyCurrent=true`。
- 修正 metadata snapshot 现有测试断言：当前 metadata inspector 会把可 drill-down 的关系 entry 纳入 `VisibleEntryIds`，测试改为验证 focus entry 存在且所有 visible id 都可解析。

## 设计意图

上游 `new_session` / `switch_session` RPC 公开合同很瘦，稳定核心是 `cancelled`。Tau 当前已经有 `summarizedCurrentBranch` / `branchSummary` 扩展字段，本轮保留这些字段以避免破坏现有 Tau client/tests，但没有继续把内部审计字段扩成公共 RPC 协议。

这轮只收口 result contract，不把 reset/resume/rebind 全部搬进 coordinator，避免在当前大量既有改动的工作树里扩大 session lifecycle 改动面。后续如果补 `session_start` / extension event，可以直接复用 result 中的 previous/target path 和 decision metadata。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentSessionSwitchCoordinator.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
- `next.md`

## 验证

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "SwitchSession|NewSession"`：10/10 通过
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter "GetMetadataSnapshot_FocusedEntryIncludesRelationsAndSections"`：1/1 通过
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：397/397 通过
- `dotnet build Tau.slnx --no-restore --verbosity minimal`：0 warning / 0 error
- `git diff --check`：通过，仅有既有 CRLF normalization warnings
