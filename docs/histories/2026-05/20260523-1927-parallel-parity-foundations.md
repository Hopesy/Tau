## [2026-05-23 19:27] | Task: advance parallel parity foundations

### 🤖 Execution Context

* **Agent ID**: main Codex + parallel workers
* **Base Model**: current model not exposed by runtime
* **Runtime**: Windows PowerShell, .NET 10, Tau repo

### 📥 User Query

> 用户要求继续使用 6-agent 并行推进 pi-mono 到 Tau 的完整移植，并由主控统一集成、验证和提交当前轮次成果。

### 🛠 Changes Overview

**Scope:** `Tau.Ai`, `Tau.CodingAgent`, `Tau.Tui`, `Tau.WebUi`, `Tau.Mom`, `Tau.Pods`, docs

**Key Actions:**

* **[Ai]**: 新增 `JsonlSecretRedactor`，为 JSONL 行级 string value redaction 提供通用 foundation；invalid JSON fallback 到整行 redaction，disabled redactor 原样返回。
* **[CodingAgent]**: 新增 append-only `tree_state` metadata entry 和 `CodingAgentTreeFoldState`，让 `/tree --interactive` 的 collapsed entry ids 写入当前 JSONL session；settings `treeCollapsedEntryIds` 保留为兼容 fallback。
* **[Tui]**: 新增 `TuiTranscriptViewport`，组合 `TuiMessageArea`、`TuiStatusBar` 和 `TuiScrollbackBuffer`，固定高度 transcript viewport 的 set/append/clear、滚动、resize rewrap 和 bottom-follow 行为。
* **[WebUi]**: 新增 `POST /api/sessions/import.coding-agent-jsonl` 和 `WebChatService.ImportCodingAgentJsonlSession`，把 CodingAgent JSONL timeline 保守导入为 WebChat DTO；tool call/result/thinking/image 使用可审计文本标记，不保留完整 branch/tree 语义。
* **[Mom]**: 新增 `MomSecretRedaction`，复用 `JsonlSecretRedactor` 和 `TauSecretRedactor`，让 `log.jsonl` 写入、history 注入 prompt 和 `last_prompt.jsonl` 写入默认脱敏；`TAU_MOM_REDACT_SECRETS=0` 可关闭。
* **[Pods]**: 将 `PodVllmCommandPlanner` 接入 `vllm plan [path] <id> <model> [name]` CLI 预览入口，输出 serve command、systemd unit、metadata JSON 和 remote command；不执行 SSH，不调用 `systemctl`，不写远端状态。
* **[Docs]**: 同步 README、next、架构、质量评分、release notes 和完整移植 active plan，记录本轮边界和测试计数。

### 🧠 Design Intent (Why)

本轮仍按互斥模块切片推进，主控集中做文档、history、验证和提交，避免并行 worker 同时改 docs 或并行 build/test 触发 Windows/Roslyn 文件锁。实现上坚持 foundation-first：先固定可测试的本地合同，不宣称 full parity、真实 Slack/Docker/vLLM/SSH/HF/e2e 或 CodingAgent branch/tree WebUi 持久化语义已经完成。

### 📁 Files Modified

* `README.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `src/Tau.Ai/Security/JsonlSecretRedactor.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.Mom/ChannelLogStore.cs`
* `src/Tau.Mom/ChannelPromptDebugStore.cs`
* `src/Tau.Mom/MomSecretRedaction.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptViewport.cs`
* `tests/Tau.Ai.Tests/JsonlSecretRedactorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeFoldStateTests.cs`
* `tests/Tau.Tui.Tests/TuiTranscriptViewportTests.cs`
* `tests/Tau.Agent.Tests/EnvironmentVariableScope.cs`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`

### ✅ Verification

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` — 204 passed
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` — 342 passed
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` — 110 passed
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` — 76 passed
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` — 65 passed
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` — 29 passed
