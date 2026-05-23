## [2026-05-23 18:23] | Task: parallel six-agent next slices

### Execution Context

* **Agent ID**: `Codex main controller + subagent workers`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一步继续并行

### Changes Overview

**Scope:** `Tau.Ai` / `Tau.CodingAgent` / `Tau.Tui` / `Tau.WebUi` / `Tau.Mom` / `Tau.Pods` / tests / docs

**Key Actions:**

* **[Parallel strategy]**: 从 `bcba121` 继续按六个互斥模块切片推进。实际执行中 Ai、CodingAgent、Tui、Pods worker 被模型网关 `403 Forbidden` 阻断，WebUi/Mom worker 完成；主控本地补齐剩余四个切片，并统一做 docs/history/验证/提交。
* **[Tau.Ai]**: `JsonlTauLogSink` 默认使用 `TauSecretRedactor` 对 runtime JSONL log 的 category、event 和 field value 做 pattern-based 脱敏，新增 `TAU_LOG_REDACT_SECRETS=0` opt-out；测试覆盖默认 redaction 与显式关闭，并改用仓库 env guard 固定测试环境。
* **[Tau.CodingAgent]**: settings snapshot/document 新增 `TreeCollapsedEntryIds`；`/tree --interactive` navigator 支持初始 folded ids 和 fold-state changed callback，Space、Ctrl/Alt+Left、Ctrl/Alt+Right 与 filter/search reset 会同步折叠状态。
* **[Tau.Tui]**: 新增 `TuiScrollbackBuffer` 库层 foundation，覆盖 append/replace/clear、max-lines trim、height resize、line/page scroll、bottom-follow 和 visible lines；`Lines` 只暴露只读视图，避免外部绕过 trim/scroll invariant。
* **[Tau.WebUi]**: 新增 `CodingAgentJsonlSessionPreviewer` 和 `POST /api/sessions/import.coding-agent-jsonl/preview`，只读预览 CodingAgent JSONL session header 与 message timeline summary；malformed JSONL 返回 `application/problem+json`。
* **[Tau.Mom]**: 新增 `MomModelSelectionResolver` 与 `ChannelSessionStore.LoadMetadata()`，让同一 workdir/channel 的后续本地委派在 request 未显式指定 provider/model 时继承 `context.json` 中已有 provider/model，并继续归一 `google` provider；显式 model-only request 不再被默认模型吞掉。
* **[Tau.Pods]**: 新增 `PodVllmCommandPlanner`，生成 vLLM serve 的 systemd unit、metadata JSON 和 remote shell command plan，固定 quoting、env、modelsPath 和 extra args；env key 规范化会保证生成合法 shell assignment，remote command 仍保持 plan-only。
* **[Docs/history/plan]**: 同步 README、next、architecture、quality score、release notes 和 active execution plan，明确本轮是 preview/planner/seam/foundation，不是 full parity。

### Design Intent (Why)

本轮继续用并行切片拿吞吐，但收口仍由主控串行完成。原因是六个模块的代码写入范围基本可隔离，但文档、history、release notes 和全仓验证是共享面，必须统一收口，避免不同 agent 对“完成度”写出互相矛盾的表述。

边界保持写实：

* WebUi 的 CodingAgent JSONL 能力只是只读 preview，不导入、不持久化、不替换 WebChatStore。
* Mom 的 carry-over 是本地 workdir/channel seam，不是真实 Slack session sync。
* Pods 的 vLLM 能力只是命令 planner，不执行 SSH，也不是真实 orchestration。
* Tui scrollback buffer 未接 terminal host、overlay 或硬件 cursor。
* CodingAgent fold persistence 不是完整 TreeSelector、多选或 TUI metadata inspector。
* Ai runtime log redaction 不等于所有 JSONL export/runtime 日志边界已闭合。

### Files Modified

* `src/Tau.Ai/Observability/JsonlTauLogSink.cs`
* `src/Tau.Ai/Security/TauSecretRedactor.cs`
* `tests/Tau.Ai.Tests/JsonlTauLogSinkTests.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs`
* `src/Tau.Tui/Runtime/TuiScrollbackBuffer.cs`
* `tests/Tau.Tui.Tests/TuiScrollbackBufferTests.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/CodingAgentJsonlSessionPreviewerTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `src/Tau.Mom/ChannelSessionStore.cs`
* `src/Tau.Mom/FileDelegationProcessor.cs`
* `src/Tau.Mom/MomChannelMessageProcessor.cs`
* `src/Tau.Mom/MomModelSelectionResolver.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `tests/Tau.Agent.Tests/MomChannelMessageProcessorTests.cs`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `README.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/SECURITY.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `docs/histories/2026-05/20260523-1823-parallel-six-agent-next-slices.md`

### Verification

* `git diff --check` passed; only CRLF normalization warnings were reported for docs and existing touched source files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed; final output: `Tau .NET project-level validation passed`.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed: 198/198.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` passed: 103/103.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed: 60/60.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed: 338/338.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed: 25/25.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` passed: 72/72.
