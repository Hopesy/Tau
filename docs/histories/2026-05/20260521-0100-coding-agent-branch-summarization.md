# 2026-05-21 01:00 | Task: CodingAgent Branch Summarization baseline

## Execution Context

- Agent ID: Codex
- Runtime: Windows PowerShell
- Scope: `Tau.CodingAgent` JSONL session tree / branch switching / HTML export

## User Query

> 继续按 Tau 的 pi-mono 移植计划推进，不要停在状态报告；遵循 AGENTS.md / harness-init 的 plan 与 history 约定。

## Changes Overview

### Scope

- `src/Tau.CodingAgent/Runtime`
- `tests/Tau.CodingAgent.Tests`
- `tests/Tau.Agent.Tests`
- `tests/Tau.WebUi.Tests`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
- `docs/exec-plans/active/*`

### Key Actions

- 新增 `CodingAgentBranchSummaryResult` 和 `ICodingAgentRunner.SummarizeBranchAsync(...)`，让 branch summary 成为 runner 的显式能力。
- 在 `RuntimeCodingAgentRunner` 中复用 `StreamFunctions.CompleteSimpleAsync(...)` 生成 branch summary，并把 file operation tracker 的 read / modified files 写入结果。
- 扩展 `CodingAgentTreeSessionStore`，新增 JSONL `branch_summary` entry，写入 `fromId`、`readFiles`、`modifiedFiles`，并在 branch restore 时把 summary 转成 runtime context message。
- 将 `/fork <entry-id>` 扩展为 `/fork <entry-id> --summarize [instructions]` / `--summary` / `-s`，普通 `/fork` 行为保持不变。
- 扩展 HTML transcript exporter，让 timeline 和 branch outline 渲染 `branch_summary`，并显示 summary、read files、modified files。
- 增加 targeted test 覆盖 `/fork --summarize`、JSONL entry、restore context、`/tree --search` 与 HTML export。
- 同步 README、ARCHITECTURE、QUALITY_SCORE、`next.md` 和两份 active plan，把 Branch Summarization baseline 与剩余完整 parity 边界写清。

## Design Intent

- 先把 Branch Summarization 接到显式 `/fork --summarize`，避免普通 branch navigation 自动触发 LLM 调用造成不可预期的 token 成本和延迟。
- JSONL `branch_summary` entry 挂到目标 entry 下，并在恢复目标 branch 时注入 summary context，保留“离开的 branch 已做什么”的语义。
- 保持当前 Tau-native tree/session/export 结构，不提前声称完整上游 TreeSelector、自动 branch switching hooks、extension events 或 cancellation UI 已完成。

## Files Modified

- `src/Tau.CodingAgent/Runtime/CodingAgentCompactionResult.cs`
- `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCompactionMessages.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
- `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
- `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

## Validation

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`
- `dotnet build tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`
- `git diff --check`
