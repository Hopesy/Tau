# Tau 100% pi-mono parity 多 Agent 移植计划

> 当前优先级说明（2026-06-07）：本计划恢复为当前优先执行线。`docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md` 已完成 Tau Agent 应用底座本地验收，现在作为 Phase 2 shared Agent foundation 和后续产品移植前置能力；它不代表 100% pi-mono product parity 完成。后续推进从 `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 的 `Phase 2 Candidate Queue` 分派 implementation/e2e worker 切片，不重开 broad inventory。

## 目标

把 Tau 从当前多面 baseline 推进到 `C:\Users\zhouh\Desktop\pi-mono-main` 的 100% 可审计移植状态。这里的 100% 不按聊天里的主观完成度计算，而按上游 inventory、行为合同、真实运行验证、发布产物和文档闭环共同判定：

- 上游 `packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods` 的用户可见功能、协议、命令、配置、环境变量、错误语义、运行日志和发布脚本都有 Tau 对应实现、测试或经用户确认的非目标说明。
- Tau 对应模块在 `.NET` 实现里通过 targeted tests、项目级 gate、运行态 smoke 和必要的真实外部 e2e。
- `next.md` 中 parity 缺口清零；不能清零的项目必须变成显式非目标并有用户确认，不允许用“后续优化”掩盖。
- release/CI 能产出真实 Tau 可执行交付件，且 Windows PowerShell 验证链是权威本地链路。

## 当前 checkpoint（2026-06-07）

- Agent platform baseline 已完成并归档：`src/Tau.Agent/Platform/**`、Console/HTTP examples、platform smoke、provider run + tool execution runtime log、全仓 `verify-dotnet.ps1 -SkipRestore` 与 `-RunSmoke` 本地验收已有当前证据。该能力降低后续 Agent/WebUi/Mom/CodingAgent host 类切片风险，但不关闭真实 provider/OAuth/e2e、release registry/signing/provenance 或完整 product parity。
- Phase 1 inventory freeze 已完成：matrix 已冻结 capability、file-level、surface、root scripts/manifests 三层 mapping；上游 package directory set 已确认，没有未知 package 目录。后续实现只在具体切片中补充 finer rows，不重新做 broad inventory。
- 当前旧 dirty WIP 边界已经关闭：Agent platform baseline、CodingAgent `/settings select` `TuiSettingsList` adoption、100% parity goal restore 和 100% gap-map/acceptance-plan docs 都已作为独立 checkpoint 提交并推送；继续前仍必须重新读取 `git status --short --branch` 和当前 diff。
- 当前恢复路线：先执行 `GOAL.md` Pass 0 状态校准，清理 matrix/next/quality/plan 中与最新 HEAD 证据不一致的 stale 描述；然后再从 matrix `Phase 2 Candidate Queue` 选择高价值 implementation/e2e 切片。优先级按阻塞程度排列：critical contracts、真实外部 e2e、product runtime parity、release/package final parity。

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
- [x] Phase 1：上游 inventory freeze（matrix 已创建并合并 capability-level inventory；AI、Agent、CodingAgent、Tui、WebUi、Mom、Pods 的 grouped file-level mapping 均已冻结；AI、CodingAgent、Tui、WebUi、Mom、Pods 的 command/API/env/config/log/schema 或对应 surface 子矩阵均已冻结；root script/manifest matrix 已冻结；final normalization pass 已完成，Phase 2 candidate queue 已落在 matrix，当前上游 package 目录集合确认无未知目录）。
- [~] Phase 2：critical contract closure（Agent stream proxy 已落地；Agent facade baseline 已落地，覆盖 `AgentOptions`、`Agent`、prompt/continue/subscribe/queue/wait/reset 和 prompt/tool-result message lifecycle，并通过 `Tau.Agent.Tests`；Agent loop 这轮已补上 `agent_end.messages`、`turn_end.message/toolResults`、tool update partial result、schema validation、error-as-tool-result baseline、facade/runtime/proxy/event public API compile-sample、high-level `Agent` facade 的 stream failure / cancellation synthetic assistant message baseline、low-level `AgentRuntime.RunStream(...)` / `EventStream<AgentEvent, ChatMessage[]>` wrapper、parallel tool start/update timing、assistant stream cancellation terminal event、aborted `ErrorEvent` preservation、tool cancellation cleanup、parallel sibling cancellation result preservation 和 `TransformContext` cancellation；AI-level `ToolArgumentValidator` public helper baseline 已上移到 `Tau.Ai` 并由 Agent 复用；`AiPublicApiCompileSampleTests` 已冻结当前 Tau.Ai .NET public surface；本轮又补 `AiHeaderUtilities`、`ShortHash`、`ContextOverflowDetector`、`JsonSchemaHelpers.StringEnum`、`StreamingJsonParser` public helper baseline、`Tau.Ai.Providers.Faux` public scripted provider baseline，并让 CodingAgent overflow retry 分类复用 AI helper；OpenAI-style / OpenAI Responses shared / Anthropic / Mistral / Bedrock streaming tool-call arguments 已接入 `StreamingJsonParser` object raw-text helper baseline；`Tau.Ai.Cli` 已补 standalone `tau-ai` help/list/login 基线与 `--auth-file auth.json` cwd auth 兼容入口；`ProviderAuthResolver` 的 models.json status 检查已改为可注入 `ModelConfigurationStore`，移除测试对进程级 `TAU_MODELS_FILE` 的文件锁敏感依赖；Phase 2 首批候选已从 matrix 抽取，下一步按 owner/validation gate 分派 AI TypeBox/export/release bin alias 决策、AI provider e2e/edge audit、CodingAgent CLI/RPC/image、Tui terminal、WebUi branch-tree/artifact、Mom Slack/Docker、Pods command/e2e、release/no-env artifact 切片；剩余 Agent gap 现在转为 facade option pass-through、public export 形状决策和真实 proxy/e2e 验证）。
- [~] 2026-06-07：继续 Phase 2 `Pods command/config/known-model compatibility`。对照上游 `packages/pods/src/models.json`、`model-configs.ts` 和 `commands/models.ts`，Tau 现在随 `Tau.Pods` 输出复制 upstream `models.json`，新增 `PodKnownModelRegistry`，并让 `PodVllmCommandPlanner` 在没有显式 `--vllm` extra args 时按 GPU type/count 选择 known-model args/env，写入 serve command、metadata、CLI JSON/text 输出和 `vllm plan/deploy` 计划；显式 `--vllm` 仍覆盖自动配置。已通过 `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal`、候选 gate 101/101、完整 `Tau.Pods.Tests` 174/174 和项目级 `verify-dotnet.ps1 -SkipRestore`。该切片只关闭本地 known-model config planning baseline，仍不关闭 upstream round-robin GPU allocation、`--gpus` / `--memory` / `--context` convenience flags、直接 `start/list/logs` PID/log-tail 流程、真实 SSH/HF/setup/GPU/vLLM smoke 或 Pods final `verified` 状态。
- [~] 2026-06-07：继续 Phase 2 `Pods command/config/known-model compatibility` 的本地 option planning 子切片。对照上游 `commands/models.ts` 的 `--gpus` / `--memory` / `--context` 语义，Tau 现在让 `vllm plan/deploy` 在非显式 `--vllm` 路径支持 requested GPU count、known-model 指定 GPU config、基于 `PodDefinition.Gpus` 的本地 selected GPU ids、单 GPU `CUDA_VISIBLE_DEVICES`、`--gpu-memory-utilization` 和 `--max-model-len` transform，并把这些字段写入 text/JSON plan 与 metadata；显式 `--vllm` 继续覆盖 known-model/GPU/memory/context 自动规划，unknown model 使用 `--gpus` 会报错。已通过 Pods build、候选 gate 108/108、完整 `Tau.Pods.Tests` 181/181 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 181）。该切片只关闭本地 option planning baseline；usage-aware GPU allocation state 已由后续独立子切片关闭本地合同，直接 `start/stop/list/logs` PID/log-tail 流程、真实 SSH/HF/setup/GPU/vLLM smoke 和 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods config path/env compatibility`。第一刀对照上游 `packages/pods/src/config.ts`，先让 Tau 未显式传 config path 时支持 `PI_CONFIG_DIR` 并默认读取/写入 `<PI_CONFIG_DIR>/pods.json`，显式 `--config path` 和旧 positional config path 继续优先于 env default；随后下一条子切片已继续关闭 no-env default 与 missing-file 语义，因此当前状态以后续条目、matrix 和 `GOAL.md` 审计快照为准。该第一刀的意义是固定 env override / explicit precedence，不单独代表当前最终 config path/env 状态。
- [~] 2026-06-08：继续 Phase 2 `Pods config path/env compatibility` 的 no-env default / missing-file 子切片。对照上游 `packages/pods/src/config.ts`，Tau 现在未显式传 config path 时会读取/写入 `PI_CONFIG_DIR/pods.json` 或无 env 默认 `~/.pi/pods.json`；`PodsConfigStore.Load` 缺文件返回空 `PodsConfig`，因此配置列表入口 `pods --json` 与 `validate --json` 不再报 `Config not found`，并新增 isolated `PI_CONFIG_DIR` 回归避免测试污染真实用户 home。已通过 `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal`、focused `PodsCli|Config` gate 92/92、完整 `Tau.Pods.Tests` 189/189 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 189）。该切片关闭 config path/env 与 missing-file 子合同；record-shaped config/schema 当时仍 open，后续已由独立子切片关闭；真实 remote e2e 和 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods config path/env compatibility` 的 record-shaped schema 子切片。Tau 现在能读取上游 `pods: Record<string, Pod>` / `models: Record<string, Model>` config，保留 `ssh` command、GPU inventory、models state、modelsPath 和 vLLM version；`Save` 写回同一上游 record shape，并继续兼容旧 Tau list-shaped config load 以支持迁移。已通过完整 `Tau.Pods.Tests` 192/192；该切片关闭本地 config shape/schema migration baseline，usage-aware GPU allocation state 已由后续独立子切片关闭本地合同，真实 remote e2e 和 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods command compatibility` top-level shim 子切片。对照上游 `packages/pods/src/cli.ts`，Tau 现在新增上游式 `pods` group，`pods` / `pods setup` / `pods active` / `pods remove` 分别映射到配置列表、注册、active 和 remove；top-level `start <model> --name <name>` 映射到 `vllm deploy` 并强制上游 `--name` 要求；top-level `list` 映射到 active/指定 pod 的 `deployments`；top-level `stop <name>` 映射到 `vllm stop`，旧 Tau-native `deployments` / `vllm` 和可消歧旧 stop 入口保留。已通过 focused `PodsCli` gate 85/85 和完整 `Tau.Pods.Tests` 197/197。该切片只关闭本地 top-level shim 合同；`shell`、`agent` / prompt mapping、no-arg stop-all 和 usage-aware GPU allocation state 已由后续子切片关闭本地合同，上游本地 PID/log-tail/startup streaming exact flow、真实 SSH/HF/GPU/vLLM smoke 和 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods command compatibility` 的 no-arg stop-all 子切片。对照上游 `pi stop [<name>]`，Tau 现在让 top-level `stop [--json] [--config path] [--pod id]` 在没有 deployment name 时先列出 active/指定 pod 的 deployments，再逐个复用现有 `vllm stop` 停止，并输出 stop-all 聚合 JSON/text；带 deployment name 的 `stop <name>` 和旧 Tau-native positional stop 继续保持兼容。已通过 focused `PodsCli` gate 86/86 和完整 `Tau.Pods.Tests` 198/198。该切片关闭 no-arg stop-all 的本地命令合同；`shell`、`agent` / prompt mapping 和 usage-aware GPU allocation state 已由后续子切片关闭本地合同，上游本地 PID/log-tail/startup streaming exact flow、真实 SSH/HF/GPU/vLLM smoke 和 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods command compatibility` 的 `shell` / `agent` prompt-mapping 子切片。对照上游 `cli.ts` 的 `pi shell [<name>]` 与 `commands/prompt.ts`，Tau 现在新增 top-level `shell [--json] [--config path] [--pod id] [pod-id]`，解析 active/指定 pod 后通过 `PodExecService.OpenShellAsync` 构造无远端命令的 SSH 进程；同时新增 `agent [--config path] [--pod id] <name> [message/options...]`，读取 `pod.models[modelName]` configured model state，构造上游式 `--base-url`、`--model`、redacted `--api-key`、`--api`、`--system-prompt` 与 passthrough user args，并保持稳定 not-implemented failure。已通过 focused `PodsCli` gate 89/89、完整 `Tau.Pods.Tests` 201/201 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 201）。该切片关闭 shell 入口和 agent prompt argument mapping baseline；可用 pod-agent runtime、上游本地 PID/log-tail/startup streaming exact flow、usage-aware GPU allocation state、真实 SSH/HF/GPU/vLLM smoke 或 Pods final `verified` 状态仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods GPU allocation state` 子切片。对照上游 `packages/pods/src/commands/models.ts` 的 `selectGPUs`、`startModel` 和 `stopModel`，`PodVllmCommandPlanner` 现在会从 `pod.Models[*].Gpu` 统计占用次数，按最少使用 GPU 选择 `vllm plan/deploy` 与 top-level `start` 的 `selectedGpus`；请求全部 GPU 时保持 inventory 顺序。成功 `vllm deploy` / top-level `start` 会把 deployment name、model id、port 和 selected GPU ids 写回 configured model state；成功 `vllm stop` / top-level `stop` / no-arg stop-all 会删除对应 state，stop-all 只删除成功停止的 deployment。当前已通过 focused `PodVllmCommandPlannerTests|VllmDeploy|Stop_WithDeploymentName|Stop_WithoutDeploymentName` gate 29/29、完整 `Tau.Pods.Tests` 204/204 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 204）。该切片只关闭本地 usage-aware GPU allocation state baseline；PID 仍以 `0` 占位，exact upstream PID/log-tail/startup streaming、可用 pod-agent runtime、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods remote process state` 子切片。对照上游 `commands/models.ts` 成功启动后保存 `pid` 的本地配置合同，`PodVllmOrchestrationService` 现在会让 systemd 路径 best-effort 查询 `MainPID`、fallback `nohup` 路径回显 `.pid` 文件中的进程号，并把 deploy stdout 中的 `pid=<n>` 解析到 `PodVllmOperationResult.ProcessId`；`PodsCli` JSON 输出新增 `processId`，`PodsConfigStore.ApplyVllmDeploymentResult` 在有正整数 PID 时写入 configured model `pid`，否则继续写 `0`。当前已通过 focused `DeployAsync_ExecutesPlannerRemoteCommandThroughSshRunner|VllmDeploy_Success_UpdatesConfiguredModelStateForGpuAllocation` gate 2/2、完整 `Tau.Pods.Tests` 204/204 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 204）；`git diff --check` 也已通过。该切片只关闭本地 deploy PID state writeback baseline；exact upstream `model_run.sh` wrapper、`~/.vllm_logs` startup streaming、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods remote process/log schema` 子切片。对照上游 `commands/models.ts` / `model_run.sh` 的 `~/.vllm_logs/<name>.log` 路径合同，`PodVllmCommandPlanner` 现在在 plan/metadata JSON 暴露 `logPath`，systemd unit 通过 `StandardOutput/StandardError=append:%h/.vllm_logs/<name>.log` 写输出，fallback `nohup` 也写 `~/.vllm_logs/<name>.log`，top-level `logs` 先 tail 该上游路径再回退 journal/旧 `.tau_pods` 日志。当前已通过 Pods build、focused `PodVllmCommandPlannerTests|DeployAsync_ExecutesPlannerRemoteCommandThroughSshRunner|Logs_WithoutPodId_UsesActivePodAndEmitsJsonFailureKind|Logs_WithConfigPodOptions_UsesExplicitPodAndTail|VllmPlan_WithJsonOption_PrintsMachineReadablePlan|VllmDeploy_Success_UpdatesConfiguredModelStateForGpuAllocation` gate 19/19、完整 `Tau.Pods.Tests` 204/204 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 204）；`git diff --check` 也已通过。该切片只关闭本地 upstream log path baseline；exact `model_run.sh` wrapper、实时 `tail -f` startup watcher、startup complete/failure line parsing、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍保持 open。
- [~] 2026-06-08：继续 Phase 2 `Pods startup log marker` 子切片。对照上游 `startModel` 的 log watcher marker，`vllm health/status` 现在在 curl `/health` 不 ready 后优先扫描 `~/.vllm_logs/<name>.log` 最近日志，识别 `Application startup complete` 为 ready，识别 `Model runner exiting with code`、`Script exited with code`、OOM 和 engine initialization failure 为 unhealthy，并保留旧 `.tau_pods` 日志 fallback；`ParseState` 也能从 raw startup marker 映射状态。当前已通过 Pods build、focused `StatusAsync_BuildsStatusCommandAndParsesState|StatusAsync_ParsesReadyAndUnhealthyOutput|HealthAsync_BuildsHealthProbeCommandAndRequiresReady|HealthAsync_ParsesStartupLogMarkers|VllmHealth_WithJsonOutput_PrintsReadyContract|VllmHealth_WithTextOutput_ReturnsNonZeroForUnhealthy|VllmDeploy_WhenHealthFails_PrintsRollbackInJson|VllmDeploy_WithHealthRetryOptions_RetriesUntilReadyAndPrintsJsonAttempts` gate 7/7、完整 `Tau.Pods.Tests` 205/205 和项目级 `verify-dotnet.ps1 -SkipRestore`（Ai 280、Agent 119、Tui 251、CodingAgent 438、WebUi 44、Pods 205）；`git diff --check` 也已通过。该切片只关闭本地 startup marker 判定 baseline；实时 `tail -f` 流式监控、失败后 config 自动删除、exact `model_run.sh` wrapper、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍保持 open。
- [ ] Phase 3：product runtime parity。
- [ ] Phase 4：external e2e closure。
- [~] Phase 5：release/CI/install delivery parity（current-RID artifact baseline 已落地：`scripts/build-release-artifacts.ps1` 会 publish `pi`、`tau-ai` / `pi-ai`、`mom`、`pi-pods`、`tau-web-ui` 到 `artifacts/tau-<rid>/`，生成 wrapper 与 `manifest.json`；release artifact 会复制 `README.md`、`LICENSE` 和完整当前 `docs/`，并在 `manifest.json.releasePayload` 审计上游 `build-binaries.sh` payload 清单，其中 `changelog` 记录为 Tau-native release notes，`package-json` 记录为 Tau-native manifest，`theme` / `export-html` 记录为 inline 实现，`examples` / Photon wasm / interactive assets 仍记录为 missing，koffi 记录为 not-applicable；`Directory.Build.props` 现在定义 Tau 产品 `VersionPrefix=0.1.0`，release `manifest.json` 会记录同一版本和来源，`scripts/smoke-release-artifacts.ps1` 会验证 manifest 版本与 MSBuild 源一致；`scripts/smoke-release-artifacts.ps1` 也会在 artifact 目录验证 payload manifest、release notes、AI CLI list、`pi` RPC `get_state`、Pods help、WebUi HTTP API 和 Mom `--once`。PowerShell no-env wrapper baseline 也已落地：`scripts/invoke-no-env.ps1` 只对子进程清除 provider/AWS/Azure/HF/Slack/Tau auth/config env，并可把 Tau auth/models/session/settings/log 指到临时目录；`scripts/verify-no-env.ps1` 会在该隔离环境运行项目验证并可加 `-RunSmoke` 覆盖 `tau-ai list` 与 CodingAgent RPC `get_state`。本轮新增 `scripts/pi-test.ps1`，把上游 `pi-test.sh --no-env` 的“直达 CodingAgent CLI + 可选 no-env”职责落成 PowerShell wrapper，`verify-no-env.ps1 -RunSmoke` 的 CodingAgent RPC smoke 已改由它执行。当前又扩展 `scripts/package-release-artifacts.ps1` 为 `auto|zip|tar.gz` 格式感知 archive builder：Windows RID 输出 zip，Linux/macOS RID 输出 tar.gz，统一 clean temp extraction 结构校验，宿主 RID 默认运行 executable smoke，非宿主 RID 默认不误跑本机 smoke；新增 `scripts/package-release-matrix.ps1` 批量归档已存在 artifact，新增 `scripts/build-release-matrix.ps1` 按 `osx-arm64/osx-x64/linux-x64/linux-arm64/win-x64` 逐个 restore/build/package。现在新增 `.github/workflows/tau-ci.yml`，让 GitHub Actions Windows runner 复用同一 no-env smoke、Release artifact build、archive package/extract smoke、tar.gz format structure smoke，并上传 `tau-win-x64.zip` workflow artifact。`scripts/plan-release.ps1` 现在提供上游 `release.mjs` 的 dry-run planning baseline：检查 clean worktree、release notes/scripts、从 MSBuild version source 计算 bump 或 explicit semver、列出 guarded release preparation preview、guarded release validation preview、local release execution preview、guarded release finalization preview、guarded package publish preview、version update preview、release notes update preview、release contract smoke、release finalize smoke、release package publish smoke、session audit script smoke、CodingAgent auth migration smoke、CodingAgent session migration smoke、CodingAgent commands migration smoke、CodingAgent tools-to-bin migration smoke、CodingAgent deprecated extension dirs audit smoke、edit tool stats smoke、Mom timestamp migration smoke、CodingAgent startup profile smoke、release version sync smoke、no-env gate 和 release matrix build/package 命令，并明确 planning 本身不执行版本写入、release notes 修改、commit、tag、GitHub Release 创建、package registry publish 或 push；`scripts/execute-release.ps1` 现在提供受控本地 release execution：默认 dry-run，显式 `-Apply` 且起始工作树干净时才运行 contract smoke、`prepare-release.ps1 -Apply`、可选 `validate-release.ps1 -Run -AllowDirty`，然后只 stage `Directory.Build.props` 与 `docs/releases/feature-release-notes.md`、创建 `Release v<version>` commit 并打 `v<version>` tag；它会阻断已有 tag、dirty worktree 和 prepare 后的额外文件变更，仍不 publish、不 push，也不生成上游第二个 `[Unreleased]` commit，因为 Tau release notes 是日期表格；`scripts/finalize-release.ps1` 现在提供受控 release finalization：默认 dry-run，显式 `-Apply` 且通过 tag 格式、git、clean worktree、remote、branch、local tag、tag 指向 branch tip、release archive 存在等预检后才 push branch/tag；`-CreateGitHubRelease` 可上传已验证 archive，`-Draft` / `-Prerelease` 在 dry-run 与 fake-gh apply smoke 中都固定为对应 GitHub CLI flag；`scripts/verify-release-finalize.ps1` 使用临时 Git 仓库、临时 bare remote、fake archive 和 fake `gh.cmd` 固定 dry-run JSON、branch/tag push、GitHub Release create 参数、dirty apply 阻断和 draft/prerelease flag 一致性；`scripts/publish-release-packages.ps1` 现在提供受控 package registry publish synchronization：默认 dry-run，显式 `-Apply` 才执行 `dotnet pack` 与 `dotnet nuget push`，默认只打包/推送 `Tau.Ai`、`Tau.Agent`、`Tau.Tui` 库包，应用项目仍默认走 release archives，API key 只从 `-ApiKeyEnv` 指定 env 读取且 JSON/command 输出不回显密钥；`scripts/verify-release-package-publish.ps1` 使用临时 Git 仓库、临时 library/app project fixture 和 fake `dotnet.ps1` 固定默认库包边界、dry-run JSON、pack/push 参数、API key redaction、应用项目 warning 与 dirty apply 阻断；`scripts/prepare-release.ps1` 可在显式 `-Apply` 且工作树干净时组合 version 与 release notes 写回，并在写入前先跑两个 helper dry-run 预检；`scripts/validate-release.ps1` 可在显式 `-Run` 且工作树干净时串行执行 `git diff --check`、no-env gate 和 release matrix build/package，现在 JSON 输出还会记录 `validationLevel`、enabled/skipped validation count 与 validation name 列表，跳过 no-env+matrix 时明确标记 `minimal-diff-only` 并给 coverage warning；`scripts/verify-release-contracts.ps1` 现在用短 dry-run JSON smoke 固定 plan/prepare/validate/execute/finalize/package publish 的 release contract，并已接入 CI 的长 no-env/build 前置步骤；`scripts/export-session-transcripts.ps1` / `scripts/report-session-costs.ps1` 对照上游 root session audit utilities 提供 Tau JSONL transcript export 与 persisted cost/token report，`scripts/verify-session-audit-scripts.ps1` 用临时 JSONL fixture 固定主路径；`scripts/migrate-coding-agent-sessions.ps1` / `scripts/verify-coding-agent-session-migration.ps1` 对照上游 CodingAgent misplaced root JSONL session helper，提供 Tau-native 可发现的 `coding-agent-sessions/<encoded-cwd>/` relocation 与 fixture smoke；`scripts/migrate-coding-agent-auth.ps1` / `scripts/verify-coding-agent-auth-migration.ps1` 对照上游 `migrateAuthToAuthJson()` 提供 legacy `oauth.json` + `settings.json.apiKeys` -> `auth.json` dry-run/apply helper 与 fixture smoke；`scripts/migrate-coding-agent-commands.ps1` / `scripts/verify-coding-agent-commands-migration.ps1` 对照上游 `migrations.ts` 的 `commands/` -> `prompts/` 迁移，提供 base directory dry-run/apply rename helper 与 fixture smoke；`scripts/migrate-coding-agent-tools-to-bin.ps1` / `scripts/verify-coding-agent-tools-to-bin-migration.ps1` 对照上游 `migrations.ts` 的 managed `fd`/`rg` `tools/` -> `bin/` 迁移，提供 agent directory dry-run/apply helper、target-exists old-source removal 和 fixture smoke；`scripts/audit-coding-agent-deprecated-extension-dirs.ps1` / `scripts/verify-coding-agent-deprecated-extension-dirs-audit.ps1` 对照上游 `checkDeprecatedExtensionDirs(...)` 提供 deprecated `hooks/` 与 custom `tools/` warning audit，不迁移或删除任何文件，并固定 managed fd/rg 与 hidden entry ignore 行为；`scripts/report-edit-tool-stats.ps1` / `scripts/verify-edit-tool-stats.ps1` 对照上游 root edit stats utility 提供 Tau JSONL edit tool call/result 统计、上下文 inflation 与 fixture smoke；`scripts/migrate-mom-timestamps.ps1` / `scripts/verify-mom-timestamp-migration.ps1` 对照上游 Mom timestamp migration helper 提供 Tau channel log timestamp 迁移与 fixture smoke；`scripts/profile-coding-agent-startup.ps1` / `scripts/verify-coding-agent-startup-profile.ps1` 对照上游 root startup profiler 提供 RPC `get_state` startup benchmark 与 CI smoke；`scripts/sync-release-versions.ps1` / `scripts/verify-release-version-sync.ps1` 对照上游 workspace version sync 提供 Tau MSBuild 单源版本漂移审计和 fixture smoke；`scripts/update-release-version.ps1` 可在显式 `-Apply` 时只写回 MSBuild version source；`scripts/update-release-notes.ps1` 可在显式 `-Apply` 时只写回 `docs/releases/feature-release-notes.md` 表格行。仍缺非宿主平台 executable smoke、真实 NuGet/package registry 发布演练、真实 package signing/provenance rehearsal、真实远端发布演练、session transcript analyze 子 agent 模式、CodingAgent usage cost 持久化、full settings/runtime auth parity、credential refresh UX、actual hooks/custom tools migration to extensions、general custom tool migration/runtime parity、edit tool runtime/e2e 继续收口、TUI first-frame startup profiling/CPU profile、examples/Photon/interactive raster assets/external export-html payload parity、exact Unix wrapper/auth-backup parity 与真实外部 e2e release smoke）。
- [ ] Phase 6：final 100% acceptance。

## 决策记录

- 2026-06-08：`Tau.Pods` config path/env 的第一刀限定为 `PI_CONFIG_DIR` env override，用来先固定可验证的上游 env override 和显式 config precedence；第二刀已继续把 Tau 默认配置从 `tau.pods.json` 切到 `~/.pi/pods.json` 并关闭 missing config 空对象语义。record-shaped schema migration 后续已按独立切片收口，保持 path/env、missing-file 和 schema 迁移可分开评审。
- 2026-06-08：第二刀把 `Tau.Pods` 默认配置从 Tau-local `tau.pods.json` 切到上游 `~/.pi/pods.json`，并把 missing-file load 语义改为上游空 config。测试侧不写真实用户 home，而是用 isolated `PI_CONFIG_DIR` 覆盖无显式路径命令；record-shaped schema migration 后续已单独关闭，避免把 path/env、missing-file 和 schema 迁移混成一个不可审查提交。
- 2026-06-08：record-shaped schema 子切片只改变 `PodsConfigStore` 的外部配置读写合同：load 支持上游 record shape 与旧 Tau list shape，save 输出上游 record shape。内部继续使用 `PodsConfig.Pods` list 和 typed result records，避免让运行中服务、CLI 命令和测试一次性承担 schema 重排。
- 2026-06-07：继续 CodingAgent `/settings select` product-level SettingsList adoption，对照上游 `packages/coding-agent/src/modes/interactive/components/settings-selector.ts` 与 `packages/tui/src/components/settings-list.ts`，把生产 settings selector 从 Tau-native 7 项 `TuiSelectList` 主菜单切到 `TuiSettingsList` 主设置面。当前可直接写回 auto-compaction、terminal show images / clear on shrink、image auto-resize / block images、show hardware cursor、editor padding、autocomplete max visible、steering/follow-up mode、tree filter、thinking level、quiet startup、collapse changelog、install telemetry，并从同一列表进入 scoped models / theme 既有子 selector；同时新增 `TuiSettingsListSession` 和 composition overlay runner，保留旧 action-id selector payload 兼容路径。已通过 focused settings selector/router 验证 9/9、`Tau.Tui.Tests` 251/251、`Tau.CodingAgent.Tests` 438/438；完整 settings submenus、package/transport/shell path/npm settings、部分 persisted terminal/editor/image/changelog 字段真实 runtime wiring、完整 focus stack/theme rendering 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui settings-list component parity，对照上游 `packages/tui/src/components/settings-list.ts` 新增 `TuiSettingsList`、`TuiSettingItem`、`TuiSettingsListTheme` 和 `TuiSettingsListOptions`。Tau 版本覆盖 label/value 对齐、description wrapping、可选搜索、Enter/Space value cycle、submenu delegate / done callback、Esc/Ctrl-C cancel、滚动提示和宽度截断；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 249/249；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 249、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 `components/settings-list.ts` 的库层 foundation；完整 CodingAgent settings 产品面、images/terminal/transport/packages 等全量配置项、完整 focus stack、theme rendering 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui box component parity，对照上游 `packages/tui/src/components/box.ts` 扩展 `TuiBox`。Tau 版本覆盖 child rendering、水平/垂直 padding、optional background formatter、`SetBackgroundFormatter(...)` 运行期切换，以及上游同款 bg-sample/child-line cache invalidation；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 244/244；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 244、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 `components/text.ts` / `truncated-text.ts` / `box.ts` / `spacer.ts` 组件组的本地库层 baseline；完整 TUI host/focus stack、theme rendering、硬件 cursor 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui text/truncated-text component parity，对照上游 `packages/tui/src/components/text.ts` 与 `components/truncated-text.ts` 扩展 `TuiTextBlock` 并新增 `TuiTruncatedText`。Tau 版本覆盖 Text 的空白文本不渲染、tab-normalized wrap、水平/垂直 padding、full-line background formatter、`SetCustomBackgroundFormatter(...)` cache invalidation，以及 TruncatedText 的首行截断、padding、空文本仍输出 padded line 和 width-aware ellipsis；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 243/243；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 243、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 `text.ts` / `truncated-text.ts` 的库层组件 baseline；`box.ts` custom background/cache、完整 TUI host/focus stack、theme rendering、硬件 cursor 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Phase 5 package signing/provenance hardening baseline。上游 `package.json` 只有 `npm run publish` / `npm publish -ws --access public`，没有显式 signing/provenance 脚本；Tau 采用 .NET-native release hardening：新增 `scripts/generate-release-provenance.ps1` 记录 release archives / NuGet packages 的 git/version/path/size/SHA256 manifest，新增 `scripts/sign-release-packages.ps1` 以 dry-run-first 方式预览/执行 `dotnet nuget sign`，并新增 `scripts/verify-release-provenance.ps1` 用临时 fixture 与 fake dotnet 固定 provenance JSON、dirty apply 阻断、签名命令形状、证书路径/指纹入口、timestamp 参数和证书密码脱敏。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `release-provenance-smoke`。本切片关闭本地 provenance manifest 与 NuGet package signing guarded smoke baseline；真实 code-signing certificate rehearsal、真实 package registry publish、release archive signing、外部供应链 attestation、非宿主 executable smoke 和真实 release e2e 仍保留为 Phase 5 未完成项。
- 2026-06-06：继续 root / AI test utility parity，对照上游 `packages/ai/scripts/generate-test-image.ts` 新增 `scripts/generate-ai-test-image.ps1` 与 `scripts/verify-ai-test-image.ps1`。Tau 版本生成 200x200 白底红圆 PNG fixture，默认输出 `tests/Tau.Ai.Tests/Data/red-circle.png`，也支持临时 `-OutputPath`；实现采用纯 PowerShell PNG chunk + zlib stored block 写入，不引入 node-canvas / System.Drawing 依赖。fixture smoke 会解析 PNG signature/chunks、IHDR、zlib/adler32、scanline filter、中心/边缘/背景像素和 SHA256 输出。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `ai-test-image-smoke`。本切片关闭 AI test image generator 的 Tau-native 本地 smoke baseline；broader image/clipboard ingestion、provider vision e2e 和 canvas antialias byte-for-byte parity 仍不是本切片完成项。
- 2026-06-06：继续 Tau.Tui loader/cancellable-loader foundation，对照上游 `packages/tui/src/components/loader.ts` 和 `components/cancellable-loader.ts` 新增 `TuiLoader` 与 `TuiCancellableLoader`。Tau 版本固定 10 帧 spinner、默认 message、前置空行、formatter hook、message update、可选 80ms timer、手动 tick 和 render request callback；cancellable loader 在 Escape / Ctrl+C 上设置 abort signal 并触发单次 callback。`Tau.Tui.Tests` 新增 loader targeted coverage，当前 `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 198/198；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 198、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 spinner loader / cancellable loader 库层 baseline；terminal image、rich markdown、完整 TUI host/focus stack、硬件 cursor 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui rich Markdown foundation，对照上游真实 `packages/tui/src/components/markdown.ts` 新增 `TuiMarkdown`、`TuiMarkdownTheme` 与 `TuiDefaultTextStyle`。Tau 版本覆盖 heading、paragraph、inline code/bold/italic/strike/link、fenced code、list、table、blockquote、horizontal rule、padding/background/cache、terminal image escape line no-wrap 和可选 OSC 8 hyperlink；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 206/206；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 206、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 Markdown 组件库层 foundation；terminal image、exact Marked tokenizer、syntax highlight/theme integration、完整 TUI host/focus stack、硬件 cursor 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui terminal image foundation，对照上游 `packages/tui/src/terminal-image.ts` 与 `components/image.ts` 新增 `TuiTerminalImage` 与 `TuiImage`。Tau 版本覆盖 capability detection、kitty/iTerm2 escape encoding、PNG/JPEG/GIF/WebP dimension sniffing、row calculation、renderImage、OSC 8 hyperlink、image fallback、image component protocol/fallback render 和 cache 行为；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 214/214；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 214、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 terminal image 与 image component 库层 foundation；真实 Kitty/iTerm2 terminal smoke、完整 terminal lifecycle、CodingAgent tool image integration、硬件 cursor 和 TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui stdin-buffer foundation，对照上游 `packages/tui/src/stdin-buffer.ts` 新增 `TuiInputSequenceBuffer`。Tau 版本覆盖普通字符拆分、跨 chunk CSI/SGR mouse buffering、OSC/DCS/APC string sequence 终止符识别、SS3/meta escape、高字节 meta 转换、bracketed paste content event 与 remainder 处理、manual flush、clear、timeout flush 和 destroy 后拒绝复用；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 225/225；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 225、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭 stdin-buffer 库层 foundation；真实 `ProcessTerminal` raw mode、Kitty keyboard protocol enable/disable、modifyOtherKeys fallback、stdin drain、Windows VT input、硬件 cursor 和 live TTY/PTY smoke 仍保留为后续缺口。
- 2026-06-06：继续 Tau.Tui spacer component parity，对照上游 `packages/tui/src/components/spacer.ts` 新增 `TuiSpacer`。Tau 版本覆盖默认 1 行空输出、`SetLines(...)` 动态更新、负数 line count 按空输出处理，以及在 `TuiContainer` 中作为 vertical gap 的组合渲染；`dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` 通过 240/240；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 240、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。该切片关闭独立 spacer component parity；broader text/truncated-text exact widget parity、完整 TUI host/focus stack、硬件 cursor 和真实 TTY/PTY smoke 仍保留为后续缺口。
- 2026-05-28：把 100% 移植率定义为 inventory + behavior tests + real e2e + release artifact + docs/history closure，不再用单一百分比或主观进度声明。
- 2026-05-28：本计划作为最终收口 plan；旧 `2026-05-10-tau-complete-pi-mono-port.md` 和 `2026-05-20-coding-agent-parity-gap-analysis.md` 保留历史和局部上下文，但后续最终验收以本计划为准。
- 2026-05-28：多 Agent 默认按上游 package 与 Tau 模块一一映射并保持互斥文件边界；Main Integrator 独占共享 docs/scripts/solution 和最终验证。
- 2026-05-28：部分 explorer 因额度或服务错误中断，Phase 1 不等待外部 agent 恢复；主控以本地扫描和已完成的 Mom explorer 输出补齐 capability-level matrix，后续继续扩成 file-level inventory。
- 2026-05-28：Phase 2 从 `packages/agent/src/proxy.ts` 对齐开始，新增 Tau-native `ProxyStreamProvider`，先关闭明确 missing 的 Agent stream proxy baseline，真实 proxy-server e2e 仍保留为后续验证项。
- 2026-05-28：继续 Phase 2 Agent lane，新增 Tau-native high-level `Agent` facade；先关闭上游 `agent.ts` 的 prompt/continue/listener/queue/wait/reset baseline，不把它标为完整 Agent loop parity，后续继续收口 `agent-loop.ts` 的 event payload、tool update、schema validation 和 failure/cancel 语义。
- 2026-05-28：Phase 1 final normalization 完成；当前上游 `packages` 目录集合为 `agent`、`ai`、`coding-agent`、`mom`、`pods`、`tui`、`web-ui`，matrix 已明确 capability rows 只作摘要、file-level rows 作 ownership/source 映射、surface rows 作 public contract/e2e 映射，并新增 Phase 2 Candidate Queue 作为下一轮 worker backlog。
- 2026-05-28：Phase 2 Agent loop/schema contract closure 落地：`Tau.Agent` 现在补上 `agent_end.messages`、`turn_end.message/toolResults`、tool update partial result、schema validation 和 error-as-tool-result baseline；`Tau.Agent.Tests`、`Tau.CodingAgent.Tests`、`Tau.WebUi.Tests` 与 `verify-dotnet.ps1 -SkipRestore` 均通过，当时剩余 Agent gap 主要是 parallel event timing、完整 failure/cancel assistant message 语义和 public API compile-sample。
- 2026-05-28：继续同一 Agent Phase 2 lane，新增 `AgentPublicApiCompileSampleTests`，用外部消费者样例冻结 Tau.Agent facade/runtime/proxy/event 公共入口；Agent 剩余 gap 当时收窄为 parallel event timing 与完整 failure/cancel assistant message 语义。
- 2026-05-28：继续同一 Agent Phase 2 lane，对照上游 `agent.ts handleRunFailure` 补 high-level `Agent` facade 的 synthetic assistant failure/cancel message：stream fault 会追加 stopReason=`error` 的 assistant failure message，cancellation 会追加 stopReason=`aborted` 的 assistant failure message，`agent_end.messages` 只报告本轮 failure message；同时新增 Tau-native `StopReason.Aborted` 并接通 proxy、OpenAI Responses cancelled 映射和 Mom stop reason normalization。当时 Agent 剩余 gap 收窄为 exact parallel event timing、low-level `EventStream<AgentEvent, AgentMessage[]>` wrapper shape 和更细的 runtime cancellation edge parity。
- 2026-05-28：继续同一 Agent Phase 2 lane，补 Tau-native low-level `AgentRuntime.RunStream(...)` wrapper，返回 `EventStream<AgentEvent, ChatMessage[]>` 并从 `agent_end.messages` 提取 `ResultAsync`，对齐上游 `createAgentStream()` 的终态结果形状；`AgentRuntimeContractTests` 和 public API compile sample 已覆盖。Agent 剩余 gap 收窄为 exact parallel event timing 和更细的 runtime cancellation edge parity。
- 2026-05-28：继续同一 Agent Phase 2 lane，对照上游 parallel tool execution 语义修正 Tau parallel timing：所有 runnable tool 先按 assistant source order 发 `tool_execution_start` / prepare，再并发执行；tool update 可在前序 tool 未完成时实时流出，最终 `tool_execution_end` / tool result 仍按 source order 发出。同时补 low-level assistant stream cancellation terminal contract：`RunAsync` / `RunStream` 在 cancellation 时产出 aborted assistant + `turn_end` + `agent_end`，并保留 aborted `ErrorEvent` partial stop reason。`Tau.Agent.Tests` 当时 targeted/full 已过 112/112；当时 Agent 剩余 gap 收窄为 tool cancellation cleanup、parallel sibling cancellation result preservation 和 `TransformContext` cancellation parity。
- 2026-05-28：继续同一 Agent Phase 2 lane，对照上游 `executePreparedToolCall` / `finalizeExecutedToolCall` 的 catch-as-tool-result 语义，补 Tau tool cancellation cleanup：prepare/before/execute/after 阶段的 `OperationCanceledException` 在运行 token 已取消时转成 `Operation canceled.` error tool result，不再打断 runtime 枚举；`AgentRuntime` 对 pending tool calls 做 finally 清理；parallel tool update wait 不再因同一 cancellation token 提前抛出，确保 sibling success/cancel 结果仍按 assistant source order 进入 `turn_end.toolResults`。`AgentRuntimeContractTests` 当前 14/14、`Tau.Agent.Tests` 当前 114/114 通过；Agent 剩余 gap 收窄为 `TransformContext` cancellation parity。
- 2026-05-28：继续同一 Agent Phase 2 lane，对照上游 `agent-loop.ts` 的 transform/cancel 行为，把 `ContextTransformer` 改成 cancellation-aware build flow，`AgentRuntime` 在 transform 阶段被取消时会产出 aborted assistant + `turn_end` + `agent_end`，并跳过 provider 调用。`AgentRuntimeContractTests` 当前 15/15、`Tau.Agent.Tests` 当前 115/115 通过；Agent loop/event contract 已在 Tau 本地闭合，剩余更大范围缺口转为 facade 选项 pass-through、public export 形状决策和真实 proxy/e2e 验证。
- 2026-05-28：继续 Phase 2 AI helper lane，对照上游 `packages/ai/src/utils/validation.ts`，把 tool argument validation 从 Agent 私有实现上移为 `Tau.Ai.ToolArgumentValidator` public helper，并让 `Tau.Agent` 复用同一 helper。当前只声明 object/array/required/properties/items/type coercion/enum/anyOf baseline，完整 AJV/TypeBox keyword 与 CSP/runtime-codegen fallback 仍保留为后续 AI public API/helper closure 缺口；串行复核已通过 `Tau.Ai.Tests` 225/225、`Tau.Agent.Tests` 115/115、`verify-dotnet.ps1 -SkipRestore` 和 `verify-dotnet.ps1 -SkipRestore -RunSmoke`。
- 2026-05-28：继续 Phase 2 AI public API lane，新增 `AiPublicApiCompileSampleTests`，用外部消费者样例覆盖 `Tau.Ai` 当前 message/content/tool/model/options/usage、provider registry、stream functions/events、model helper、auth/OAuth 和 validation 公共入口。该切片只固定 .NET-native public surface，不关闭 standalone `pi-ai` bin、TypeBox/faux/helper export 等无直接 .NET 映射项；`AiPublicApiCompileSampleTests` 1/1、`Tau.Ai.Tests` 226/226、`verify-dotnet.ps1 -SkipRestore` 通过。
- 2026-05-28：继续 Phase 2 AI helper lane，对照上游 `packages/ai/src/utils/headers.ts`、`hash.ts`、`overflow.ts`、`typebox-helpers.ts`，新增 `AiHeaderUtilities`、`ShortHash`、`ContextOverflowDetector` 和 `JsonSchemaHelpers.StringEnum` public helper baseline；`ContextOverflowDetector` 固定上游 provider overflow pattern、non-overflow exclusion 和 silent usage overflow 判断，`CodingAgentRetryClassifier` 复用同一 helper。该切片不关闭 standalone `pi-ai` bin、public faux provider、完整 TypeBox/AJV、incomplete JSON parser 或 exact TS export shape；focused validation `AiUtilityHelpersTests|AiPublicApiCompileSampleTests` 17/17、`CodingAgentRetryClassifierTests` 2/2 通过。
- 2026-05-28：继续 Phase 2 AI public provider lane，对照上游 `packages/ai/src/providers/faux.ts`，新增 `Tau.Ai.Providers.Faux` public scripted provider baseline，并把 `AiPublicApiCompileSampleTests` 扩展到 Faux provider 注册、响应队列和 tool-use assistant 结果；同时把 `ProviderAuthResolver` 的 models.json status source 改为构造期可注入 `ModelConfigurationStore`，让 status 测试不再依赖进程级 `TAU_MODELS_FILE`，避免 Windows/xUnit 并行下临时目录删除碰到文件占用。最新验证通过 `Tau.Ai.Tests` 259/259、`verify-dotnet.ps1 -SkipRestore` 和 `verify-dotnet.ps1 -SkipRestore -RunSmoke`，全仓计数为 `Tau.Ai.Tests` 259、`Tau.Agent.Tests` 115、`Tau.Tui.Tests` 190、`Tau.CodingAgent.Tests` 435、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
- 2026-05-28：继续 Phase 2 AI helper lane，对照上游 `packages/ai/src/utils/json-parse.ts` 和 `partial-json@0.1.7` 实际输出，新增并校准 `StreamingJsonParser.ParseStreamingJson(...)` public helper baseline；当前固定 complete JSON fast path、empty/invalid fallback `{}`、incomplete nested object/array/string/literal recovery、incomplete decimal drop 和 incomplete exponent base recovery，并把 `AiPublicApiCompileSampleTests` 扩展到外部消费者调用入口。focused validation `AiUtilityHelpersTests|AiPublicApiCompileSampleTests` 当前 29/29 通过；该切片关闭 public incomplete JSON parser baseline，但 provider-wide streaming tool-argument parser 统一接入、standalone `pi-ai` bin、完整 TypeBox/AJV 和 exact TypeScript export/subpath shape 仍保留为后续缺口。
- 2026-05-29：继续 Phase 2 AI helper/provider parser lane，对照上游 `parseStreamingJson(...)` 在 OpenAI completions、OpenAI Responses shared、Anthropic、Bedrock 和 Mistral streaming tool-call argument path 的调用方式，把 Tau 的 OpenAI-style / OpenAI Responses / Anthropic / Mistral / Bedrock provider streaming tool-call arguments 接到 `StreamingJsonParser` object raw-text helper；partial/done message 的 `ToolCallContent.Arguments` 保持合法 JSON object 字符串，`ToolCallDeltaEvent.Delta` 保留 provider 原始增量。focused validation 当前 34/34 通过，`Tau.Ai.Tests` 当前 273/273 通过；该切片关闭 primary provider parser adoption baseline，但真实 provider e2e、standalone `pi-ai` bin、完整 TypeBox/AJV 和 exact TypeScript export/subpath shape 仍保留为后续缺口。
- 2026-05-29：继续 Phase 2 AI public API/bin lane，对照上游 `packages/ai/src/cli.ts` 和 package `bin.pi-ai`，新增 `Tau.Ai.Cli` standalone executable baseline：`help`、`list`、`login [provider]`、交互式 provider selection、unknown-provider error、默认 Tau auth store 写入，以及 `--auth-file auth.json` 显式复刻上游 cwd `auth.json` 写入语义。该切片不声明发布层 `pi-ai` alias、真实 OAuth e2e、TypeBox re-export 或 exact TypeScript export/subpath shape 完成；focused validation 通过 `AiCliRunnerTests` 7/7、`Tau.Ai.Tests` 280/280，并完成 `dotnet run --project src\Tau.Ai.Cli\Tau.Ai.Cli.csproj --no-build -- list` smoke。
- 2026-05-29：启动 Phase 5 release artifact baseline，对照上游 `scripts/build-binaries.sh` 和 root bin 入口，新增 `scripts/build-release-artifacts.ps1` 与 `scripts/smoke-release-artifacts.ps1`。当前平台 `win-x64` artifact 会 publish `Tau.CodingAgent`、`Tau.Ai.Cli`、`Tau.Mom`、`Tau.Pods`、`Tau.WebUi` 到 `artifacts/tau-win-x64/apps/**`，并生成 `bin/pi.cmd`、`bin/tau-ai.cmd`、`bin/pi-ai.cmd`、`bin/mom.cmd`、`bin/pi-pods.cmd`、`bin/tau-web-ui.cmd`、`manifest.json` 和基础 docs。artifact smoke 已验证 `tau-ai list`、`pi-ai list`、`pi --mode rpc get_state`、`pi-pods --help`、WebUi `/healthz` / `/api/status` / `/api/catalog` / session store 写入，以及 Mom `--once` 本地 event/inbox/outbox/status/log/runtime-log 链路；`verify-dotnet.ps1 -SkipRestore` 也通过。该切片只关闭 current-RID executable artifact baseline，不声明 cross-platform archives、no-env wrapper、full payload parity、version/tag/publish automation 或真实外部 e2e 完成。
- 2026-05-29：继续 Phase 5 no-env wrapper baseline，对照上游 root `test.sh` / `pi-test.sh` 的 `--no-env` 和 auth 隔离意图，新增 `scripts/invoke-no-env.ps1` 与 `scripts/verify-no-env.ps1`。`invoke-no-env.ps1` 通过 `ProcessStartInfo.EnvironmentVariables` 只清理子进程环境，覆盖 OpenAI、Anthropic、Google/Gemini/Vertex、Azure OpenAI、AWS/Bedrock、HF、OpenAI-compatible aliases、Slack 和 Tau auth/config/session/log 变量，设置 `PI_NO_LOCAL_LLM=1` / `TAU_NO_LOCAL_LLM=1`，并支持把 `TAU_AUTH_FILE`、`TAU_MODELS_FILE`、CodingAgent session/settings/history/keybindings 和 `TAU_LOG_FILE` 指向临时目录；dry-run 只列变量名，不回显值；input file 通过 shell redirection 进入子进程，避免 Windows PowerShell / .NET stdin BOM 破坏 CodingAgent RPC JSONL。`verify-no-env.ps1 -SkipRestore -RunSmoke` 已在该隔离环境真实运行全仓 gate 并通过，覆盖既有 `tau-ai` / WebUi / Mom smoke，以及 no-env `tau-ai list` 和 CodingAgent RPC `get_state`；本切片不移动用户真实 auth 文件，因此 exact upstream auth backup / Unix wrapper parity 仍未关闭。
- 2026-05-29：继续 Phase 5 `pi-test.sh` 直达 wrapper closure，新增 `scripts/pi-test.ps1`。默认运行 `Tau.CodingAgent`，`--no-env` 时复用 `invoke-no-env.ps1` 的 provider/auth 环境清理和临时 Tau 状态，`--input-file` 通过 shell redirection 传 stdin，`--argument-list-base64` 支持验证脚本绕开 PowerShell 5.1 的 child args 透传歧义；`verify-no-env.ps1 -RunSmoke` 的 CodingAgent RPC `get_state` 现在实际通过 `pi-test.ps1 --no-env --no-build` 执行。本切片是 PowerShell-first 等价入口，不移动用户真实 auth 文件，也不声明 Unix shell wrapper 或 exact auth-backup parity 完成。
- 2026-05-29：继续 Phase 5 release archive baseline，对照上游 `scripts/build-binaries.sh` 在构建目录后创建 release archive 并解压测试的职责，新增 `scripts/package-release-artifacts.ps1`。脚本校验 `artifacts/tau-<rid>/manifest.json`，生成 `artifacts/releases/tau-<rid>.zip`，保留 `tau-<rid>` 顶层目录，并把 zip 解压到全新临时目录后复用 `scripts/smoke-release-artifacts.ps1` 验证 extracted copy。该切片关闭 current-RID zip archive + extraction smoke baseline，不声明全平台 `tar.gz/zip` 矩阵、full payload parity、version/tag/publish automation、CI 接入或真实外部 e2e 完成。
- 2026-05-29：继续 Phase 5 CI/release artifact baseline，新增 `.github/workflows/tau-ci.yml`。workflow 在 `push main`、`pull_request` 和手动触发时使用 Windows runner，按 `global.json` 安装 .NET SDK，先 `dotnet restore Tau.slnx`，再运行 `verify-no-env.ps1 -SkipRestore -RunSmoke`、`build-release-artifacts.ps1 -Configuration Release`、`package-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64`，最后上传已经解压 smoke 过的 `artifacts/releases/tau-win-x64.zip`。本切片关闭 Windows current-RID CI + release zip artifact upload baseline；不声明全平台 archive matrix、Unix wrapper、version/tag/publish automation 或真实外部 e2e release smoke 完成。

- 2026-05-29：继续 Phase 5 cross-platform archive matrix baseline，对照上游 `scripts/build-binaries.sh` 的五平台归档规则，把 `scripts/package-release-artifacts.ps1` 扩展为 `-ArchiveFormat auto|zip|tar.gz`：`win-*` 生成 zip，`linux-*` / `osx-*` 生成 tar.gz；归档后统一解压到 clean temp 校验 `tau-<rid>` 顶层目录，宿主 RID 默认运行 executable smoke，非宿主 RID 默认只做结构校验，除非显式 `-ForceSmoke`。新增 `scripts/package-release-matrix.ps1` 归档已存在 RID artifact，新增 `scripts/build-release-matrix.ps1` 按 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`、`win-x64` 执行 restore/build/package。本切片关闭 archive format/RID matrix baseline，不关闭非宿主 runner smoke、full payload parity、Unix wrapper/auth-backup parity、version/changelog/tag/publish automation或真实外部 e2e。
- 2026-05-29：继续 Phase 5 release payload manifest baseline，对照上游 `scripts/build-binaries.sh` 的 shared file copy 清单，`build-release-artifacts.ps1` 现在复制 `README.md`、`LICENSE` 和完整当前 `docs/`，并在 `manifest.json.releasePayload` 中记录 `readme`、`license`、`docs`、`examples`、`changelog`、`package-json`、`photon-wasm`、`theme`、`export-html`、`interactive-assets`、`koffi-windows-native` 的 included / tau-native-docs / tau-native-manifest / tau-native-inline / missing / not-applicable 状态；`smoke-release-artifacts.ps1` 会验证这些 entry 和 `docs/releases/feature-release-notes.md`。本切片关闭当前 Tau docs payload copy + manifest audit baseline，不关闭 examples、Photon image pipeline、interactive raster assets、external export-html vendor/template、非宿主 runner smoke、version/changelog/tag/publish automation或真实外部 e2e。
- 2026-05-29：继续 Phase 5 release automation dry-run baseline，对照上游 `scripts/release.mjs` 新增 `scripts/plan-release.ps1`。脚本只生成可审计 release plan：检查 git worktree、release notes 和 release scripts，计算 `major|minor|patch` 或 explicit `x.y.z` 下一版本，列出 no-env gate 与 release matrix build/package 命令，并把版本写入、release notes 修改、commit、tag、publish、push 明确列为未执行 mutation。该 dry-run 先暴露了 Tau 缺 repo-owned MSBuild version source；后续 `Directory.Build.props` `VersionPrefix` baseline 已关闭版本源缺口，但真实 release execution automation 仍未完成。
- 2026-05-29：继续 Phase 5 repo-owned version source baseline，对照上游 packages lockstep version 语义，Tau 先把自身产品版本事实源落到 `Directory.Build.props` 的 `VersionPrefix=0.1.0`，而不是复用上游 npm 包当前 `0.67.68`。`plan-release.ps1` 现在可直接从 MSBuild version source 计算 bump；release artifact `manifest.json` 会写入 `version` 和 `versionSource`，artifact smoke 会验证 manifest 与 `Directory.Build.props` 一致。该切片关闭 repo-owned version source + manifest audit baseline，不关闭版本写回、release notes、commit/tag/publish/push execution automation。
- 2026-05-29：继续 Phase 5 version writeback preview baseline，新增 `scripts/update-release-version.ps1`。脚本读取 `Directory.Build.props` 中唯一的 `Version` / `VersionPrefix` / `PackageVersion`，计算 bump 或验证 explicit semver，默认 dry-run；只有显式 `-Apply` 才写回该属性。验证使用临时 props 副本执行 `-Apply`，避免把真实仓库版本从 `0.1.0` 提前推进。该切片关闭 version writeback helper baseline，不关闭 release notes mutation、release commit/tag、publish 或 push automation。
- 2026-05-29：继续 Phase 5 release notes helper baseline，新增 `scripts/update-release-notes.ps1`。脚本读取 `docs/releases/feature-release-notes.md`，按 `v<version>`、日期、功能域、用户价值和摘要生成当前月份表格行，默认 dry-run；只有显式 `-Apply` 才写回 release notes。验证使用临时 release notes 副本执行 `-Apply`，避免把真实发布记录提前写成未发布版本。`plan-release.ps1` 现在把 release notes preview 纳入 planned commands 和 non-executed mutation；该切片关闭 release notes mutation helper baseline，不关闭 release commit/tag、publish、push 或完整 release execution automation。
- 2026-05-29：继续 Phase 5 guarded release preparation baseline，新增 `scripts/prepare-release.ps1`。脚本默认 dry-run，复用 `update-release-version.ps1` 与 `update-release-notes.ps1` 预览版本和 release notes 写入；显式 `-Apply` 且工作树干净时只写 `Directory.Build.props` 与 `docs/releases/feature-release-notes.md`，写入前会先跑两个 helper dry-run 预检。`plan-release.ps1` 现在把该 preparation flow 纳入 required scripts 和 planned commands。该切片关闭本地 release preparation 编排 baseline，不关闭 no-env gate/release matrix 执行编排、release commit/tag、publish、push、非宿主 runner smoke 或真实外部 e2e。
- 2026-05-29：继续 Phase 5 guarded release validation baseline，新增 `scripts/validate-release.ps1`。脚本默认 dry-run，列出 `git diff --check`、`verify-no-env.ps1` 和 `build-release-matrix.ps1`；显式 `-Run` 且工作树干净时才执行这些本地验证，`-AllowDirty` 只用于 WIP validation。`plan-release.ps1` 现在把该 validation flow 纳入 required scripts 和 planned commands。该切片关闭本地 release validation 编排 baseline，不关闭 release preparation apply、commit/tag、publish、push、非宿主 runner smoke 或真实外部 e2e。
- 2026-05-29：继续 Phase 5 release dry-run contract smoke baseline，新增 `scripts/verify-release-contracts.ps1`。脚本只调用 `plan-release.ps1`、`prepare-release.ps1` 和 `validate-release.ps1` 的 `-Json` dry-run 输出，断言 dry-run boundary、planned command names、non-executed mutation boundary、preparation changed files 和 validation coverage metadata；不跑 build/test，不生成 artifact，不写版本或 release notes。`validate-release.ps1` 现在输出 `validationLevel`、enabled/skipped validation count 和 validation name 列表，并在 `-SkipNoEnv -SkipMatrix` 下给 coverage warning，防止 minimal diff-only run 被误认为完整 release validation。`.github/workflows/tau-ci.yml` 已把该短 smoke 放在 no-env/build 前。本切片关闭 release script dry-run contract smoke baseline，不关闭 release commit/tag/publish/push、非宿主 runner smoke 或真实外部 e2e；后续 release finalization smoke 已在 2026-06-03 增量中补齐。
- 2026-05-29：继续 Phase 5 guarded local release execution baseline，新增 `scripts/execute-release.ps1`。脚本默认 dry-run；显式 `-Apply` 且工作树干净时才运行 release contract smoke、执行 version + release notes preparation、可选本地 validation，然后只 stage `Directory.Build.props` 和 `docs/releases/feature-release-notes.md`，创建 `Release v<version>` commit 并打 `v<version>` tag；同名 tag、dirty worktree 或额外 prepare 变更会阻断。`plan-release.ps1` 与 `verify-release-contracts.ps1` 已纳入该 execution contract。该切片关闭本地 release commit/tag baseline，但不关闭远端 publish/push、非宿主 runner smoke、真实外部 e2e、examples/Photon/interactive assets/external export-html payload parity 或 exact Unix wrapper/auth-backup parity；Tau 也不生成上游第二个 `[Unreleased]` commit，因为 release notes 是日期表格而不是每包 CHANGELOG section。远端 branch/tag push 与 GitHub Release archive upload baseline 已在 2026-06-03 finalization 切片中补齐，剩余远端发布缺口收窄为 package registry publish synchronization 与真实远端演练。
- 2026-05-29：继续 Phase 5 root session audit script parity，对照上游 `scripts/session-transcripts.ts` 和 `scripts/cost.ts` 新增 `scripts/export-session-transcripts.ps1`、`scripts/report-session-costs.ps1` 与 `scripts/verify-session-audit-scripts.ps1`。Transcript 脚本默认扫描 Tau JSONL tree session，也支持显式 `-SessionPath` / `-SessionsDirectory`，只导出 user/assistant 文本并按字符上限切片；cost 脚本按日期窗口聚合已持久化 `usage.cost` 和 token records，但不从当前 catalog 反推缺失历史成本。fixture smoke 固定坏 JSONL line 跳过、toolResult 忽略、上游 string content 兼容、cost/token 汇总和 JSON array shape；`plan-release.ps1` 与 `verify-release-contracts.ps1` 已纳入该 smoke。该切片不关闭上游 transcript `--analyze` 子 agent 流程，也不关闭 CodingAgent session usage/cost 持久化缺口。
- 2026-05-29：继续 Phase 5 CodingAgent startup profile script parity，对照上游 `scripts/profile-coding-agent-node.mjs` 新增 `scripts/profile-coding-agent-startup.ps1` 与 `scripts/verify-coding-agent-startup-profile.ps1`。Profiler 使用已构建或自动构建的 `Tau.CodingAgent.dll`，在隔离 Tau state 下运行 `--mode rpc --no-context-files --no-themes`，用无 BOM JSONL input file + shell redirection 发送 `get_state`，计时到 successful response 并输出 summary / `METRIC startup_time_ms` / JSON。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `coding-agent-startup-profile-smoke`。该切片只关闭 RPC startup benchmark baseline，不关闭 TUI first-frame startup profiling、CPU profile、Node/Bun runtime comparison 或 `PI_STARTUP_BENCHMARK` 等价 hook。
- 2026-05-29：继续 Phase 5 release version sync parity，对照上游 `scripts/sync-versions.js` 新增 `scripts/sync-release-versions.ps1` 与 `scripts/verify-release-version-sync.ps1`。Tau 不维护 `packages/*/package.json` workspace lockstep，而是以 `Directory.Build.props` 的单一 MSBuild 版本为事实源；脚本扫描 `src/**/*.csproj` 的显式 `Version` / `VersionPrefix` / `PackageVersion` 漂移，dry-run 漂移时返回非零，`-Apply` 可同步显式项目版本。fixture smoke 固定漂移检测、ProjectReference 审计和 apply 修复，并验证当前仓库 8 个 src 项目无漂移。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `release-version-sync-smoke`。该切片不关闭 NuGet/package registry publish synchronization；branch/tag push baseline 已在 2026-06-03 finalization 切片中补齐。

- 2026-06-03：继续 Phase 5 release finalization parity，对照上游 `scripts/release.mjs` 后段 `npm run publish`、`git push origin main` 和 `git push origin v<version>`，新增 `scripts/finalize-release.ps1` 与 `scripts/verify-release-finalize.ps1`。Tau 将本地 version/notes/commit/tag 与远端发布拆开：`execute-release.ps1` 仍只负责本地 release commit/tag；`finalize-release.ps1` 默认 dry-run，显式 `-Apply` 才在 tag 格式、git、clean worktree、remote、branch、local tag、tag 指向 branch tip、release archive 存在等预检通过后执行 `git push <remote> <branch>` 与 `git push <remote> <tag>`。`-CreateGitHubRelease` 会额外要求 release notes 与 `gh` CLI，并上传已验证 archive；`-Draft` / `-Prerelease` 在 dry-run 预览和 fake-gh apply smoke 中都固定为 `--draft` / `--prerelease`。`verify-release-finalize.ps1` 用临时 Git 仓库、临时 bare remote、fake archive 和 fake `gh.cmd` 固定 dry-run JSON、temp remote branch/tag push、GitHub Release create 参数、dirty worktree apply 阻断和 draft/prerelease flag 一致性；当前 `verify-release-finalize.ps1` 22 assertions 通过，`verify-release-contracts.ps1` 46 assertions 通过，`git diff --check` 通过。该切片关闭 guarded branch/tag push 与 GitHub Release archive upload baseline；真实远端发布演练、非宿主 runner smoke、真实外部 e2e release smoke、examples/Photon/interactive assets payload parity 和 exact Unix wrapper/auth-backup parity 仍保留为 Phase 5 缺口。
- 2026-06-03：继续 Phase 5 package publish synchronization baseline，对照上游 `scripts/release.mjs` 的 `npm run publish`，新增 `scripts/publish-release-packages.ps1` 与 `scripts/verify-release-package-publish.ps1`。Tau 不把应用项目默认发布为 NuGet 包，默认 package boundary 只覆盖 `Tau.Ai`、`Tau.Agent`、`Tau.Tui` 三个库包；`publish-release-packages.ps1` 默认 dry-run，显式 `-Apply` 才执行 `dotnet pack` 与 `dotnet nuget push`，并从 `Directory.Build.props` 读取版本、从 `-ApiKeyEnv` 指定环境变量读取 API key，JSON/command output/子进程 output preview 只报告 env 名称和 present 状态而不回显密钥。`verify-release-package-publish.ps1` 用临时 Git 仓库、临时 library/app project fixture 和 fake `dotnet.ps1` 固定默认库包边界、pack/push command、API key redaction、子进程 output preview 脱敏、应用项目 warning 和 dirty worktree apply 阻断；当前 `verify-release-package-publish.ps1` 21 assertions 通过，`verify-release-contracts.ps1` 46 assertions 通过，`git diff --check` 通过。该切片关闭 guarded package publish dry-run/apply baseline；真实 NuGet/package registry 发布演练、应用项目包策略、symbol package publishing、真实 package signing/provenance rehearsal 和真实外部 e2e release smoke 仍保留为 Phase 5 缺口。
- 2026-05-29：继续 Phase 5 edit tool stats script parity，对照上游 `scripts/edit-tool-stats.mjs` 新增 `scripts/report-edit-tool-stats.ps1` 与 `scripts/verify-edit-tool-stats.ps1`。脚本扫描 Tau JSONL session 中 assistant edit tool call 与 tool result，默认同时识别 Tau `edit_file` 和上游 `edit`，输出 success/failure、single/multi-edit、argument style、provider/model、extension、same-file cluster、context inflation、failure kind 和 worst examples；fixture smoke 固定 Tau/upstream 工具名、multi-edit、失败分类、过滤和 malformed JSONL 跳过。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `edit-tool-stats-smoke`。该切片不改变 edit tool runtime，也不关闭真实 provider/tool e2e。
- 2026-05-29：继续 Mom timestamp migration helper parity，对照上游 `packages/mom/scripts/migrate-timestamps.ts` 新增 `scripts/migrate-mom-timestamps.ps1` 与 `scripts/verify-mom-timestamp-migration.ps1`。脚本扫描 `<data-dir>/<channel>/log.jsonl`，默认 dry-run，显式 `-Apply` 时把历史毫秒 Unix `ts` 转成 Slack `seconds.microseconds`，保留 malformed 行、已有 Slack timestamp 和 Tau-native `*-bot` timestamp；fixture smoke 固定 dry-run/apply/idempotent 行为。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `mom-timestamp-migration-smoke`。该切片不关闭真实 Slack smoke、Slack session sync 或 Docker sandbox smoke。

- 2026-05-30：继续 CodingAgent auth migration helper parity，对照上游 `packages/coding-agent/src/migrations.ts` 的 `migrateAuthToAuthJson()`，新增 `scripts/migrate-coding-agent-auth.ps1` 与 `scripts/verify-coding-agent-auth-migration.ps1`。脚本默认扫描用户 `~/.tau`，也支持显式 `-AgentDirectory` / `-AuthPath` / `-OAuthPath` / `-SettingsPath`；默认 dry-run，显式 `-Apply` 才写 `auth.json`、把 `oauth.json` rename 为 `oauth.json.migrated` 并从 `settings.json` 删除 `apiKeys`；`auth.json` 已存在时跳过 legacy 文件，OAuth 与 api key 同 provider 冲突时 OAuth 优先，JSON 输出只报告 provider/credentialKind 不回显 secret。fixture smoke 固定 dry-run/apply/idempotent、existing auth skip、invalid oauth/settings skip、OAuth winner、secret redaction 和 remaining gap audit，当前 25 assertions 通过。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `coding-agent-auth-migration-smoke`。该切片只关闭 legacy auth migration helper baseline，不迁移 Tau runtime `coding-agent-settings.json`，也不关闭 full settings/runtime auth parity、credential refresh UX、actual hooks/custom tools migration 或真实 OAuth e2e。
- 2026-05-29：继续 CodingAgent session migration helper parity，对照上游 `packages/coding-agent/scripts/migrate-sessions.sh` 和 `packages/coding-agent/src/migrations.ts` 新增 `scripts/migrate-coding-agent-sessions.ps1` 与 `scripts/verify-coding-agent-session-migration.ps1`。脚本扫描 agent 根目录直接子 `.jsonl`，只迁移第一行 `type=session` 且带 `cwd` 的 root misplaced session；cwd 编码沿用上游 slash/backslash/colon 替换规则，但目标落在 Tau 当前 `/clone` / `/resume` 可发现的 `coding-agent-sessions/<encoded-cwd>/`，而不是上游未被 Tau 搜索的 `sessions/<encoded-cwd>/`。默认 dry-run，显式 `-Apply` 才移动；fixture smoke 固定 dry-run/apply/idempotent、target conflict、坏 header、nested 不扫描和 Windows cwd 编码。该切片只关闭 Tau-native root JSONL relocation helper，不关闭 full settings/runtime auth parity、credential refresh UX、上游 extensions/keybindings/tools migrations 或 exact session schema parity。
- 2026-05-29：继续 CodingAgent commands-to-prompts migration helper parity，对照上游 `packages/coding-agent/src/migrations.ts` 的 `migrateCommandsToPrompts(...)` / `migrateExtensionSystem(...)`，新增 `scripts/migrate-coding-agent-commands.ps1` 与 `scripts/verify-coding-agent-commands-migration.ps1`。脚本默认检查用户 `~/.tau` 与当前项目 `./.tau`，也支持显式 `-BaseDirectory`；当 base directory 下 `commands/` 存在且 `prompts/` 不存在时 rename 为 `prompts/`；默认 dry-run，显式 `-Apply` 才执行。fixture smoke 固定 dry-run/apply/content preserved、target-exists、no-commands、file source、missing base、二次 apply 幂等和 remaining gap audit。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `coding-agent-commands-migration-smoke`。该切片只关闭 commands-to-prompts helper baseline，不关闭 settings/session/keybindings/tools-to-bin migrations、full settings/runtime auth parity、actual hooks/custom tools migration to extensions 或 general custom tool migration parity。
- 2026-05-29：继续 CodingAgent tools-to-bin migration helper parity，对照上游 `packages/coding-agent/src/migrations.ts` 的 `migrateToolsToBin()`，新增 `scripts/migrate-coding-agent-tools-to-bin.ps1` 与 `scripts/verify-coding-agent-tools-to-bin-migration.ps1`。脚本默认扫描用户 `~/.tau/tools`，也支持显式 `-AgentDirectory`；只处理 managed binaries `fd`、`rg`、`fd.exe`、`rg.exe`，目标 `bin/<name>` 不存在时创建 `bin/` 并 move，目标已存在时删除旧 `tools/<name>` 副本，不覆盖目标；custom tools 保持不动。默认 dry-run，显式 `-Apply` 才 move/remove；fixture smoke 固定 dry-run/apply/duplicate removal/content preserved/custom tool preserved/directory source skip/missing tools dir/二次 apply 幂等和 remaining gap audit。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `coding-agent-tools-to-bin-migration-smoke`。该切片只关闭 managed fd/rg relocation helper baseline，不关闭 settings/session/keybindings migrations、full settings/runtime auth parity、actual hooks/custom tools migration to extensions 或 general custom tool migration parity。
- 2026-05-29：继续 CodingAgent deprecated extension dirs audit helper parity，对照上游 `packages/coding-agent/src/migrations.ts` 的 `checkDeprecatedExtensionDirs(...)`，新增 `scripts/audit-coding-agent-deprecated-extension-dirs.ps1` 与 `scripts/verify-coding-agent-deprecated-extension-dirs-audit.ps1`。脚本默认检查用户 `~/.tau` 与当前项目 `./.tau`，也支持显式 `-BaseDirectory` / `-Label`；只审计 deprecated `hooks/` 目录和 `tools/` 目录中的 custom entries，忽略 managed `fd`、`rg`、`fd.exe`、`rg.exe` 与隐藏 entry，不迁移、不删除、不覆盖文件；JSON 输出包含 warnings/directories summary、上游 migration guide URL、extensions docs URL 与 remaining gaps。fixture smoke 固定 hooks warning、custom tools warning、managed binary ignore、hidden entry ignore、file tools path skip、missing base 和 no-mutation boundary。`plan-release.ps1`、`verify-release-contracts.ps1` 和 CI 已纳入 `coding-agent-deprecated-extension-dirs-audit-smoke`。该切片只关闭 warning audit helper baseline，不关闭 actual hooks/custom tools migration to extensions、settings/session/keybindings migrations、full settings/runtime auth parity、general custom tool migration parity 或 TypeScript extension runtime。
