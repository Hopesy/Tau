## [2026-05-21 12:00] | Task: CodingAgent RPC session switch / fork messages baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` RPC parity，完成一个任务后继续指定下一步移植计划。

### Changes Overview

**Scope:** `Tau.CodingAgent` RPC mode、JSONL tree session utility、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **RPC session switching**: `CodingAgentRpcHost` 新增 `switch_session`，在没有 active prompt 时恢复指定 JSONL tree session，并把 current branch snapshot 同步到 runner。
* **Flat snapshot compatibility**: session 切换后复用现有 `PersistSession()` seam，把恢复后的 runner 状态写回 flat JSON session，保持旧 session store 兼容。
* **Fork selector data**: 新增 `get_fork_messages`，让 headless client 可以取得可 fork 的 user message `{ entryId, text }` 列表。
* **Tree extraction seam**: `CodingAgentTreeSessionStore` 新增 `CodingAgentForkMessage` 与 `GetUserMessagesForForking()`，遍历 JSONL entries 中的 user messages，并只拼接 text content。
* **Tests/docs**: 补 RPC host targeted coverage，并同步 README、architecture、quality score、next、release notes 和两份 active execution plans 的当前 RPC parity 状态。

### Design Intent (Why)

上游 RPC 的 `switch_session` / `get_fork_messages` 都是低耦合 session utility，不需要 bash 执行、安全配置编辑或 extension UI 子协议。Tau 已有 JSONL tree session `Resume()`、current branch flat snapshot 和 runner restore seam，因此本切片选择复用现有 session authority，而不是在 RPC host 中新增第二套 session reader。

`get_fork_messages` 对齐上游 `AgentSession.getUserMessagesForForking()` 的关键语义：遍历 session entries，而不是只看 current branch；只返回 user message；array content 只拼接 text block，图片、tool result 等非文本内容不参与 fork selector label。

本切片不宣称完整 settings RPC、bash/abort_bash、extension UI sub-protocol、queue modes 或 full command provenance 已完成。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，233/233。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 233、`Tau.Pods.Tests` 32。
* `git diff --check`：通过，退出码 0；仅出现既有 CRLF normalization warnings。
