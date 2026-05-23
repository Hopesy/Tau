## [2026-05-23 17:38] | Task: parallel six-agent parity slices

### Execution Context

* **Agent ID**: `Codex main controller + Ai worker 019e541a-4f6a-7841-b966-0508ef1e94ea + CodingAgent worker 019e541a-503a-7363-a355-a88c3699a010 + Tui worker 019e541a-514b-7b90-8d54-55262ab7eeea + WebUi worker 019e541a-5272-7c63-9d60-5f44f23dd68e + Mom worker 019e541a-5389-7c80-a2a3-39ac41a3accc + Pods worker 019e541a-549c-7f22-bf96-b80a3cbf902d`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 直接6个agent吧。

### Changes Overview

**Scope:** `Tau.Ai` / `Tau.CodingAgent` / `Tau.Tui` / `Tau.WebUi` / `Tau.Mom` / `Tau.Pods` / tests / docs

**Key Actions:**

* **[Parallel strategy]**: 从干净基线 `f80153d` 开启 6 个互斥 worker，分别处理 `Tau.Ai`、`Tau.CodingAgent`、`Tau.Tui`、`Tau.WebUi`、`Tau.Mom` 和 `Tau.Pods`。worker 只写各自模块和测试；README/docs/history/release notes/plan 由主控统一收口。
* **[Tau.Ai]**: `models.json` 未预注册 provider 现在可在显式 OpenAI-compatible marker 下运行时注册到 `ProviderRegistry`；只接受 `openai-compatible`、`openai-completions`、`openai-chat-completions` 三个 marker，未知或拼错 API 不静默兜底。`EnvironmentVariableScope` 测试 helper 改用 `SemaphoreSlim` 避免 async test 跨线程 dispose 锁异常。
* **[Tau.CodingAgent]**: 新增 `/metadata [entry-id]` 命令和 help/catalog 条目；无参数检查 JSONL session header、cwd/parent/leaf、entry/message/branch/label 统计和最近 metadata entries，指定 entry 时输出 id/type/parent/timestamp/path/depth/children/label、message preview、model/session/label/compaction/branch-summary/retry 等关键字段。
* **[Tau.Tui]**: 新增 `TuiMessageArea` 和 `TuiStatusBar` 库层 foundation，覆盖 role prefix、wrap、continuation indent、bottom-anchored visible transcript、left/right status segment、右侧状态保留和窄宽截断。
* **[Tau.WebUi]**: JSONL import 增加稳定 error contract：unsupported content type 返回 `415 application/problem+json`，parser error 返回 `400 application/problem+json` 并包含 `code`、可选 `line` 和 detail；前端 import input 支持 `.jsonl` 并按扩展名选择 JSON / JSONL endpoint。
* **[Tau.Mom]**: 新增 `MomLocalDelegationFlow`，让 `Worker` 后台轮询和 `Program --once` 共用 `events -> inbox -> runner -> outbox/status/log/archive` 本地流程；fake runner e2e 覆盖 due event、provider/model normalize、attachment staging、outbox/status/log/archive 和 inbox cleanup。
* **[Tau.Pods]**: 新增 model lifecycle baseline：`model list/pull/remove/status`、`PodDefinition.ModelsPath`、`PodModelService`、HF cache parse、`huggingface-cli` pull、python fallback、model status present/missing 和长耗时 SSH keepalive。
* **[Docs/history/plan]**: 同步 `README.md`、`next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、release notes 和 active execution plan，明确本轮仍不是真实云端 e2e、完整 Tui host、完整 WebUi/CodingAgent session-tree parity、真实 Slack/Docker smoke 或真实 SSH/HF/vLLM orchestration。

### Design Intent (Why)

这轮扩大到 6 个 worker 的前提是写入范围必须互斥，且所有集成验证由主控串行执行。这样可以利用并行收益，又避免多个 agent 同时写共享 docs、脚本或同一测试项目造成语义漂移。

边界保持明确：

* Ai 只允许显式 OpenAI-compatible dynamic registration，不把任意未知 `api` 当成可调用 provider。
* CodingAgent 的 `/metadata` 是 CLI inspector baseline，不是完整 TUI metadata inspector。
* Tui 是 message/status 库层 foundation，不是完整 terminal host。
* WebUi 是 WebUi-local JSONL import/error contract，不是 CodingAgent branch/session-tree parity。
* Mom 是本地 flow seam，不是真实 Slack smoke 或 Docker container smoke。
* Pods 是 SSH/HF cache model lifecycle baseline，不是真实 HF download、token handling或 vLLM runner orchestration。

### Files Modified

* `src/Tau.Ai/Providers/BuiltInProviders.cs`
* `src/Tau.Ai/Registry/ModelConfigurationStore.cs`
* `tests/Tau.Ai.Tests/EnvironmentVariableScope.cs`
* `tests/Tau.Ai.Tests/ModelConfigurationStoreTests.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `src/Tau.Tui/Components/TuiMessageArea.cs`
* `src/Tau.Tui/Components/TuiStatusBar.cs`
* `tests/Tau.Tui.Tests/TuiMessageStatusComponentTests.cs`
* `src/Tau.WebUi/Services/WebChatJsonlImporter.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/WebChatJsonlExporterTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `src/Tau.Mom/MomLocalDelegationFlow.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/Worker.cs`
* `tests/Tau.Agent.Tests/MomLocalDelegationFlowTests.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodDefinition.cs`
* `src/Tau.Pods/Models/PodModelResults.cs`
* `src/Tau.Pods/Services/PodExecService.cs`
* `src/Tau.Pods/Services/PodModelService.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodModelServiceTests.cs`
* `README.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `docs/histories/2026-05/20260523-1738-parallel-six-agent-parity-slices.md`

### Verification

* `git diff --check` passed; only CRLF normalization warnings were reported for `src/Tau.Mom/Worker.cs`, `src/Tau.Pods/Services/PodsConfigStore.cs` and `src/Tau.WebUi/Ui/WebUiPage.cs`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed; final output: `Tau .NET project-level validation passed`.
* Full verify test counts: `Tau.Ai.Tests` 196/196, `Tau.Agent.Tests` 68/68, `Tau.Tui.Tests` 92/92, `Tau.CodingAgent.Tests` 336/336, `Tau.Pods.Tests` 55/55.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed: 17/17 tests.
* `dotnet run --project src\Tau.Mom\Tau.Mom.csproj --no-build -- --validate-sandbox --Mom:Sandbox host` passed with exit code 0 and logged `Mom sandbox 'host' is valid. No Docker checks were required.`
