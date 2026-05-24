## [2026-05-24 01:02] | Task: Multi-surface runtime baselines

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续按 Tau 的 pi-mono 完整移植计划推进，并收口当前多模块 baseline。

### Changes Overview

**Scope:** `Tau.Pods`, `Tau.Mom`, `Tau.WebUi`, `Tau.Tui`, `Tau.Ai`, `Tau.CodingAgent`, tests, repo docs

**Key Actions:**

* **Tau.Pods vLLM orchestration baseline**: 新增 `vllm deploy/status/health/stop [--json]` CLI 路径和 `PodVllmOrchestrationService`，把现有 vLLM serve plan 接到 SSH command/service baseline。`deploy` 会写远端 service/metadata，优先 systemd user unit，失败或不可用时 fallback 到 `nohup` pid/log；默认按 12 次、5 秒 backoff 做远端 `/health` readiness 窗口，可用 `--no-health` 跳过，也可用 `--health-attempts` / `--health-backoff-ms` 调整；`status` 拉取 systemd/fallback pid/metadata 文本；`health` 返回 ready/unhealthy/dead/starting/unknown、failure kind 和 attempts；`stop` 做 idempotent cleanup。
* **Tau.Mom runtime log baseline**: `Program` 注入 `ITauLogSink`，`RuntimeDelegationAgentRunner` 写 `delegation.start`、`response.start`、`tool.start`、`tool.end`、`response.end`、`usage`、`delegation.end`，并在 cancellation 路径写 `delegation.end stopReason=cancelled` 后 rethrow。
* **Tau.Mom Slack runtime response baseline**: `MomChannelMessageProcessor` 支持可选 runtime responder seam，先发 `_Thinking_ ...` 占位，完成后更新同一条响应；`SlackWebApiResponder` 固定 `chat.update` / `chat.delete` 契约，`[SILENT]` 响应会删除占位且不再追加普通回复。
* **Tau.WebUi CodingAgent JSONL preview tree metadata/filter/audit**: `CodingAgentJsonlSessionPreviewer` 返回只读 tree metadata，包含 leaf/root/branch/message/label counts、entry type histogram、current branch ids 和 per-entry depth/child/current-branch/current-leaf/label metadata；entry id、parent id、label target 和 self-parent resolution 按 case-insensitive tree-store 语义处理；preview endpoint 支持 `search` / `currentBranchOnly` 只过滤返回的 `Messages`，并返回 filter audit；conservative import 响应带回 source tree/source audit/warnings。
* **Tau.Tui viewport host/session baseline**: 新增 `TuiTranscriptViewportHost` 和 `TuiTranscriptSession`，把 `TuiTranscriptViewport`、`ITuiRenderSurface` 和 `TuiDiffRenderer` 包成 runtime render/apply wrapper，再提供 start/stop、auto-render mutation、PageUp/PageDown/Home/End 滚动键输入和可注入 key reader seam，其中 Home 固定 scroll-top；固定 resize、force/reset、diff apply 和 apply failure 不推进 previous frame 的语义。
* **Tau.Ai Bedrock SSO refresh baseline**: `BedrockSsoResolver` 现在会在 AWS CLI SSO cache access token 过期、且 cache 中存在 `clientId` / `clientSecret` / `refreshToken` / 未过期 `registrationExpiresAt` 时，通过 AWS SSO OIDC `CreateToken` 刷新 access token，随后继续调用 SSO Portal，并 best-effort 写回 cache。
* **Tau.CodingAgent HTML nested list baseline**: `CodingAgentHtmlSessionExporter` 的轻量 Markdown list renderer 改为跟踪 open list item，缩进升级时把子 `<ul>/<ol>` 保留在父 `<li>` 内，避免嵌套列表在 HTML transcript 中变成父项外的兄弟列表。
* **Review fixes**: 收回 Pods Models/Services 依赖方向，确保 model/result 类型不引用 service 层；补 Mom production sink DI 和 cancel end event；补 WebUi case-insensitive self-parent root 判定；补 Tui apply failure previous frame 回归。
* **Smoke isolation**: `scripts/verify-dotnet.ps1 -RunSmoke` 的 Mom smoke 显式把 `TAU_LOG_FILE` 指向 smoke 临时目录，并断言 Tau runtime log 包含 `delegation.start/end`，避免验证脚本在 `src/Tau.Mom/.tau/` 留下运行日志。
* **Docs sync**: README、ARCHITECTURE、QUALITY_SCORE、active plan、next 和 release notes 同步本轮六个 baseline、测试计数和剩余边界。

### Design Intent (Why)

本轮选择六个可本地验证、互不重叠的应用面 baseline，而不是把还依赖真实 Slack/Docker/vLLM/GPU/HF/terminal/AWS 云端环境的能力写成 full parity。

`Tau.Pods` 先固定 SSH command/service orchestration 合同，让后续真实 vLLM smoke 可以检查同一套 command surface；`deploy` 成功现在代表远端 shell command 和配置的 health readiness 窗口通过（除非显式 `--no-health`），但不代表真实 GPU/HF/vLLM 环境长期健康、模型 snapshot 已解析、真实 systemd/nohup 路径已经在 GPU pod 上 smoke，或具备多版本 rollout rollback。

`Tau.Mom` 先把已有 `DelegationExecution`、tool events 和 usage 摘要写入统一 Tau runtime log sink，提升本地委派可审计性；当前仍不是跨模块 trace/correlation 协议，也不覆盖 auth status、统一 tool execution trace、Pod operation 或真实 Slack session sync。

`Tau.WebUi` 先暴露 CodingAgent JSONL tree 的只读 preview metadata，避免导入前把 branch session 误当线性 transcript；conservative import 仍只消费 timeline-derived `preview.Messages` 并生成 WebChat DTO，不替换 `WebChatStore`，不持久化 branch/tree 结构。

`Tau.Tui` 先补 viewport/render surface/diff apply wrapper，给后续主屏接线一个可测试 host；它不是完整 terminal lifecycle、overlay compositor、hardware cursor 或 CodingAgent 主屏。

`Tau.Ai` 继续保持无 AWS SDK 边界，只消费 AWS CLI cache 已有的 client registration / refresh metadata，并用最小 OIDC `CreateToken` HTTP seam 修复 access token 过期后的本地可恢复路径。当前不做 `RegisterClient`、client registration renewal、cache 并发保护或真实 AWS 云端 e2e。

`Tau.CodingAgent` 继续保留 Tau-native 轻量 Markdown subset，只修正 list item stack 的 HTML 语义。这样可以低风险改善 `/export` / `/share` transcript 结构，同时不把完整 Markdown parser、syntax highlight、custom renderer 或 richer template 伪装成已完成。

### Validation

* `dotnet build-server shutdown` completed before serial tests.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed: 89/89.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` passed: 144/144.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` passed: 81/81.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed after the filter/audit regressions: 39/39.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed: 211/211.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed: 347/347.
* `git diff --check` passed on the final working tree; Git only reported CRLF normalization warnings.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed on the final working tree: `Tau.Ai.Tests` 211/211, `Tau.Agent.Tests` 81/81, `Tau.Tui.Tests` 144/144, `Tau.CodingAgent.Tests` 347/347, `Tau.WebUi.Tests` 39/39, `Tau.Pods.Tests` 89/89.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed on the final working tree, including WebUi smoke and Mom `--once` smoke; Mom processed one due event and the smoke log contained `delegation.start` / `delegation.end`.

### Files Modified

* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `next.md`
* `scripts/verify-dotnet.ps1`
* `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
* `src/Tau.Ai/Providers/Bedrock/BedrockSsoResolver.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Tui/Runtime/TuiScrollbackBuffer.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptViewport.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptSession.cs`
* `src/Tau.Tui/Runtime/TuiTranscriptViewportHost.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.Ai.Tests/BedrockSsoResolverTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Tui.Tests/TuiScrollbackBufferTests.cs`
* `tests/Tau.Tui.Tests/TuiTranscriptSessionTests.cs`
* `tests/Tau.Tui.Tests/TuiTranscriptViewportHostTests.cs`
* `tests/Tau.WebUi.Tests/CodingAgentJsonlSessionPreviewerTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
