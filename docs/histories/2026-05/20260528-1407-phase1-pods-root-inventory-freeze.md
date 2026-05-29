## [2026-05-28 14:07] | Task: Phase 1 Pods/root inventory freeze

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, C:\Users\zhouh\Desktop\Tau`

### User Query

> 持续执行 Tau 100% pi-mono parity 多 Agent 移植计划，从 Phase 1 上游 inventory freeze 开始，按 GOAL.md 和 active execution plan 推进。

### Changes Overview

**Scope:** `docs/exec-plans/active`, `next.md`, `docs/QUALITY_SCORE.md`, `docs/histories`

**Key Actions:**

* **Pods file-level matrix**: 对照 `C:\Users\zhouh\Desktop\pi-mono-main\packages\pods\src/**` 与 Tau `src/Tau.Pods/**` / `tests/Tau.Pods.Tests/**`，把 Pods 从 capability-level inventory 推进到 file-level mapping。
* **Pods surface matrix**: 补 CLI command、env、config、known model、GPU allocation、remote log/runtime log 和 prompt/agent path 的状态与缺口。
* **Root script matrix**: 对照上游 root/package scripts、release/build/test/browser smoke 和 package-local scripts，记录 Tau 当前 release/CI/no-env/script parity 缺口。
* **AI file-level matrix**: 对照 `C:\Users\zhouh\Desktop\pi-mono-main\packages\ai\src/**` 与 Tau `src/Tau.Ai/**` / `tests/Tau.Ai.Tests/**`，冻结 AI package 的 barrel/export、registry、stream facade、types/models/env/OAuth utilities、provider implementations 和 helper gaps。
* **Plan sync**: 同步 100% parity active plan、`next.md` 和 `docs/QUALITY_SCORE.md`，明确 Phase 1 尚待冻结的 CodingAgent/Tui/WebUi/Mom file-level mapping 与 AI/CodingAgent/Tui/WebUi/Mom 子矩阵。

### Design Intent (Why)

Phase 1 的完成标准不是“知道大概缺什么”，而是后续 worker 能从 matrix 直接领取带上游证据、Tau 目标、状态和验证缺口的任务。本轮先收敛 Pods/root 这块边界清楚、文件数少但 release/e2e 风险高的区域，避免后续继续把真实 SSH/HF/GPU/vLLM smoke、known model/GPU allocation、release artifact 和 no-env wrapper 漏在自由文本里。

随后继续补 AI file-level inventory，是因为 `Tau.Ai` 是 CodingAgent/WebUi/Mom 共享 provider/auth/model 根层。如果不先冻结 `packages/ai/src/**` 的 public API、provider、OAuth、env/config 和 helper gaps，后续 worker 容易把真实 provider e2e、`pi-ai` standalone CLI/bin、API string divergence、schema validation helper、faux provider 和 generated model full coverage 混进运行时实现阶段才发现。

### Files Modified

* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-05/20260528-1407-phase1-pods-root-inventory-freeze.md`

## [2026-05-28 15:01] | Task: Phase 1 CodingAgent/Tui/WebUi/Mom inventory freeze continuation

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, C:\Users\zhouh\Desktop\Tau`

### User Query

> 持续执行 Tau 100% pi-mono parity 多 Agent 移植计划，继续 Phase 1 上游 inventory freeze。

### Changes Overview

**Scope:** `docs/exec-plans/active`, `next.md`, `docs/QUALITY_SCORE.md`, `docs/histories`

**Key Actions:**

* **CodingAgent matrix**: 对照 `packages/coding-agent/src/**` 与 Tau `src/Tau.CodingAgent/**` / `tests/Tau.CodingAgent.Tests/**`，冻结 grouped file-level matrix 和 CLI/RPC/env/config/log/schema surface，明确 package/bin/config、top-level CLI flags、session schema、RPC extension UI、image ingestion、package manager、TypeScript extension runtime、HTML/share/release asset 等缺口。
* **Tui matrix**: 对照 `packages/tui/src/**` 与 Tau `src/Tau.Tui/**` / `tests/Tau.Tui.Tests/**`，冻结 file-level matrix 和 terminal/input/render/theme surface，明确 terminal live TTY、terminal image、loader、rich markdown、full app host/focus stack 和 theme token 系统等缺口。
* **WebUi matrix**: 对照 `packages/web-ui/src/**` 与 Tau `src/Tau.WebUi/**` / `tests/Tau.WebUi.Tests/**`，冻结 grouped file-level matrix 和 HTTP/component/env/config/log/schema/browser surface，明确 reusable component package、IndexedDB/provider-key/custom-provider UI、sandbox runtime、artifact viewers、CodingAgent branch-tree persistence、release/static smoke 等缺口。
* **Mom matrix**: 对照 `packages/mom/src/**` / `packages/mom/scripts/**` 与 Tau `src/Tau.Mom/**` / Mom 相关 tests，冻结 file-level matrix 和 Slack/event/session/sandbox/env/config/log/schema surface，明确 real Slack workspace smoke、Slack history/file/stop e2e、Docker smoke、fs watcher/debounce、timestamp migration helper 和 schema/layout parity 缺口。
* **Plan sync**: 同步 100% parity active plan、`next.md` 和 `docs/QUALITY_SCORE.md`，把 Phase 1 下一步改为 final normalization pass 与 Phase 2 候选抽取。

### Design Intent (Why)

Phase 1 的主要风险已经从“还有未知上游目录”转为“matrix 行过粗、status 需要统一、Phase 2 候选需要可执行化”。本轮把剩余四个产品模块的 file-level 和 surface inventory 固定下来，是为了让后续 worker 不再重新猜 CodingAgent CLI/RPC、Tui terminal、WebUi artifact/sandbox、Mom Slack/session/sandbox 的范围，而是直接从 matrix 中领取带上游证据、Tau 目标和验证缺口的切片。

### Files Modified

* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-05/20260528-1407-phase1-pods-root-inventory-freeze.md`

## [2026-05-28 14:33] | Task: Phase 1 AI surface inventory freeze continuation

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, C:\Users\zhouh\Desktop\Tau`

### User Query

> 持续执行 Tau 100% pi-mono parity 多 Agent 移植计划，继续 Phase 1 上游 inventory freeze。

### Changes Overview

**Scope:** `docs/exec-plans/active`, `next.md`, `docs/QUALITY_SCORE.md`, `docs/histories`

**Key Actions:**

* **AI command/API/env/config/log/schema surface**: 补 `packages/ai/src/**` 的公共合同矩阵，覆盖 `pi-ai` CLI/bin、KnownApi/KnownProvider、StreamOptions/Provider options、auth.json/models.json、runtime log hooks、tool schema validation、OAuth registry、helper utilities、generated model coverage、error/abort contract 与 Tau 对照。
* **Plan sync**: 同步 100% parity active plan、`next.md` 和 `docs/QUALITY_SCORE.md`，把 AI 从 file-level inventory 推进到后续 worker 可直接领任务的公共合同粒度。

### Design Intent (Why)

AI package 是 CodingAgent/WebUi/Mom 共用的 provider/auth/model 根层。只做 file-level mapping 还不足以指导后续 worker，必须把 CLI、API 名、env、config、log、schema、OAuth 和 helper contract 单独冻结，才能把 `pi-ai` standalone CLI/bin 缺口、OpenAI/Google API string divergence、tool schema validation helper 缺口和模型/凭证配置边界清楚地留给后续切片。

### Files Modified

* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-05/20260528-1407-phase1-pods-root-inventory-freeze.md`

## [2026-05-28 15:12] | Task: Phase 1 final normalization and Phase 2 candidate queue

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, C:\Users\zhouh\Desktop\Tau`

### User Query

> 接续 Tau 100% pi-mono parity / Phase 1 inventory freeze，完成 final normalization pass 并抽取 Phase 2 worker-ready candidates。

### Changes Overview

**Scope:** `docs/exec-plans/active`, `next.md`, `docs/QUALITY_SCORE.md`, `docs/histories`

**Key Actions:**

* **Normalization result**: 在 matrix 中补 `Phase 1 Normalization Result`，明确 capability rows 只作摘要、file-level rows 作 ownership/source 映射、surface rows 作 public contract/e2e 映射，并固定 status 语义。
* **Unknown directory check**: 现场核对上游 `packages` 目录集合为 `agent`、`ai`、`coding-agent`、`mom`、`pods`、`tui`、`web-ui`，没有未知 package 目录留在 matrix 之外。
* **Phase 2 candidate queue**: 在 matrix 中新增 worker-ready 队列，覆盖 Agent loop/schema、AI public API/bin/e2e、CodingAgent CLI/RPC/image、Tui terminal/image/markdown/loader、WebUi branch-tree/artifact sandbox、Mom Slack/Docker、Pods command/real e2e 和 release/no-env artifact parity；每行都记录 owner、上游证据、Tau 目标、status source、validation gate 和是否需要外部 e2e。
* **Plan sync**: 把 100% parity active plan、`next.md` 和 `docs/QUALITY_SCORE.md` 从“等待 final normalization”更新为“Phase 1 grouped inventory freeze 完成，下一步按 Phase 2 Candidate Queue 分派 worker”。

### Design Intent (Why)

Phase 1 的剩余风险不是继续扩表，而是防止后续 worker 从重复的 summary 行里重新猜任务边界。本轮把 capability/file/surface 三层职责写清楚，并把候选任务抽成带验证门禁的队列表，让下一轮可以直接进入 Phase 2 critical contract closure，同时保留 `external-e2e-needed` 直到真实服务或运行态 smoke 通过。

### Files Modified

* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-05/20260528-1407-phase1-pods-root-inventory-freeze.md`
