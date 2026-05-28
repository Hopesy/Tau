# pi-mono -> Tau parity matrix

Status values:

- `verified`: Tau implementation has direct behavior evidence from tests/smoke/e2e and docs are current.
- `ported`: Tau implementation exists, but evidence is not yet strong enough for final 100% acceptance.
- `partial`: Tau covers part of the upstream behavior, with known behavior gaps.
- `missing`: No meaningful Tau equivalent yet.
- `external-e2e-needed`: Contract/fake coverage may exist, but real provider/service/runtime validation is still required.
- `non-goal-proposed`: Candidate non-goal; only valid after explicit user confirmation.

This matrix is the Phase 1 inventory surface for `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`. It is intentionally evidence-first: every status must point to upstream source, Tau target, and validation evidence or remaining proof needed.

## Inventory Progress

| Area | Upstream scope | Tau scope | Status | Notes |
| --- | --- | --- | --- | --- |
| AI + Agent | `packages/ai`, `packages/agent` | `src/Tau.Ai`, `src/Tau.Agent`, tests | partial | Capability inventory merged from local scan. Known final blockers include provider e2e, auth refresh/registration, broader config UX, Agent facade/event/correlation closure. |
| CodingAgent + Tui | `packages/coding-agent`, `packages/tui` | `src/Tau.CodingAgent`, `src/Tau.Tui`, tests | partial | Capability inventory merged from local scan after explorer failure. Known final blockers include full TreeSelector, terminal host, TypeScript extension runtime, OAuth/settings UI parity and richer rendering. |
| WebUi | `packages/web-ui` | `src/Tau.WebUi`, tests | partial | Capability inventory merged from local scan after explorer failure. Known final blockers include reusable component package parity, CodingAgent branch/tree true persistence, artifact renderers, dialogs, sandbox runtime providers, browser/release packaging parity. |
| Mom | `packages/mom` | `src/Tau.Mom`, Mom-related `tests/Tau.Agent.Tests` | partial | Capability inventory merged from Mom explorer. Known final blockers include real Slack smoke, Slack session sync, Docker smoke and multi-message e2e. |
| Pods + root scripts | `packages/pods`, root scripts/manifests | `src/Tau.Pods`, `tests/Tau.Pods.Tests`, `scripts/*`, CI/release | partial | Capability inventory merged from local scan after explorer failure. Known final blockers include real SSH/HF/GPU/vLLM smoke, upstream GPU allocation/model prompt parity, rollout/rollback state, transport hardening and release artifact parity. |

## Upstream Package Surface

Initial file counts from `C:\Users\zhouh\Desktop\pi-mono-main`:

| Package | Total tracked source/doc/config files counted | `src/**` files counted | Tau target |
| --- | ---: | ---: | --- |
| `packages/ai` | 109 | 44 | `src/Tau.Ai`, `tests/Tau.Ai.Tests` |
| `packages/agent` | 15 | 5 | `src/Tau.Agent`, `tests/Tau.Agent.Tests` |
| `packages/coding-agent` | 411 | 137 | `src/Tau.CodingAgent`, `tests/Tau.CodingAgent.Tests` |
| `packages/tui` | 57 | 25 | `src/Tau.Tui`, `tests/Tau.Tui.Tests` |
| `packages/web-ui` | 85 | 75 | `src/Tau.WebUi`, `tests/Tau.WebUi.Tests` |
| `packages/mom` | 29 | 16 | `src/Tau.Mom`, Mom-related `tests/Tau.Agent.Tests` |
| `packages/pods` | 22 | 10 | `src/Tau.Pods`, `tests/Tau.Pods.Tests` |

Count method:

```powershell
$root='C:\Users\zhouh\Desktop\pi-mono-main'
$pkgs='ai','agent','coding-agent','tui','web-ui','mom','pods'
foreach($p in $pkgs){
  $src=Join-Path $root "packages\$p"
  $files=rg --files $src | Where-Object { $_ -match '\.(ts|tsx|js|mjs|json|md|sh|css)$' }
  $srcFiles=$files | Where-Object { $_ -match '\\src\\' }
  "${p}: total=$($files.Count) src=$($srcFiles.Count)"
}
```

## Root Build/Test/Release Surface

| Upstream item | Upstream behavior | Tau equivalent | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| `package.json` workspaces | Builds all packages in order: tui, ai, agent, coding-agent, mom, web-ui, pods. Runs root check, browser smoke, tests, version and publish scripts. | `Tau.slnx`, `scripts/verify-dotnet.ps1`, `scripts/verify-dotnet.sh` | partial | Tau has project-level build/test/smoke, but release/publish/version parity is not complete. |
| `test.sh` | Temporarily backs up `~/.pi/agent/auth.json`, unsets provider env vars, sets `PI_NO_LOCAL_LLM=1`, runs `npm test`. | `scripts/verify-dotnet.ps1 -SkipRestore` and module tests | partial | Tau gate exists, but no exact auth backup / no-env parity mode yet. |
| `pi-test.sh` | Runs `packages/coding-agent/src/cli.ts`, supports `--no-env` to unset provider env vars before CLI execution. | `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj` | partial | Tau has CLI entry, but no equivalent wrapper script with full no-env matrix. |
| `scripts/check-browser-smoke.mjs` + `scripts/browser-smoke-entry.ts` | Browser smoke for web UI/example. | `scripts/verify-dotnet.ps1 -RunSmoke`, `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs` | partial | Tau has WebUi smoke and browser tests; release/static package browser smoke still needs closure. |
| `scripts/build-binaries.sh` | Builds distributable upstream binaries. | none complete | missing | Final 100% requires Tau real executable artifacts. |
| `scripts/release.mjs` | Release/version/publish flow. | none complete | missing | Release artifact and CI parity remain Phase 5 blockers. |
| `scripts/session-transcripts.ts` | Session transcript utility. | CodingAgent/WebUi export paths | partial | Tau has exports, but script parity/invocation surface is not inventoried as verified. |
| `scripts/profile-coding-agent-node.mjs` | TUI/RPC profiling entry. | no direct Tau script | missing | Need decide Tau profiling/smoke equivalent or non-goal. |
| `scripts/edit-tool-stats.mjs` | Edit tool statistics utility. | no direct Tau script | missing | Need inventory whether this is user-facing or release/dev-only. |
| `scripts/cost.ts` | Cost utility. | `Tau.Ai.Registry.ModelCatalog.CalculateCost` | partial | Library cost exists; script/CLI utility parity not verified. |
| `scripts/sync-versions.js` | Workspace package version sync. | no direct Tau equivalent | partial | Likely replaced by .NET packaging version flow, but needs Phase 5 decision. |

## AI + Agent Matrix

| Capability | Upstream evidence | Tau target / evidence | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| AI stream/event abstractions | `packages/ai/src/types.ts`, `stream.ts`, `utils/event-stream.ts` | `src/Tau.Ai/Abstractions/*`, `src/Tau.Ai/Streaming/*`, provider parser tests | ported | Tau has 13 stream-event records and channel-backed `EventStream`; final verification still depends on provider-family e2e. |
| Provider registry and built-ins | `api-registry.ts`, `providers/register-builtins.ts`, provider subpath exports in `packages/ai/package.json` | `src/Tau.Ai/Providers/ProviderRegistry.cs`, `BuiltInProviders.cs`, `StreamFunctions.cs`, `tests/Tau.Ai.Tests/BuiltInProvidersTests.cs` | partial | Tau has lazy registry and major built-ins, but parity must still prove every upstream exported provider/API surface and dynamic registration behavior. |
| Model catalog / generated models | `models.ts`, `models.generated.ts`, `scripts/generate-models` implied by package build | `src/Tau.Ai/Registry/ModelCatalog.cs`, `GeneratedBuiltInModels.g.cs`, `generated-models.seed.json`, `ModelCatalogTests.cs` | partial | Generated seed exists, but current docs only prove Tau-supported model families, not full upstream generated model coverage. |
| OpenAI / Responses / Codex / Azure / Copilot providers | `providers/openai-*.ts`, `azure-openai-responses.ts`, `github-copilot-headers.ts` | `src/Tau.Ai/Providers/OpenAi*`, `tests/Tau.Ai.Tests/OpenAi*`, `AzureOpenAiResponsesProviderTests.cs` | external-e2e-needed | Stub/serialization tests exist; real cloud Responses/Codex/Azure/Copilot behavior remains unverified. |
| Anthropic / Google / Vertex / Gemini CLI / Antigravity / Mistral | `providers/anthropic.ts`, `google*.ts`, `google-vertex.ts`, `mistral.ts` | `src/Tau.Ai/Providers/Anthropic`, `Google`, `Mistral`, corresponding tests | external-e2e-needed | Contract tests cover request and parser behavior; real provider e2e and auth refresh/error semantics remain final blockers. |
| Bedrock / AWS auth chain | `bedrock-provider.ts`, `providers/amazon-bedrock.ts` | `src/Tau.Ai/Providers/Bedrock/*`, `Bedrock*Tests.cs` | partial | Tau covers SigV4, bearer, shared profile, credential process, ECS/IMDS/web identity, AssumeRole and SSO refresh baseline; OIDC client registration renewal, multi-cache/concurrency and real AWS e2e remain open. |
| OAuth providers and credential store | `oauth.ts`, `utils/oauth/*` | `src/Tau.Ai/Auth/OAuth/*`, `OAuthProviderTests.cs`, `OAuthCredentialStoreTests.cs`, `ProviderAuthResolverTests.cs` | partial | Built-in OAuth providers exist for Anthropic, Copilot, Gemini CLI, Antigravity and OpenAI Codex; real OAuth e2e and UX-level refresh/login parity remain open. |
| Env/api key resolution and custom provider config | `env-api-keys.ts`, `api-registry.ts`, provider options | `EnvironmentApiKeyResolver.cs`, `ModelConfigurationStore.cs`, `ModelConfigurationStoreTests.cs` | partial | Tau supports env/literal/command-backed models.json subset and dynamic OpenAI-compatible registration; full upstream runtime config UX and every provider option still need mapping. |
| Secret redaction and runtime log foundation | Upstream package has auth/config surfaces; redaction is Tau hardening surface | `TauSecretRedactor.cs`, `JsonlSecretRedactor.cs`, `JsonlTauLogSink.cs`, redaction tests | ported | Tau has stronger string-value JSONL redaction foundation; remaining work is expanding patterns from real leaked samples and proving cross-module adoption. |
| Agent runtime loop and tool events | `packages/agent/src/agent-loop.ts`, `agent.ts`, `types.ts` | `src/Tau.Agent/Runtime/AgentRuntime.cs`, `ToolExecutor.cs`, `Abstractions/AgentEvents.cs`, `AgentRuntimeQueueModeTests.cs`, `AgentRuntimeToolTraceTests.cs` | partial | Core loop/tool execution/events are implemented with tests, but upstream high-level `Agent` facade semantics and every event/error/cancel edge are not yet file-level mapped. |
| Agent stream proxy | `packages/agent/src/proxy.ts` reconstructs partial assistant messages from stripped proxy events via `/api/stream` | `src/Tau.Agent/Proxy/ProxyStreamProvider.cs`, `tests/Tau.Agent.Tests/ProxyStreamProviderTests.cs` | ported | Tau now has a .NET proxy stream provider that posts the upstream request envelope, rebuilds stripped proxy events into Tau stream events, and has request/response tests. Real proxy-server e2e remains unverified. |
| Package/index export shape | `packages/agent/src/index.ts`, `packages/ai/package.json` subpath exports and bins | .NET public API in projects and CLI apps | partial | Tau public API is .NET-native and not package-export equivalent; final matrix must map user-visible API/CLI behavior instead of TypeScript export names. |

## CodingAgent + Tui Matrix

| Capability | Upstream evidence | Tau target / evidence | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| CLI package/bin/config surface | `packages/coding-agent/src/cli.ts`, `src/config.ts`, `package.json` bin `pi` and `piConfig` | `src/Tau.CodingAgent/Program.cs`, command router/runtime/settings stores | partial | Tau has real CLI/RPC/settings/session paths, but packaging/bin/config-dir parity and all flags still need a command-level map. |
| Coding tools | `src/core/tools/*` | `src/Tau.CodingAgent/Tools/*`, `ReadFileToolTests.cs`, `ListDirectoryToolTests.cs`, tool/export tests | ported | Read/ls/edit/bash/glob/grep/write baseline exists with many upstream details; remaining gaps are custom renderers, image resize/clipboard and full TUI details. |
| JSONL session tree and lifecycle | `src/core/agent-session*`, tree/session/fork/clone/compact modules | `CodingAgentTreeSessionStore.cs`, `CodingAgentSessionSwitchCoordinator.cs`, tree/resume/metadata tests | partial | Broad Tau-native tree/session baseline exists; full upstream TreeSelector, branch-switch hooks, metadata inspector layout, multi-select and exact persisted schema parity remain open. |
| RPC mode | `src/modes/rpc/*` | `CodingAgentRpcHost.cs`, `CodingAgentRpcHostTests.cs` | partial | Tau covers prompt/steer/follow-up/settings/session/tree/bash/export commands; upstream extension UI subprotocol, full settings selector, terminal/packages and richer streamed contracts remain open. |
| Slash commands, prompts, skills and extensions | `src/core/slash-commands.ts`, `prompt-templates.ts`, `skills.ts`, `extensions/*` | `CodingAgentCommandRouter`, `CodingAgentPromptTemplateStore`, `CodingAgentSkillStore`, `CodingAgentExtensionCommandStore`, event bus tests | partial | Declarative command/resource/event baseline exists; upstream TypeScript extension runtime, custom tools, lifecycle events, resource selector and diagnostics parity remain open. |
| Settings/model/theme/thinking/auth selectors | settings/keybindings/theme/auth modules and interactive components | `CodingAgentSettingsStore/Selector`, `ModelSelector`, `ThemeStore/Selector`, `ThinkingSelector`, `AuthSelector`, tests | partial | Many Tau selectors and RPC fields exist; full upstream SettingsList/submenus, package/install settings, terminal/image/markdown edit UI and OAuth dialog/session parity remain open. |
| OAuth login/logout UX | auth storage and OAuth flows under coding-agent + ai | `InteractiveOAuthLoginCallbacks.cs`, `/auth` `/login` `/logout`, `Tau.Ai/Auth/OAuth/*` | partial | Local provider selection/login/logout baseline exists; real OAuth e2e and credential refresh UX still belong to AI/CodingAgent closure. |
| Export/share/rich rendering | `src/core/export-html/*`, share/import helpers | `CodingAgentHtmlSessionExporter.cs`, `CodingAgentShareClient.cs`, HTML/export tests | partial | HTML transcript is rich but not upstream template/vendor parity; real `gh gist` share smoke and Tau share viewer remain unverified. |
| Package manager / install telemetry / changelog | `src/core/package-manager.ts`, `package-manager-cli.ts`, changelog startup behavior | `CodingAgentSettingsStore` has fields; `/changelog` reads Tau release notes | missing | Tau persists some upstream-shaped settings but does not implement package manager CLI, install telemetry flow or startup changelog parity. |
| TUI editor/input/autocomplete/history | `packages/tui/src/editor.ts`, `autocomplete.ts`, `keybindings.ts`, `kill-ring.ts`, `undo-stack.ts` | `InteractiveInputEditor.cs`, `InputHistoryStore.cs`, `TuiAutocompleteProvider.cs`, keybinding/editor tests | partial | Strong editor foundation exists; paste marker, redo/grouped undo, visual autocomplete popup and full editor box parity remain open. |
| TUI components/selectors/render diff | `components/*`, `select-list.ts`, `settings-list.ts`, `tui.ts` | `TuiSelectList`, `TuiMultiSelectList`, `TuiOverlayHost`, `TuiDiffRenderer`, `TuiTranscript*`, tests | partial | Component/render foundations exist and are wired into CodingAgent selectors; no complete upstream TUI app host/focus stack/settings-list parity yet. |
| Terminal host / images / markdown / loaders | `terminal.ts`, `terminal-image.ts`, `components/image.ts`, `markdown.ts`, `loader.ts`, `cancellable-loader.ts` | `SystemConsoleTerminal`, `TuiAnsiRenderSurface`, limited message/status/markdown helpers | missing | Full ProcessTerminal lifecycle, kitty/iTerm image rendering, marked/highlight Markdown component, loader/cancellable-loader and hardware cursor remain open. |

## WebUi Matrix

| Capability | Upstream evidence | Tau target / evidence | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| Package/component export | `packages/web-ui/package.json` exports `.` and `./app.css`; `src/index.ts`, `ChatPanel.ts`, web components | `src/Tau.WebUi` ASP.NET Core app with embedded `WebUiPage` | partial | Tau delivers a host app, not a reusable Lit/mini-lit component package with CSS export. Need decide product parity shape or build .NET static/component equivalent. |
| Chat panel, messages, input, streaming | `ChatPanel.ts`, `AgentInterface.ts`, `Messages.ts`, `MessageList.ts`, `Input.ts`, `StreamingMessageContainer.ts` | `WebUiPage.cs`, `WebChatService.cs`, `WebUiApplication.cs`, `WebUiBrowserFlowTests.cs` | partial | Tau supports create/send/stream/rename with browser tests; upstream component-level behavior and every UI state remain unmapped. |
| Attachments | `AttachmentTile.ts`, `AttachmentOverlay.ts`, `utils/attachment-utils.ts`, sandbox attachment provider | `WebChatAttachmentDto`, `WebUiPage` attachment picker/preview, service tests | partial | Basic attachment preview/send exists; upstream overlay/runtime attachment provider and richer extraction behavior remain open. |
| Storage/settings/provider keys/custom providers | `storage/*`, IndexedDB backend, settings/provider/custom stores | `WebChatStore.cs`, session JSON under `output/`, auth status/catalog endpoints | partial | Tau persists WebChat DTO sessions and settings per session, but not upstream IndexedDB store model, provider key store or custom provider UI parity. |
| Dialogs | `dialogs/SettingsDialog.ts`, `SessionListDialog.ts`, `ProvidersModelsTab.ts`, `ApiKeyPromptDialog.ts`, `CustomProviderDialog.ts`, `PersistentStorageDialog.ts` | Inline controls in `WebUiPage.cs`, API endpoints/tests | partial | Tau has simpler inline controls and auth status; full dialog workflows, persistent storage UX and API key prompt parity remain open. |
| Tool renderers and artifacts | `tools/renderers/*`, `tools/artifacts/*`, docx/xlsx/pdf/svg/html/image/text artifacts | `WebUiPage` tool cards, `WebChatHtml/Markdown/JsonlExporter`, browser/page tests | partial | Basic tool timeline and markdown rendering exist; document/xlsx/pdf/svg/html/image artifact runtime/renderers and sandbox iframe parity remain open. |
| Sandbox runtime bridge | `components/sandbox/*`, `SandboxedIframe.ts`, runtime message router/bridge/providers | No equivalent sandbox runtime provider layer found in `src/Tau.WebUi` | missing | Needed for artifact/runtime parity unless explicitly declared non-goal. |
| CodingAgent JSONL import/preview | Upstream storage/session semantics plus CodingAgent session tree surface | `CodingAgentJsonlSessionPreviewer.cs`, import endpoints/tests | partial | Tau preview/import returns branch tree audit but persists conservative timeline DTO only; true branch/tree WebUi persistence remains open. |
| Auth/model discovery/proxy utilities | `utils/model-discovery.ts`, `auth-token.ts`, `proxy-utils.ts`, provider key dialog | `GET /api/catalog`, `GET /api/auth/{provider}`, `WebUiRunnerFactory` | partial | Catalog/auth status exist; upstream proxy/token/provider-key model and login UX remain open. |
| Browser/release/static smoke | package `build/check`, example app and root browser smoke | `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`, `scripts/verify-dotnet.ps1 -RunSmoke` | external-e2e-needed | Local app browser flow exists; release/static output smoke and component package build parity remain open. |

## Mom Matrix

| Capability | Upstream evidence | Tau target / evidence | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| Package/bin/build | `packages/mom/package.json`, `mom` bin, Slack/Web API deps | `src/Tau.Mom/Tau.Mom.csproj`, `Tau.slnx`, `verify-dotnet.ps1` | partial | Worker project exists; npm package/bin/publish artifact parity remains Phase 5 work. |
| Main CLI/config | `src/main.ts`: `--sandbox`, `--download`, `MOM_SLACK_APP_TOKEN`, `MOM_SLACK_BOT_TOKEN` | `Program.cs`, `MomCommandLine.cs`, `appsettings.json`, Mom command tests | partial | Tau supports `--once`, `--validate-sandbox`, `--validate-slack`, `--download`; env/config surface differs and real Slack long-run entry still needs e2e. |
| Delegation runtime | `src/agent.ts`: `AgentSession`, Slack thread updates, usage, compaction, `last_prompt.jsonl` | `RuntimeDelegationAgentRunner.cs`, `Delegation*.cs`, `ChannelPromptDebugStore.cs`, runtime tests | partial | Tau has structured delegation/tool/usage/prompt-debug baseline, not full upstream AgentSession + Slack UI/event rendering + compaction/session tree parity. |
| Slack receive/respond/update/delete/upload | `src/slack.ts`: Socket Mode, mentions/DM, ack, queue, respond/update/delete/upload/backfill/stop | `SlackSocketModeTransport.cs`, `SlackEventMapper.cs`, `SlackWebApiResponder.cs`, queue/processor tests | external-e2e-needed | Fake connector/HTTP contract coverage exists; real Slack token/workspace smoke is required before verified. |
| Slack session sync | Upstream `log.jsonl -> context.jsonl` sync and restart recovery | `ChannelSessionStore.cs`, `ChannelLogStore.cs`, runtime/message processor tests | partial | Tau uses `context.json` Tau-native snapshot and same-workdir carry-over; upstream log-to-context JSONL sync and real Slack thread/session recovery remain open. |
| Store/log/attachment/status | `src/store.ts`, Slack attachment/download/status log behavior | `ChannelLogStore.cs`, `ChannelAttachmentStore.cs`, `ChannelStatusStore.cs`, `SlackAttachmentDownloader.cs` | partial | Tau has log/status/attachment manifests and redaction; schemas are not fully upstream-equivalent. |
| Events | `src/events.ts`: fs watch, immediate/one-shot/periodic, queue limits | `MomEventProcessor.cs`, `MomLocalDelegationFlow.cs`, `Worker.cs`, event tests | partial | Tau scans due events into inbox; not fs.watch + SlackBot.enqueueEvent real-time parity. |
| Download | `src/download.ts`, `main.ts --download` | `SlackChannelHistoryDownloadService.cs`, download tests | external-e2e-needed | HTTP contract tests exist; real Slack history/replies smoke remains open. |
| Sandbox/Docker | `src/sandbox.ts`, `docker.sh`, `dev.sh` | `MomSandbox.cs`, `MomSandboxValidator.cs`, sandbox/tool tests | external-e2e-needed | Host and Docker command construction seams exist; no real Docker container smoke or lifecycle helper parity yet. |
| Tools | `tools/bash.ts`, `read.ts`, `write.ts`, `edit.ts`, `attach.ts`, `truncate.ts` | `MomTools.cs`, `MomToolOutputTruncator.cs`, `MomSandboxAndToolsTests.cs` | ported | Five same-name tools exist with schema/diff/truncation/attachment behavior; Docker execution still gated by sandbox e2e. |
| Operator docs/helpers | `README.md`, `docs/slack-bot-minimal-guide.md`, `docs/sandbox.md`, `docs/events.md`, `scripts/migrate-timestamps.ts` | Root docs/architecture/next/history | partial | Tau lacks Mom-specific operator docs and `migrate-timestamps.ts` equivalent; exploratory docs may become non-goals only after user confirmation. |
| Runtime trace/correlation | Upstream `log.ts` + AgentSession events | `TauRuntimeLogContext`, `JsonlTauLogSink`, runtime delegation tests | partial | Tau emits delegation/response/tool/usage/end and propagates context to runner; not yet a complete cross CodingAgent/Agent/Mom/Pods e2e trace protocol. |

## Pods Matrix

| Capability | Upstream evidence | Tau target / evidence | Status | Proof / gap |
| --- | --- | --- | --- | --- |
| Package/bin/build artifact | `packages/pods/package.json`: bin `pi-pods`, build copies `models.json` and `scripts` | `src/Tau.Pods/Tau.Pods.csproj`, `Tau.Pods` CLI | partial | Runtime CLI exists, but distributable artifact/script packaging parity remains Phase 5 work. |
| Config, registration and active pod | `src/config.ts`, `commands/pods.ts` list/setup/active/remove | `PodsConfigStore.cs`, `PodsConfigValidator.cs`, `PodsCli.cs`, config/CLI tests | ported | Tau supports init/list/validate/status/active/remove and setup registration baseline with JSON output. |
| SSH/SCP transport | `src/ssh.ts`, use of `sshExec`, `sshExecStream`, `scpFile` | `PodExecService.cs`, `PodSetupService.cs`, lifecycle/model/vLLM services | external-e2e-needed | Process argv hardening/fake runners exist; real SSH/SCP smoke is not yet proven. |
| Setup run | `commands/pods.ts setupPod`: HF/PI token check, copy `pod_setup.sh`, run setup, detect GPUs, save config | `PodSetupPlanner.cs`, `PodSetupService.cs`, setup tests | external-e2e-needed | Tau has plan/run/SCP/GPU-detect contract and token redaction tests; real remote setup/GPU smoke remains open. |
| Known model configs and GPU allocation | `src/models.json`, `model-configs.ts`, `startModel` GPU selection, memory/context overrides | `PodVllmCommandPlanner.cs`, `PodDefinition.Gpus`, vLLM option parsing | partial | Tau plans vLLM commands and records GPUs, but upstream known-model compatibility matrix, round-robin GPU allocation, `--memory` and `--context` behavior are not fully ported. |
| Model lifecycle | `commands/models.ts`: start/stop/list/logs/show known models | `PodModelService.cs`, `PodLifecycleService.cs`, `PodVllmOrchestrationService.cs`, tests | partial | Tau splits HF cache model lifecycle and vLLM deployment lifecycle; upstream direct `start/stop/logs/list` behavior, log streaming and process verification need command-level mapping. |
| vLLM plan/preflight/deploy/status/health/stop | Upstream model run scripts and SSH commands | `PodVllmCommandPlanner.cs`, `PodVllmOrchestrationService.cs`, vLLM tests | external-e2e-needed | Tau has richer systemd/nohup/health/rollback baseline and many fake-runner tests; no real HF/GPU/vLLM startup smoke yet. |
| Prompt against pod model | `commands/prompt.ts` builds local coding-agent args but throws `Not implemented` | No Tau `pods prompt` equivalent found | missing | Upstream is itself placeholder, but final matrix still needs a decision: port usable prompt path, map to CodingAgent base URL, or mark user-confirmed non-goal. |
| Runtime logs/correlation | Upstream mostly console/process oriented | `ITauLogSink` integration in probe/lifecycle/model/vLLM services | partial | Tau emits structured operation summaries; correlation through real remote e2e and release smoke remains unverified. |
| Root release/scripts parity | `packages/pods/scripts`, root `build-binaries.sh` / release | `scripts/verify-dotnet.ps1`, no complete release scripts | missing | Final 100% requires real Tau executable artifact and install/release chain. |

## External E2E Needed

Initial known list:

- AI provider family real e2e: OpenAI/Responses/Codex/Azure/Anthropic/Google/Vertex/Gemini CLI/Antigravity/Mistral/Bedrock/OpenAI-compatible.
- OAuth real e2e: OpenAI Codex, Anthropic, Gemini CLI/Antigravity, GitHub Copilot device flow.
- AWS/Bedrock e2e: SigV4, SSO refresh, OIDC registration renewal, AssumeRole/profile/credential_process.
- Slack e2e: Socket Mode receive/ack/respond/update/delete/download/stop.
- Docker e2e: Mom sandbox validate + tool execution.
- Pods e2e: SSH/HF/GPU/vLLM full path.
- WebUi e2e: browser smoke against release/static output.

## Next Inventory Freeze Steps

- Expand this capability-level matrix into file-level mapping for every upstream `src/**` file before Phase 1 exit.
- Add command/API/env/config/log/schema submatrices for CodingAgent, WebUi, Mom and Pods.
- Move first implementation candidates into Phase 2 only when their upstream evidence, Tau target files and validation command are explicit.
- Keep `external-e2e-needed` entries open until real provider/service/runtime smoke exists or the user explicitly accepts a non-goal.
