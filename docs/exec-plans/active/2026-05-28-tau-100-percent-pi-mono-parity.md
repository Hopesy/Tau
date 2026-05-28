# Tau 100% pi-mono parity 多 Agent 移植计划

## 目标

把 Tau 从当前多面 baseline 推进到 `C:\Users\zhouh\Desktop\pi-mono-main` 的 100% 可审计移植状态。这里的 100% 不按聊天里的主观完成度计算，而按上游 inventory、行为合同、真实运行验证、发布产物和文档闭环共同判定：

- 上游 `packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods` 的用户可见功能、协议、命令、配置、环境变量、错误语义、运行日志和发布脚本都有 Tau 对应实现、测试或经用户确认的非目标说明。
- Tau 对应模块在 `.NET` 实现里通过 targeted tests、项目级 gate、运行态 smoke 和必要的真实外部 e2e。
- `next.md` 中 parity 缺口清零；不能清零的项目必须变成显式非目标并有用户确认，不允许用“后续优化”掩盖。
- release/CI 能产出真实 Tau 可执行交付件，且 Windows PowerShell 验证链是权威本地链路。

## 范围

包含：

- 上游 `pi-mono-main` 全包 inventory freeze 和 parity matrix。
- `Tau.Ai` provider、auth、models、secret redaction、runtime config、真实 provider e2e。
- `Tau.Agent` runtime facade、tool contract、event/correlation、共享行为合同。
- `Tau.CodingAgent` CLI/RPC/session tree/commands/tools/extensions/settings/OAuth/TUI integration/share/export/rich rendering。
- `Tau.Tui` terminal host、overlay compositor、input/editor、selector/resource UI、theme/rendering、hardware cursor。
- `Tau.WebUi` Web chat、CodingAgent JSONL branch/tree semantic import、auth/settings UX、artifact/tool rendering、browser smoke。
- `Tau.Mom` Slack、workspace/session sync、sandbox/tools、Docker smoke、multi-message delegation e2e。
- `Tau.Pods` SSH/HF/GPU/vLLM real smoke、setup/deploy/rollback、remote transport hardening、operations e2e。
- scripts/CI/release packaging、docs/next/quality/history/exec-plan 同步。

不包含：

- 改写上游 `pi-mono-main`。
- 为了形式上的一致性把 TypeScript 架构逐字搬进 .NET；Tau 可以采用 .NET-native 抽象，但外部行为必须对齐。
- 未经用户确认就降低 100% 标准，把真实 e2e 或 release 产物改成“以后再做”。

## 背景

相关文档：

- `AGENTS.md`
- `docs/REPO_COLLAB_GUIDE.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

参考源：

- `C:\Users\zhouh\Desktop\pi-mono-main`
- 上游包目录：`packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods`
- 上游 scripts：`scripts/build-binaries.sh`、`scripts/release.mjs`、`scripts/check-browser-smoke.mjs`、`scripts/session-transcripts.ts`、`pi-test.sh`、`test.sh`

Tau 目标模块映射：

- `packages/ai` -> `src/Tau.Ai` / `tests/Tau.Ai.Tests`
- `packages/agent` -> `src/Tau.Agent` / `tests/Tau.Agent.Tests`
- `packages/coding-agent` -> `src/Tau.CodingAgent` / `tests/Tau.CodingAgent.Tests`
- `packages/tui` -> `src/Tau.Tui` / `tests/Tau.Tui.Tests`
- `packages/web-ui` -> `src/Tau.WebUi` / `tests/Tau.WebUi.Tests`
- `packages/mom` -> `src/Tau.Mom` + Mom 相关 `tests/Tau.Agent.Tests`
- `packages/pods` -> `src/Tau.Pods` / `tests/Tau.Pods.Tests`

当前 baseline：

- 已提交并推送 `4be4459 feat: close multi-surface parity baseline`。
- 最近项目级 gate 已通过 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`。
- 最近运行态 gate 已通过 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`，覆盖 WebUi smoke 与 Mom `--once` smoke。
- `next.md` 仍列出真实缺口：provider e2e、SSO registration renewal、完整 CodingAgent/Tui parity、WebUi CodingAgent branch/tree true persistence、real Slack/Docker smoke、real SSH/HF/GPU/vLLM smoke、release/CI 产物等。

## 工作原则

### MAGI 大轮次

每一轮按照固定顺序推进，不跳步：

1. 审视：读取上游源码、Tau 当前实现、测试和文档，产出缺口、问题、风险和验收标准。
2. 执行：按互斥模块边界实现一组可评审切片，主控负责整合。
3. 提升：补验证、history、plan/next/quality，同步下一轮计划并清掉已经过时的缺口。

### 多 Agent 角色

Main Integrator：

- 独占 `docs/**`、`README.md`、`AGENTS.md`、`next.md`、`scripts/**`、solution/project 组织和最终 commit。
- 分配 worker 切片，冻结互斥边界，合并结果，串行执行会写 `bin/obj/output/.tau` 的验证。
- 不直接接受 worker 的“完成”结论，必须用本仓库测试、smoke、diff 和文档状态复核。

Inventory/QA Agent：

- 只读扫描上游与 Tau，维护 parity matrix。
- 输出每个上游文件/命令/协议对应 Tau 状态：`ported`、`partial`、`missing`、`non-goal-proposed`、`verified`。
- 不改业务代码，避免和实现 worker 冲突。

Module Workers：

- `AiWorker`：`src/Tau.Ai/**`、`tests/Tau.Ai.Tests/**`
- `AgentWorker`：`src/Tau.Agent/**`、`tests/Tau.Agent.Tests/**`
- `CodingAgentWorker`：`src/Tau.CodingAgent/**`、`tests/Tau.CodingAgent.Tests/**`
- `TuiWorker`：`src/Tau.Tui/**`、`tests/Tau.Tui.Tests/**`
- `WebUiWorker`：`src/Tau.WebUi/**`、`tests/Tau.WebUi.Tests/**`
- `MomWorker`：`src/Tau.Mom/**`、Mom 相关 `tests/Tau.Agent.Tests/**`
- `PodsWorker`：`src/Tau.Pods/**`、`tests/Tau.Pods.Tests/**`

Docs/History Worker：

- 默认由 Main Integrator 承担。
- 只有在主控明确授权时才改 docs/history；否则 worker 只提交文档建议，不直接编辑共享文档。

### Worker 输出协议

每个 worker 必须输出：

- 读取的上游路径和 Tau 路径。
- 这次实现的行为合同，不用泛泛说“对齐上游”。
- 受影响文件列表。
- targeted validation 命令和结果。
- 剩余缺口列表，必须按 `missing/partial/blocked/external-e2e-needed` 分类。
- 是否触碰 public API、持久化格式、secret/auth、CLI/RPC/HTTP contract 或 runtime log schema。

## 互斥边界

默认互斥：

- Worker 不改 `docs/**`、`next.md`、`README.md`、`AGENTS.md`、`scripts/**`、solution/project 文件，除非 Main Integrator 在该轮显式授权。
- Worker 不跨模块改共享类型；需要共享类型时先在审视阶段提出，Main Integrator 决定归属。
- 不并行运行会写同一 `bin/obj/output/.tau` 的 `dotnet build/test` 或 smoke。
- Windows 本机验证默认使用 PowerShell；`/bin/bash` 缺失只算环境现实，不算 Tau 代码失败。

允许的相邻测试例外：

- `MomWorker` 可改 Mom 相关 `tests/Tau.Agent.Tests/**`。
- `WebUiWorker` 若只通过公共合同消费 CodingAgent JSONL，不改 `src/Tau.CodingAgent/**`；需要改 tree store 时交给 `CodingAgentWorker` 或主控拆分。
- `CodingAgentWorker` 若需要 TUI 组件能力，先把 pure TUI 改动拆给 `TuiWorker`。

## 100% 判定标准

### Inventory 标准

- 每个上游 package 建立 `source file -> Tau target/status/test/e2e/doc` 映射。
- 每个上游命令、CLI flag、RPC method、HTTP endpoint、env var、config key、runtime log event、error code、persisted schema 都有 Tau 状态。
- 上游 scripts 和 release 流程必须映射到 Tau scripts/CI/release；不能只看 runtime 包。

### 行为标准

- public contract 用 tests 固定：CLI output、JSON/RPC/HTTP shape、exit code、error message、runtime event summary、persisted schema migration。
- 高风险路径必须有 targeted test 或 smoke：provider/auth/secret、session tree、tool execution、compaction/retry、Slack/Docker/SSH/HF/vLLM、release packaging。
- 局部纯 UI 文案可以用 build + focused test + smoke 覆盖，但不能替代 public API 合同测试。

### 真实运行标准

- 项目级 gate 通过。
- `-RunSmoke` 通过，并持续扩展到 WebUi/Mom/Pods/CodingAgent 核心路径。
- 有凭证/环境时运行真实 provider e2e、Slack smoke、Docker smoke、SSH/HF/GPU/vLLM smoke。
- 无凭证/环境时，计划必须保留 `external-e2e-needed`，不能标成完成；最终 100% 前要补环境验证或用户明确接受非目标。

### 文档和交付标准

- `next.md` 无未完成 parity 缺口。
- `docs/QUALITY_SCORE.md` 不再把关键模块标为 `C` 级 parity 风险；若仍为 `C`，必须说明风险不影响 100% 移植判定。
- active plan 关闭或明确变成 completed。
- 每个实质变更有 history。
- release artifacts 是 Tau 真实可执行产物，不是占位或旧脚本外壳。

## 阶段计划

### Phase 0：Baseline freeze 与计划接管

目标：

- 固定 `4be4459` 作为当前多面 baseline。
- 新增本计划，作为后续 100% parity 主控 plan。
- 明确旧 active plan 的关系：旧 plan 保留历史上下文，本计划负责最终收口。

任务：

- [x] 提交并推送当前 WIP baseline。
- [x] 在 `next.md` 顶部挂本计划指针。
- [x] 新增 history。
- [x] docs-only 验证并提交本计划。

Exit criteria：

- `git status --short --branch` 干净。
- `origin/main` 包含 baseline commit 和 plan commit。

### Phase 1：上游 inventory freeze

目标：

- 建立完整 parity matrix，避免继续靠零散 `next.md` 条目推进。
- 所有后续 worker 都从 matrix 领任务。

并行 worker：

- `AiWorker` 扫 `packages/ai/src/**`、provider/auth/model/config/test。
- `AgentWorker` 扫 `packages/agent/src/**`、runtime/tool/event facade。
- `CodingAgentWorker` 扫 `packages/coding-agent/src/**`、commands/RPC/session/tree/tools/extensions/settings/export/share。
- `TuiWorker` 扫 `packages/tui/src/**`、terminal/editor/components/rendering/theme。
- `WebUiWorker` 扫 `packages/web-ui/src/**`、dialogs/components/tools/artifacts/session/settings/auth。
- `MomWorker` 扫 `packages/mom/src/**`、Slack/sandbox/tools/events/store/download/docs。
- `PodsWorker` 扫 `packages/pods/src/**`、config/CLI/SSH/HF/vLLM/ops。
- `Inventory/QA Agent` 扫 root scripts、test scripts、release scripts、package manifests。

产物：

- `docs/exec-plans/active/...` 本计划追加 inventory 进度摘要，或由主控新增单独 matrix 文档。
- 每个模块形成 `missing/partial/verified/non-goal-proposed` 列表。
- `next.md` 从自由文本缺口收敛成可执行清单。

验证：

- 只读 inventory 不需要 build。
- 若生成 matrix 文档，运行 `git diff --check`。

Exit criteria：

- 每个上游 package 都有完整映射。
- 没有“未知是否需要移植”的上游目录。

### Phase 2：Critical contract closure

目标：

- 优先关闭会阻塞其他模块的公共合同：provider/auth、session tree、runtime log correlation、tools、持久化 schema、secret redaction。

重点切片：

- `Tau.Ai`
  - provider e2e matrix：OpenAI/Responses/Codex/Azure/Anthropic/Google/Vertex/Gemini CLI/Antigravity/Mistral/Bedrock/OpenAI-compatible。
  - Bedrock SSO OIDC client registration renewal、多 cache/并发刷新边界。
  - dynamic provider/runtime config UX 和非标准 secret pattern 样本扩展。
  - provider request/response/eventstream 的真实云端行为确认。
- `Tau.Agent`
  - 高层 Agent facade 与上游 agent runtime 行为对齐。
  - tool execution cancellation、structured error、runtime event schema 稳定化。
  - correlation context 在 CodingAgent/WebUi/Mom/Pods 的统一继承边界。
- `Tau.CodingAgent`
  - JSONL session tree true contract：branch switching、summary、fork/clone/import/export、metadata、parentSession、labels。
  - RPC public contract：extension UI、bash streaming、full settings parity、command inventory。
  - tool contracts：read/ls/edit/bash/share/export/custom render details。
- `Tau.Tui`
  - terminal host、overlay compositor、selector host、theme token、hardware cursor 的共享 contract。
- `Tau.WebUi`
  - Web session 与 CodingAgent JSONL branch/tree 的真实持久化边界。
  - auth/settings endpoint 与前端状态机 contract。
- `Tau.Mom`
  - Slack event/message/session/tool/status schema。
  - sandbox tool result details 与 attachment manifest contract。
- `Tau.Pods`
  - SSH/HF/vLLM operation result schema、failureKind、rollback state、runtime log schema。

验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
```

附加 targeted tests 由各 worker 按模块执行。主控阶段串行跑项目级 gate。

Exit criteria：

- 所有跨模块 public contract 已测试固定。
- 没有 worker 需要反复修改对方模块的内部类型才能继续。

### Phase 3：Product runtime parity

目标：

- 按产品面把用户真实会碰到的 runtime 行为补齐。

#### CodingAgent + Tui lane

待收口：

- 完整上游 TreeSelector：多选、完整 metadata inspector、branch/session 操作快捷键、search/filter/fold 状态、summary hooks。
- 自动 branch switching summarization hooks、LLM-generated split-turn summarization、compaction extension events 和 cancellation UI。
- retry cancellation UI、auto-retry settings UI、settings selector full parity。
- model selector theme/dynamic-border/terminal-host parity、per-entry thinking UI editor。
- OAuth login dialog/session parity、credential refresh UX。
- TypeScript extension runtime/custom tools/events、theme watcher、resource selector、full diagnostics。
- TUI 主屏：message/status/scrollback 接管真实 terminal host，overlay compositor、hardware cursor、theme rendering、autocomplete popup。
- 完整 Markdown/highlight renderer、custom tool renderer、richer HTML template、Tau share viewer。

验收：

- `Tau.CodingAgent.Tests` 和 `Tau.Tui.Tests` targeted tests 覆盖每条 public behavior。
- 真实 CLI smoke 覆盖 `/session`、`/tree`、`/metadata`、`/fork`、`/resume`、`/settings`、`/model`、`/auth`、`/login` mock flow、`/export`、`--mode rpc`。
- 截屏或 transcript 证据能证明 TUI 主屏、overlay 和 selector 不只是 pure model tests。

#### WebUi lane

待收口：

- CodingAgent JSONL branch/tree true persistence/import，而不只是 conservative timeline import。
- Session list/settings/auth/model/provider dialogs 与上游行为对齐。
- Tool renderer/artifact renderer parity：document/image/html/svg/pdf/text/console/artifacts。
- Attachment、streaming、thinking、tool timeline、error/problem details 的 browser-level regression。
- WebUi release/static asset packaging 与 smoke。

验收：

- `tests/Tau.WebUi.Tests` 包含 service/API/browser 三层回归。
- `scripts/verify-dotnet.ps1 -RunSmoke` 覆盖 WebUi create/send/rename/import/export 的核心路径。

#### Mom lane

待收口：

- real Slack smoke：Socket Mode、app mention、DM、thread response、runtime update/delete、file download、stop/cancel、backfill。
- Slack session sync：跨消息 context、thread/session mapping、same-channel queue、restart recovery。
- Docker sandbox smoke：validate、bash/read/write/edit/attach、path mapping、artifact return。
- Multi-message delegation e2e 和更高层 delegation flow。
- Runtime trace/correlation 与 CodingAgent/Agent tool events 贯通。

验收：

- fake tests 固定 contract，真实 Slack/Docker smoke 固定运行文档和可重复命令。
- 无真实 token 时不能标完成；只能标 `external-e2e-needed`。

#### Pods lane

待收口：

- real SSH smoke：probe/exec/lifecycle/status/logs。
- real HF download smoke：model list/pull/status/remove，revision/snapshot。
- real setup run smoke：SCP script、GPU detect、vLLM version、config writeback。
- real vLLM startup/health smoke：deploy/status/health/stop、prefetch、extra args、readiness。
- 多版本 rollout/rollback 状态机，不止 cleanup fallback。
- Transport hardening：ssh options、timeouts、remote quoting、long-running process cancellation、log redaction。

验收：

- Fake runner tests + real remote smoke 分开记录。
- runtime log 中有 preflight/deploy/health/rollback correlation 摘要。

### Phase 4：External e2e closure

目标：

- 把“只有 fake/stub/contract tests”的外部集成全部跑成真实 e2e，或经用户明确认定为非目标。

E2E matrix：

- AI providers：至少覆盖每个 provider family 的 text stream、tool call/tool result、image/vision where supported、auth refresh/error。
- OAuth：OpenAI Codex、Anthropic、Gemini CLI/Antigravity、GitHub Copilot device flow。
- Bedrock/AWS：SigV4、SSO refresh、registration renewal、AssumeRole/profile/credential_process。
- Slack：Socket Mode receive/ack/respond/update/delete/download/stop。
- Docker：Mom sandbox validate + tool execution。
- Pods：SSH/HF/GPU/vLLM full path。
- WebUi：browser smoke with real static/UI flow。

验证记录：

- 每条 e2e 写清命令、环境变量名称、脱敏结果、日志路径、通过/失败。
- 敏感值只记录存在与来源，不回显。

Exit criteria：

- `external-e2e-needed` 清零。
- 外部服务不可用时，必须有可复现失败证据和用户接受的 non-goal 或延期决策。

### Phase 5：Release、CI 与安装交付 parity

目标：

- Tau 不只是源码可跑，还能以真实交付物发布和验证。

任务：

- 对照上游 `scripts/build-binaries.sh`、`scripts/release.mjs`、root `test.sh` / `pi-test.sh`，建立 Tau 的 build/test/release 等价链。
- `scripts/verify-dotnet.ps1 -RunSmoke` 扩展到 CodingAgent/WebUi/Mom/Pods 最小 runtime smoke。
- bash 验证脚本要么补等价 smoke，要么文档化 Windows 本机 PowerShell 为权威链路。
- 产出真实 Tau 可执行 release artifact，包含 CodingAgent、WebUi、Mom、Pods 必要入口。
- CI 能覆盖 restore/build/test/smoke 的核心矩阵。

Exit criteria：

- release artifact 可在干净目录运行核心命令。
- CI 和本地 PowerShell gate 对同一套 public behavior 给出一致信号。

### Phase 6：Final 100% acceptance

目标：

- 关闭 active plan，证明 Tau 已达到 100% pi-mono parity。

最终验收清单：

- [ ] parity matrix 所有条目为 `verified` 或用户确认 `non-goal`。
- [ ] `next.md` 无未完成 pi-mono parity 缺口。
- [ ] `docs/QUALITY_SCORE.md` 反映最终水位和剩余非 parity 风险。
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过。
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 通过。
- [ ] 真实外部 e2e matrix 通过或有用户确认的非目标决策。
- [ ] release artifact 构建、启动、核心命令 smoke 通过。
- [ ] 所有实质变更都有 `docs/histories/YYYY-MM/**`。
- [ ] 本 plan 移入 `docs/exec-plans/completed/`，旧 active plan 同步关闭或注明历史保留原因。

## 验证方式

Worker 阶段：

```powershell
dotnet build src\<Module>\<Module>.csproj --no-restore --verbosity minimal
dotnet test tests\<Module.Tests>\<Module.Tests>.csproj --no-restore --verbosity minimal
```

主控阶段：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
```

Docs-only 阶段：

```powershell
git diff --check
```

外部 e2e 阶段：

- 使用模块专属 smoke 命令，记录脱敏环境、命令、日志路径和结果。
- 不把缺凭证、缺 Docker、缺 SSH/GPU 机器的本地环境限制误标为代码通过。

## 风险

- 风险：并行 worker 同时改共享模块造成冲突。
  缓解方式：主控冻结互斥路径，共享合同先审视后拆分，不允许 worker 自行跨界改。
- 风险：fake tests 掩盖真实 provider/Slack/Docker/SSH 行为差异。
  缓解方式：Phase 4 把所有 fake-only 条目标成 `external-e2e-needed`，最终 100% 前必须清零。
- 风险：文档声称完成但代码未验证。
  缓解方式：每条 completion 必须带测试/smoke/e2e 证据；history 记录真实验证命令。
- 风险：Windows 文件锁或并行 build 误伤验证。
  缓解方式：worker targeted validation 可分开跑，主控最终 gate 串行跑；必要时 `dotnet build-server shutdown` 后重试一次并记录原因。
- 风险：secret/auth/e2e 日志泄露敏感值。
  缓解方式：只记录字段存在、来源类型和脱敏日志路径；不把 token/JWT/client secret 写进 docs/history。

## Commit 策略

- 每个 phase 可以拆成多个 module-scoped commit，但每个 commit 必须是可评审、可验证的自洽单位。
- 公共合同变更和消费者变更可以同 commit，但必须解释为什么不能拆。
- docs/history/next/quality 由主控在同轮同步。
- 大规模 worker 结果进入主控前先跑 targeted tests；主控合并后跑项目级 gate。
- release/CI 变更单独提交，避免和业务 runtime 行为混在一起。

## 进度记录

- [x] Phase 0：baseline commit 已推送到 `origin/main`。
- [x] Phase 0：本 100% parity plan 提交并推送。
- [~] Phase 1：上游 inventory freeze（matrix 已创建并合并 capability-level inventory；Agent package 已补 `agent.ts` / `agent-loop.ts` / `types.ts` / `proxy.ts` / `index.ts` file-level mapping；其它 package 的 file-level mapping、command/API/env/config/log/schema submatrices 仍待冻结）。
- [~] Phase 2：critical contract closure（Agent stream proxy 已落地；Agent facade baseline 已落地，覆盖 `AgentOptions`、`Agent`、prompt/continue/subscribe/queue/wait/reset 和 prompt/tool-result message lifecycle，并通过 `Tau.Agent.Tests`；Agent loop event payload、tool update/schema/error/cancel 仍待继续闭环）。
- [ ] Phase 3：product runtime parity。
- [ ] Phase 4：external e2e closure。
- [ ] Phase 5：release/CI/install delivery parity。
- [ ] Phase 6：final 100% acceptance。

## 决策记录

- 2026-05-28：把 100% 移植率定义为 inventory + behavior tests + real e2e + release artifact + docs/history closure，不再用单一百分比或主观进度声明。
- 2026-05-28：本计划作为最终收口 plan；旧 `2026-05-10-tau-complete-pi-mono-port.md` 和 `2026-05-20-coding-agent-parity-gap-analysis.md` 保留历史和局部上下文，但后续最终验收以本计划为准。
- 2026-05-28：多 Agent 默认按上游 package 与 Tau 模块一一映射并保持互斥文件边界；Main Integrator 独占共享 docs/scripts/solution 和最终验证。
- 2026-05-28：部分 explorer 因额度或服务错误中断，Phase 1 不等待外部 agent 恢复；主控以本地扫描和已完成的 Mom explorer 输出补齐 capability-level matrix，后续继续扩成 file-level inventory。
- 2026-05-28：Phase 2 从 `packages/agent/src/proxy.ts` 对齐开始，新增 Tau-native `ProxyStreamProvider`，先关闭明确 missing 的 Agent stream proxy baseline，真实 proxy-server e2e 仍保留为后续验证项。
- 2026-05-28：继续 Phase 2 Agent lane，新增 Tau-native high-level `Agent` facade；先关闭上游 `agent.ts` 的 prompt/continue/listener/queue/wait/reset baseline，不把它标为完整 Agent loop parity，后续继续收口 `agent-loop.ts` 的 event payload、tool update、schema validation 和 failure/cancel 语义。
