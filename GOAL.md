# Tau 100% pi-mono parity /goal

## /goal prompt

当前目标：把 Tau 从已完成的 Agent platform baseline 继续推进到对 `C:\Users\zhouh\Desktop\pi-mono-main` 的所有能力 100% 可审计移植。

你是 Main Integrator，不是单模块 worker。当前主线恢复为 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`；Phase 1 inventory freeze 的权威矩阵是 `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`。后续实现必须从 matrix 的 `Phase 2 Candidate Queue` 或当前 active plan 明确列出的 Phase 2/3/4/5 缺口领取，不重开 broad inventory。

`docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md` 是已完成的前置能力：它证明 Tau 已具备第一版可复用 Agent 应用底座、examples、provider run / tool execution runtime log、platform smoke 和全仓 gate 证据。它不是 100% product parity 完成证据，也不能关闭真实 provider/OAuth、Slack、Docker、SSH/HF/GPU/vLLM、WebUi artifact/runtime、Tui live terminal、release/package registry 等最终缺口。

每一轮都按当前 repo 事实推进：先区分 committed baseline、dirty WIP、已验证结果、文档声称和真实上游行为；再选择一个可审计、可验证、可单独评审的 parity 切片。不要问“要不要继续”。只有存在真正歧义且继续会产出与用户意图相反的成果时才停下来问。

## Current checkpoint

- [x] Agent platform baseline completed：`src/Tau.Agent/Platform/**`、Console/HTTP examples、`verify-agent-platform-examples.ps1`、provider run + tool execution runtime log 和本地验收已完成，当前作为全量移植的 shared Agent foundation。
- [x] Phase 1 inventory freeze completed：`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 已冻结 capability、file-level、surface 和 root scripts/manifests 三层 inventory；不重开 broad package scan。
- [x] Dirty WIP boundary closure：Agent platform baseline、CodingAgent `/settings select` UI parity 和 100% parity goal restore 已拆成独立提交并推送；后续实现继续从 Phase 2 Candidate Queue 领取窄切片，不能把 SDK/API、UI parity、Pods runtime 和 docs-only pivot 混成一个不可审查提交。
- [ ] Phase 2 critical contract closure：provider/auth、Agent/tool/session/runtime log、CodingAgent RPC/session/settings、Tui terminal/selector、WebUi session/artifact、Mom Slack/sandbox、Pods operation schema 等跨模块合同继续按 matrix 收口。
- [ ] Phase 3 product runtime parity：CodingAgent + Tui、WebUi、Mom、Pods 的用户可见 runtime 行为继续补齐，不能用 fake-only tests 替代产品行为。
- [ ] Phase 4 external e2e closure：真实 provider/OAuth/AWS/Slack/Docker/Pods/WebUi/browser/release 等 e2e 要通过；没有环境时保持 `external-e2e-needed`，不能标成完成。
- [ ] Phase 5 release/package/CI final parity：Tau release/CI 必须产出真实 executable/package artifacts，并完成 registry/signing/provenance/non-host smoke 或取得用户明确非目标确认。
- [ ] Final audit：matrix 全部 `verified` 或用户确认 `non-goal`，`next.md` 无未完成 parity backlog，active plan 可归档为 completed。

## Strict completion criteria

目标只能在以下条件全部满足后标记 complete：

- 上游 `packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods` 以及 root scripts/manifests 的用户可见功能、协议、命令、配置、环境变量、错误语义、日志、持久化 schema 和 release/CI 行为都有 Tau 对应实现、验证证据，或有用户明确确认的 `non-goal`。
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 中所有条目最终为 `verified` 或用户确认的 `non-goal`；`partial`、`missing`、`ported`、`external-e2e-needed` 不能作为最终完成状态。
- `next.md` 不再保留未完成 product parity backlog；无法完成的项必须写成用户确认的非目标，而不是“后续优化”。
- Windows PowerShell 本地权威链通过：
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
- 真实外部 e2e 通过，或用户明确确认非目标：AI providers、OAuth、AWS/Bedrock、Slack、Docker、Pods SSH/HF/GPU/vLLM、WebUi browser/release/static smoke、package registry/signing/provenance/release smoke。
- release/CI 产出真实 Tau executable/package artifacts；不能只保留 dry-run、fake runner 或占位 wrapper。
- 每个实质变更都有 `docs/histories/YYYY-MM/**` 记录；active plan、matrix、`next.md`、`docs/QUALITY_SCORE.md` 和必要架构/安全/可靠性文档同步。

## Current audit snapshot

本节是 2026-06-08 基于当前 HEAD、上游目录、active matrix 和本地工作树重新审视后的事实面，用来防止把 baseline、fake tests 或局部 planning 误写成 100% 完成。

- 本轮审视时 committed baseline 已到 `780f858 fix`，`main` 与 `origin/main` 对齐；该提交已包含 Pods record-shaped config/schema 本地合同切片、history 和部分文档同步。当前没有本轮待提交实现 WIP。
- 上游 package directory set 当前仍是 `agent`、`ai`、`coding-agent`、`mom`、`pods`、`tui`、`web-ui`，与 Phase 1 inventory freeze 匹配；本轮没有发现新的未知上游 package。
- 上游 root scripts 当前仍包括 `browser-smoke-entry.ts`、`build-binaries.sh`、`check-browser-smoke.mjs`、`cost.ts`、`edit-tool-stats.mjs`、`profile-coding-agent-node.mjs`、`release.mjs`、`session-transcripts.ts`、`sync-versions.js`；这些 release/test/audit surfaces 仍不能用普通 `dotnet test` 替代最终验收。
- 按当前 matrix 全表 status 列机器计数为 `partial=187`、`external-e2e-needed=31`、`ported=31`、`missing=12`、`non-goal-proposed=1`、`verified=0`。这说明当前仓库是高覆盖 baseline + 大量局部合同测试状态，不是 100% 移植完成状态；`ported` 也只是“已有本地证据但未达到最终验收”，不能当完成。
- 当前 `missing` 行集中在 package manager/install telemetry/changelog、RPC extension UI protocol、image/attachment ingestion、WebUi sandbox/tool/artifact runtime/prompt constants、Pods prompt/agent/pod-model user command、root release/scripts parity。任何一个 `missing` 未关闭，100% 验收都不能通过。
- 当前 `external-e2e-needed` 行集中在真实 AI provider/OAuth/AWS、live terminal/TTY、WebUi browser/release/static、Slack receive/respond/file/history、Docker sandbox、Pods SSH/SCP/setup/GPU/vLLM。没有真实环境证据时只能保持 open，不能用 fake/stub tests 降级为完成。
- 最新 Pods config path/env/schema 切片已关闭上游 path/env、missing-file 和本地 record-shaped config 子合同：对照上游 `packages/pods/src/config.ts` / `types.ts`，Tau 未显式传 config path 时会读取/写入 `PI_CONFIG_DIR/pods.json` 或无 env 默认 `~/.pi/pods.json`；`PodsConfigStore.Load` 对缺失文件返回空 `PodsConfig`，能读取上游 `pods: Record<string, Pod>` / `models: Record<string, Model>` config，也继续读取旧 Tau list-shaped config；`Save` 写回上游 record shape 并保留 active、ssh command、GPU inventory、modelsPath、vLLM version 和 per-pod configured model state。该能力仍不关闭真实 remote e2e、top-level operation compatibility、usage-aware GPU allocation state 或 Pods final `verified` 状态。
- 最新 Pods top-level command shim 已关闭一层本地合同：`pods` / `pods setup` / `pods active` / `pods remove` 复用配置与注册路径，`start <model> --name <name>` 复用 `vllm deploy` 并强制上游 `--name` 要求，`list` 复用 active/指定 pod deployments，`stop <name>` 复用 `vllm stop`；该切片已通过 PodsCli targeted 和完整 Pods tests。它仍不关闭 `shell`、`agent` / prompt mapping、无参 stop-all、上游本地 PID/log-tail/startup streaming exact flow、真实 SSH/HF/GPU/vLLM smoke 或 Pods final `verified` 状态。
- `docs/QUALITY_SCORE.md` 当前仍把关键产品面、测试、CI/CD、可观测性、安全配置标为 `C` 风险；只要这些风险仍指向 parity 缺口，Final audit 就不能关闭。

## 100% acceptance ledger

| Gate | Current state | Work required before 100% |
| --- | --- | --- |
| Inventory coverage | Phase 1 grouped inventory 已覆盖 7 个上游 package 和 root scripts；按当前 matrix 全表 status 列统计仍是 `partial=187`、`external-e2e-needed=31`、`ported=31`、`missing=12`、`non-goal-proposed=1`、`verified=0`。 | 每个 file/surface/root-script row 必须最终转成 `verified`，或有用户明确确认的 `non-goal`；不得保留 `partial`、`missing`、`ported`、`external-e2e-needed`。 |
| Contract parity | AI/Agent/CodingAgent/Tui/WebUi/Mom/Pods 都有多条 local contract baseline。 | 关闭 public API/bin/export、CLI/RPC/HTTP、config/env、persisted schema、runtime log、failureKind/error shape、session/tree/schema 和 operation result schema 的剩余缺口。 |
| Product runtime parity | CodingAgent/Tui、WebUi、Mom、Pods 已有本地可运行产品切片。 | 补真实 terminal host、branch/tree persistence、artifact/runtime bridge、Slack/Docker runtime、Pods setup/deploy/log/startup/rollback/allocation 等用户会实际碰到的路径。 |
| External e2e | 当前大多是 fake/stub/contract tests；matrix 全表当前有 `external-e2e-needed=31` 行明确需要真实 provider/service/runtime e2e。 | 用真实凭证、真实服务、真实容器或真实远端 pod 跑脱敏 smoke；没有环境时保持 open，最终只能由用户确认 `non-goal` 关闭。 |
| Release/package/install | 已有 PowerShell-first gate、artifact baseline、release dry-run/apply helpers、package publish/sign/provenance dry-run contracts；root release/scripts parity 仍有 `missing`/`partial`。 | 完成真实 registry publish rehearsal、package consumer smoke、signing/provenance rehearsal、non-host executable smoke、global alias/install parity、release/static browser smoke。 |
| Documentation and history | `GOAL.md`、active plan、matrix、`next.md`、quality/history 已建立协作闭环。 | 每个实质切片同轮同步 matrix/active plan/next/quality/history；最终 `next.md` 不得隐藏任何 product parity backlog。 |
| Final validation | 本地 PowerShell gate 曾多次通过，但这只是当前 baseline 证据。 | 最终必须重新通过 `verify-dotnet.ps1 -SkipRestore`、`verify-dotnet.ps1 -SkipRestore -RunSmoke`、release/package gates 和 external e2e gates，再归档 active plan。 |

## Current incomplete surface ledger

本轮窄源复核确认：上游仍存在 `packages/coding-agent/src/package-manager-cli.ts`、`packages/coding-agent/src/core/package-manager.ts`、`packages/coding-agent/src/modes/rpc/rpc-types.ts`、`packages/web-ui/src/components/sandbox/**`、`packages/pods/src/commands/prompt.ts`、root `scripts/release.mjs` / `build-binaries.sh` / `check-browser-smoke.mjs` 等未完全闭合 surfaces。下面表格是当前 100% 移植不能通过的主要阻塞面；最终以 matrix row 逐项 `verified` 或用户确认 `non-goal` 为准。

| Area | Still incomplete before 100% | Required closure evidence |
| --- | --- | --- |
| Global scripts / release | `package.json` workspace/install 行为、`test.sh`/`pi-test.sh` exact shell wrapper、`build-binaries.sh`、`release.mjs` publish/push/version flow、browser/static smoke、session transcript analyze、startup profiling、edit stats、cost persistence、version sync。 | 真实 release artifacts、non-host executable smoke、registry publish rehearsal、signing/provenance rehearsal、package/global install alias、release/static browser smoke；不能只用 dry-run 或 fake runner。 |
| Tau.Ai | 真实 provider/OAuth/AWS e2e、exact `pi-ai` package/bin/export/subpath、KnownApi 名称差异、`onPayload`/`onResponse` callback、TypeBox/AJV keyword parity、full generated model catalog、非标准 secret pattern。 | provider/auth external smoke、public API/package consumer sample、CLI/bin alias smoke、model catalog audit、redaction sample audit。 |
| Tau.Agent | 高层 facade option pass-through、public export shape、package consumer boundary、real proxy/server e2e。 | public compile sample、package consumer smoke、real proxy/e2e 或用户确认 Tau-native non-goal。 |
| Tau.CodingAgent | package manager/install telemetry/startup changelog、top-level CLI flags、full TreeSelector/session schema、RPC extension UI protocol、custom tool/extension runtime、image/clipboard ingestion、full settings runtime wiring、usage cost/share/export transcript parity。 | CLI/RPC/schema targeted tests、extension UI headless contract tests、image/file/clipboard smoke、session tree migration/import/export tests、real provider vision/share e2e。 |
| Tau.Tui | live `ProcessTerminal` host 接管、real TTY/PTY smoke、hardware cursor、complete overlay compositor、theme rendering、terminal image real Kitty/iTerm/Windows smoke、TUI first-frame/CPU profile。 | live terminal transcript/smoke、component/host tests、theme/render regression、startup profile evidence。 |
| Tau.WebUi | CodingAgent branch/tree true persistence、semantic import/navigation、sandbox/artifact runtime bridge、JavaScript REPL/tool runtime、IndexedDB/provider-key/custom-provider UI、component package/release/static browser parity。 | API/browser tests、artifact sandbox browser smoke、release/static packaged smoke、session tree persistence evidence。 |
| Tau.Mom | real Slack Socket Mode/Web API/file/history/stop smoke、Slack session sync/schema、real Docker container sandbox/tool smoke、fs-watch/higher-level delegation、trace/correlation unification。 | real Slack workspace smoke、real Docker validation/tool smoke、session/log/schema tests、external e2e evidence or confirmed non-goal。 |
| Tau.Pods | `shell`、`agent` / prompt mapping、no-arg stop-all、usage-aware GPU allocation state、real SSH/SCP/HF/setup/GPU/vLLM smoke、upstream local PID/log-tail/startup streaming exact flow、multi-version rollback/transport hardening；`pods/start/list/stop/logs/ssh` top-level 本地 shim 已有合同测试但仍不是真实 remote e2e。 | CLI alias/operation tests, real remote pod smoke, vLLM startup/health/log evidence, model allocation state tests. |

## Gap classification plan

后续每个 `/goal` 执行切片必须先归入一个 blocker class，再领取对应 matrix row；同一提交不能混合多个无关 class。

- `contract`：先收口 AI public API/bin/export、Agent facade/export/proxy、CodingAgent CLI/RPC/session/config、Tui host/component contracts、WebUi session/artifact API、Mom channel/session/log schema、Pods command/config/schema。验收以 source 对照、targeted tests、compile/API sample、CLI/RPC/HTTP shape 为准。
- `runtime`：再收口真实产品路径。重点是 CodingAgent/Tui terminal host 和 settings/runtime wiring、WebUi branch/tree + artifact sandbox、Mom Slack-compatible delegation + Docker sandbox、Pods top-level operation compatibility + setup/start/log/rollback/allocation。
- `external-e2e`：把 AI provider/OAuth/AWS、Slack、Docker、Pods SSH/HF/GPU/vLLM、WebUi packaged/browser、release/static smoke 从 fake/stub 证据推进到真实环境证据；没有真实环境时不得降级为完成。
- `release-package`：关闭 root scripts/manifests、no-env/pi-test shell parity、build/release/finalize/publish/sign/provenance、NuGet/package consumer、global alias/install、non-host executable smoke。
- `final-audit`：只在 matrix 清零、`next.md` parity backlog 清零、quality 不再标出影响 parity 的 `C` 风险、两条 PowerShell gate 和所有 release/e2e gate 通过后执行。

## Current 100% gap map

本节是当前审视后的 100% 移植缺口地图。它不替代 matrix，但为后续 `/goal` 执行提供主控分派顺序。所有条目最终只能以 `verified` 或用户明确确认的 `non-goal` 关闭；`ported`、`partial`、`missing`、`external-e2e-needed` 都不是完成状态。

### Global scripts / release / root manifests

- `package.json` workspace 行为仍未完全映射到 Tau 的 build/check/test/version/publish/install 交付链；当前已有 PowerShell-first gate、Windows CI、release plan/execute/finalize/publish dry-run/apply baseline，但真实 registry publish、push/publish 远端演练、install/global alias parity 仍未最终关闭。
- `test.sh` / `pi-test.sh` 仍未达到 exact Unix shell wrapper parity；Tau 已有 no-env 隔离和 `pi-test.ps1`，但没有上游式 auth backup / shell wrapper 完整等价。
- `scripts/build-binaries.sh` / `scripts/release.mjs` 仍需关闭非宿主平台 executable smoke、真实 package registry、signing/provenance rehearsal、release archive signing、examples/Photon/interactive raster assets/external export-html payload parity。
- `scripts/check-browser-smoke.mjs` 的 release/static browser smoke 仍缺最终 closure。
- `scripts/session-transcripts.ts` 的 `--analyze` 子 agent flow、`scripts/profile-coding-agent-node.mjs` 的 TUI first-frame / CPU profile / Node-Bun comparison、`scripts/edit-tool-stats.mjs` 对真实 edit runtime/e2e、`scripts/cost.ts` 对 CodingAgent assistant usage cost 默认持久化都仍是未完成项。
- `scripts/sync-versions.js` 的 NuGet/package publish sync 策略仍未最终关闭。
- 上游 package 脚本仍有未完全关闭项：AI generated model full coverage、Pods `pod_setup.sh` vendored script / real setup smoke、Pods `model_run.sh` startup-monitor parity、Mom timestamp migration 的真实 Slack runtime/session sync。

### Tau.Ai

- 真实 provider e2e 未关闭：OpenAI / Responses / Codex / Azure / Copilot / Anthropic / Google / Vertex / Gemini CLI / Antigravity / Mistral / Bedrock 都需要真实凭证或真实服务 smoke 证据。
- OAuth/auth 未关闭：真实 login/refresh UX、credential refresh diagnostics、provider-specific edge errors、Bedrock OIDC client registration renewal、多 cache/concurrency 和真实 AWS e2e 仍需验证或实现。
- Public API / package surface 未关闭：`pi-ai` global/package alias、TypeScript export/subpath 到 Tau-native API 的完整映射、KnownApi 名称差异、`onPayload` / `onResponse` callback 等价、TypeBox/AJV exact keyword/schema parity 仍需实现或用户确认非目标。
- Model registry 未关闭：generated seed 只证明 Tau 当前支持 family，未证明等于上游 full generated model catalog。
- Config/security 未完全关闭：models.json/runtime config UX、非标准 secret pattern、真实云端 credential chain 和跨模块 redaction 采样仍需补充证据。

### Tau.Agent

- 高层 facade 已有 baseline，但 broader option pass-through、public export shape、package consumer boundary、real proxy server e2e 仍未关闭。
- Agent platform baseline 只能作为 shared foundation；不能替代真实 provider/OAuth、真实 package consumer、真实 proxy/e2e 或上游 package export parity。

### Tau.CodingAgent

- CLI/session/config/package parity 未最终关闭：完整 TreeSelector/session schema、package/bin/config、settings/runtime auth parity、credential refresh UX、migration helpers 到真实 runtime 的覆盖仍需收口。
- RPC/extension/custom tool runtime 未关闭：TypeScript extension runtime、extension UI protocol、actual hooks/custom tools migration to extensions、package/extension shortcut migration、general custom tool runtime parity 仍缺。
- 产品输入/输出缺口仍在：image/clipboard ingestion、provider vision e2e、full share/export/runtime transcript semantics、usage cost persistence 和 higher-level default path regression 仍未完成。
- Settings/TUI 集成仍未最终关闭：完整 settings submenus、transport/package/shell path/npm settings、terminal/editor/image/changelog persisted runtime wiring、auto-retry/compaction/cancellation UI 和 full resource selector 仍缺。

### Tau.Tui

- 库层 foundation 已覆盖大量组件，但真实 `ProcessTerminal` host 接管、live TTY/PTY smoke、hardware cursor、complete overlay compositing、theme rendering、full TUI app host 和 CodingAgent 主屏接管仍未关闭。
- Terminal image 仍需真实 Kitty/iTerm2/Windows terminal smoke；Markdown/theme integration 也不能只靠本地组件测试作为最终完成证据。
- Startup profiling 仍缺 TUI first-frame timing 与 CPU profile closure。

### Tau.WebUi

- WebChat DTO baseline 仍未等价 CodingAgent branch/tree session：true branch/tree persistence、semantic import、session tree storage 和 branch navigation 仍需关闭。
- Artifact/sandbox runtime bridge 未完成：artifact renderers、sandboxed iframe/runtime providers、JavaScript REPL/tool runtime、browser API smoke 仍需真实产品路径验证。
- IndexedDB/provider-key/custom-provider UI、reusable component package parity、browser/release/static packaging parity 仍未完成。
- Release/static WebUi smoke 不能只靠 ASP.NET 测试替代，最终必须覆盖 packaged/static output 或用户确认非目标。

### Tau.Mom

- Slack 真实 e2e 未关闭：Socket Mode receive/ack/respond、thread reply、file download/upload、stop/cancel、startup backfill、Slack session sync/schema 都需要真实 workspace smoke 或用户确认非目标。
- Docker sandbox 未关闭：真实 container validate、tool execution、filesystem boundary、permission/error semantics 仍需 smoke。
- Higher-level delegation flow、fs-watch event runtime、workspace/session schema parity、runtime trace/correlation 统一协议仍需补齐。
- Timestamp migration helper 只有 fixture smoke，不证明真实 Slack runtime/session sync。

### Tau.Pods

- 真实 remote e2e 未关闭：SSH/SCP、HF download、setup run、GPU detect、vLLM startup/health、systemd user/fallback pid/log path、remote logs/status/deployments 都需要真实 pod smoke。
- Upstream operation compatibility 仍未最终关闭：`pods` group、top-level `start/list/stop` 和既有 `ssh/logs` 已有本地 shim/contract tests，但 `shell`、`agent` / prompt mapping、no-arg stop-all、direct PID/log-tail/startup streaming flow 和 pod prompt command mapping 仍需实现或非目标确认。
- Config/schema 本地合同已关闭 baseline：`PI_CONFIG_DIR -> pods.json`、无 env `~/.pi/pods.json` 默认、missing config 空对象语义、上游 record-shaped `pods` / `models` 读写、旧 Tau list-shaped load migration、active top-level 和 configured model state round-trip 已有测试覆盖。最终仍需把这些合同放到真实 remote operations/e2e 与 command compatibility 中审计，不能直接标成 Pods `verified`。
- GPU/model allocation 未关闭：已完成本地 `--gpus` / `--memory` / `--context` planning baseline，但 usage-aware round-robin `pod.models[*].gpu` allocation state、多版本 rollout/rollback、long-running remote transport hardening 和 real ops smoke 仍缺。
- `pod_setup.sh` 未作为 Tau script vendored 并做真实远端验证；`model_run.sh` 与 systemd/nohup 差异需要真实 smoke 后决定实现、映射或非目标。

## 100% migration execution plan

后续执行从下列 pass 推进。每个 pass 完成后同步 matrix、active plan、`next.md`、`docs/QUALITY_SCORE.md` 和 history；如果 pass 中某条不能实现，只能保持 open 或提交用户明确确认的 `non-goal` 记录。

### Pass 0：状态校准与 stale 文档清理

- 重新读取当前 `git status`、matrix、active plan、`next.md`、`QUALITY_SCORE` 和上游 package manifests，确认没有未知上游目录或 root script。
- 每轮开始重新机器计数 matrix 状态，当前全表校准值为 `partial=187`、`external-e2e-needed=31`、`ported=31`、`missing=12`、`non-goal-proposed=1`、`verified=0`；如果 matrix、GOAL、next、quality 对同一缺口有矛盾，先修文档事实再实现。
- 清理 matrix 中与最近实现不一致的 stale 文字，尤其是 Pods 本地 `--gpus` / `--memory` / `--context` planning、`PI_CONFIG_DIR` / no-env `~/.pi/pods.json` / missing config 空对象语义、record-shaped config/schema 已完成但真实 remote e2e、top-level command compatibility 和 usage-aware GPU allocation 仍 open 的状态表达。
- 将 backlog 重新归类为 `contract`、`runtime`、`external-e2e`、`release/package`、`non-goal-proposed` 五类，避免把本地测试完成误写成 final acceptance。
- 验收：`git diff --check` 通过，matrix/goal/next 对同一缺口没有互相矛盾的完成声明。

### Pass 1：public API / CLI / config / schema contract closure

- 关闭 AI public API/bin/export/helper、Agent facade/export/proxy、CodingAgent CLI/RPC/session/config、Tui host contracts、WebUi session/artifact API、Mom channel/session/log schema、Pods config/command/schema 的合同缺口。
- 每个 contract 关闭必须同时对照上游源码、Tau source/tests、targeted tests 和 public compile/API sample；不能只改文档。
- 验收：各模块 targeted tests 通过，public API compile samples 覆盖新增/变更合同，matrix contract rows 只剩外部 e2e 或明确非目标。

### Pass 2：product runtime parity closure

- CodingAgent/Tui：接真实 terminal host、full settings/resource selector/runtime wiring、image/clipboard ingestion、live TTY/PTY smoke。
- WebUi：把 session 从 WebChat DTO baseline 推到 CodingAgent branch/tree true persistence，补 artifact/sandbox runtime bridge 和 browser product flow。
- Mom：补 Slack-compatible higher-level delegation、fs-watch/runtime session flow、Docker sandbox runtime。
- Pods：补 direct operation commands、setup/deploy/logs/startup streaming、remote transport hardening、allocation/rollback state。
- 验收：runtime feature 有 targeted tests 和 smoke；fake-only runner 只能证明合同，不能把真实产品行为标成完成。

### Pass 3：external e2e closure

- AI providers/OAuth/AWS：用真实凭证或真实服务跑最小 e2e，记录脱敏证据、失败语义和环境边界。
- Slack/Docker/Pods/WebUi/browser：完成真实 Slack workspace、真实 Docker container、真实 SSH/HF/GPU/vLLM pod、WebUi packaged/static/browser flow smoke。
- 所有外部 e2e 缺环境时保持 `external-e2e-needed`；最终 100% 前必须补环境验证或取得用户明确非目标确认。
- 验收：External E2E list 清零，或每个无法执行项都有用户确认的 `non-goal` 和对应 matrix 记录。

### Pass 4：release/package/install parity closure

- 关闭 root scripts/manifests：build/test/no-env/pi-test/release/version/finalize/publish/sign/provenance/package install/global alias/browser smoke。
- 完成真实 NuGet/package registry publish rehearsal、real signing/provenance rehearsal、non-host executable smoke 或用户确认非目标。
- 验收：release artifact 在干净目录可运行核心命令；package consumer smoke 能消费 `Tau.Ai` / `Tau.Agent` / `Tau.Tui`；registry/signing/provenance 不停留在 dry-run。

### Pass 5：final audit

- matrix 全部为 `verified` 或用户确认 `non-goal`；没有 `partial`、`missing`、`ported`、`external-e2e-needed`。
- `next.md` 不再保留 product parity backlog；只允许记录 post-100% improvement，不允许藏未完成 parity。
- active 100% plan 归档到 completed；`docs/QUALITY_SCORE.md` 不再把关键模块标为会影响 parity 的 `C` 风险。
- 通过权威本地链：
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
- 通过 release/package/external e2e gate；最终报告列出每个上游 package/root script 的验收证据和剩余用户确认非目标。

## Current worktree boundary

继续实现或提交前必须重新读取 `git status --short --branch` 和相关 diff，并保持以下边界：

- 已关闭的旧 dirty WIP：Agent platform baseline、CodingAgent `/settings select` UI parity 和 docs-only goal restore 已分别提交并推送，不再作为当前未提交边界处理。
- 当前实现切片必须来自 `Phase 2 Candidate Queue` 或 active plan 的明确缺口；每个切片都要同时对照上游源码、Tau 当前 source/tests、targeted validation、plan/next/quality/history。
- Main Integrator 独占 shared docs/history/scripts/solution 与最终验证；module worker 只改自己的模块和相邻测试，不把 unrelated parity lane 混入同一 commit。

## Source of truth

每轮先按这个顺序建立事实面：

1. `AGENTS.md`
2. `docs/REPO_COLLAB_GUIDE.md`
3. `docs/ARCHITECTURE.md`
4. `docs/QUALITY_SCORE.md`
5. `docs/PLANS_GUIDE.md`
6. `docs/HISTORY_GUIDE.md`
7. 当前 `git status --short --branch`
8. 当前 diff，尤其是未提交 WIP 与本轮预期写集
9. `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
10. `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
11. `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`
12. `next.md`
13. 当前 Tau source/tests/examples/scripts
14. 上游 `C:\Users\zhouh\Desktop\pi-mono-main` 中与当前切片有关的源码、tests、scripts 和 package metadata

不要只读文档就下结论。每个实现切片必须同时读取真实上游源码和 Tau 当前代码/tests；涉及 OpenAI/Azure/Anthropic/AWS/GitHub/Slack/Docker/SSH/vLLM 等可能漂移的 API、协议或报错时，必须查官方资料、真实日志或真实测试证据，不能凭猜测编写代码。

## Core loop

每一轮严格执行 MAGI 大轮次。

### 1. 审视

- 重新确认工作树、active plans、matrix、`next.md` 和相关 diff。
- 从 Phase 2 Candidate Queue 或当前 plan 中选择一个高价值、互斥写集清晰、可验证的切片。
- 对照上游 `pi-mono-main` 和 Tau 当前实现，写清行为合同、风险、验证命令和剩余缺口。
- 明确是否触碰 public API、持久化格式、secret/auth、CLI/RPC/HTTP contract、runtime log schema、release/package contract 或外部 e2e。

### 2. 执行

- 默认用多 Agent 并行推进互不重叠模块，但 shared docs/history/scripts/solution 由 Main Integrator 独占。
- 优先关闭真实 parity 缺口和外部 e2e blocker；不要把 fake provider、stub runner 或纯模型测试包装成 100% 完成。
- 保持切片小而完整：实现、targeted tests/smoke、plan/next/quality/history 同轮收口。
- 遇到报错先读 log 和失败根因；涉及不确定 API 时查官方文档或 GitHub issues。

### 3. 提升

- 主控整合 diff，串行运行会写同一 `bin/obj/output/.tau` 的验证。
- 同步 active plan、matrix、`next.md`、`docs/QUALITY_SCORE.md`、必要 docs 和 history。
- 形成清晰 commit 边界；不要把 Agent platform、UI parity、release/e2e 和 docs-only pivot 混成一个不可审查提交。
- 如果总目标仍未满足，直接进入下一轮切片，不问“要不要继续”。

## Worker ownership

Main Integrator 独占：

- `GOAL.md`
- `AGENTS.md`
- `README.md`
- `next.md`
- `docs/**`
- `scripts/**`
- solution/project 组织文件
- 最终验证、history、commit 边界和 plan/matrix 状态

Module workers：

- `AiWorker`：`src/Tau.Ai/**`、`src/Tau.Ai.Cli/**`、`tests/Tau.Ai.Tests/**`
- `AgentWorker`：`src/Tau.Agent/**`、`tests/Tau.Agent.Tests/**`
- `CodingAgentWorker`：`src/Tau.CodingAgent/**`、`tests/Tau.CodingAgent.Tests/**`
- `TuiWorker`：`src/Tau.Tui/**`、`tests/Tau.Tui.Tests/**`
- `WebUiWorker`：`src/Tau.WebUi/**`、`tests/Tau.WebUi.Tests/**`
- `MomWorker`：`src/Tau.Mom/**`、Mom 相关 `tests/Tau.Agent.Tests/**`
- `PodsWorker`：`src/Tau.Pods/**`、`tests/Tau.Pods.Tests/**`
- `Inventory/QA Agent`：只读维护 matrix、外部 e2e 证据和缺口分类

Worker 默认不改 `docs/**`、`next.md`、`scripts/**`、solution/project 文件或其它 worker 的模块。需要共享合同时先由 Main Integrator 定边界，再重新分派。

## Next execution priority

本轮审视后，下一步不是重新做 inventory，而是从当前 `missing=12`、`external-e2e-needed=31` 和高阻塞 `partial` 行中领取可验证切片：

1. 先关闭低歧义 contract 缺口：Pods top-level command compatibility / operation schema；CodingAgent RPC extension UI protocol；CodingAgent image/clipboard ingestion；WebUi sandbox/tool/artifact runtime API。
2. 再推进产品 runtime 缺口：Tui live terminal host / real TTY smoke、WebUi CodingAgent branch/tree true persistence、Mom Docker sandbox runtime、Pods top-level operation compatibility / setup/start/logs/allocation/rollback。
3. 并行准备但不伪造 external e2e：AI provider/OAuth/AWS、Slack workspace、Docker container、Pods SSH/HF/GPU/vLLM、WebUi packaged/static/browser、release/package registry/signing/provenance；没有真实环境就保持 `external-e2e-needed`。
4. Release/package/install 作为单独主控切片收口：publish/push execution、真实 NuGet/package registry rehearsal、package consumer smoke、non-host executable smoke、global alias/install parity、release/static browser smoke。
5. 每轮只领取一个可审计切片，完成后同步 matrix、next、quality、history 和必要 docs；没有真实 e2e 环境时保持 open，不做完成声明。

所有后续完成声明都必须用当前仓库验证和上游对照支撑，不能用历史记忆或文档声称替代证据。
