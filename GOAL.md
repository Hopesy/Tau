# Tau 100% pi-mono parity /goal

## /goal prompt

持续把 `C:\Users\zhouh\Desktop\pi-mono-main` 100% 可审计地移植到 `C:\Users\zhouh\Desktop\Tau`。

你是主控 agent / integrator，不是单模块 worker。必须按照 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md` 持续执行，直到最终验收清单全部满足，不能用主观百分比、局部测试通过或 fake/stub 覆盖来宣称完成。

当前执行起点：

- `4be4459 feat: close multi-surface parity baseline` 已推送。
- `e1e73f1 docs: add 100 percent parity multi-agent plan` 已推送。
- 下一步从 Phase 1 `上游 inventory freeze` 开始，建立完整 parity matrix，然后进入 critical contract closure。

目标只能在以下条件全部满足后标记 complete：

- 上游 `packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods` 的用户可见功能、协议、命令、配置、环境变量、错误语义、运行日志、持久化 schema 和发布脚本都有 Tau 对应实现、测试、真实 e2e 证据，或有用户明确确认的非目标说明。
- parity matrix 所有条目为 `verified` 或用户确认的 `non-goal`。
- `next.md` 不再包含未完成 pi-mono parity 缺口。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 通过。
- 真实外部 e2e matrix 通过：provider/OAuth/AWS/Slack/Docker/Pods/WebUi browser smoke；缺凭证或缺环境不能标完成，只能保留 `external-e2e-needed`，最终必须清零或获得用户确认的非目标决策。
- release/CI 能产出并验证真实 Tau 可执行交付件。
- 每个实质变更都有 `docs/histories/YYYY-MM/**` 记录；active plan、`next.md`、`docs/QUALITY_SCORE.md` 和必要架构文档同步。

如果目标尚未满足，就继续执行下一轮。不要问“要不要继续”。只有存在真正歧义且继续会产出与用户意图相反的结果时才停下来问。

## Source of truth

每轮先按这个顺序建立事实面：

1. `AGENTS.md`
2. `docs/REPO_COLLAB_GUIDE.md`
3. `docs/ARCHITECTURE.md`
4. `docs/QUALITY_SCORE.md`
5. `docs/PLANS_GUIDE.md`
6. `docs/HISTORY_GUIDE.md`
7. `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
8. 其它 active execution plans
9. `next.md`
10. 当前 `git status --short --branch`
11. Tau 当前源码、测试和上游 `pi-mono-main` 对应源码

不要只读文档就下结论。每个实现切片必须对照真实上游源码和 Tau 当前代码。

## Core loop

每一轮严格执行 MAGI 大轮次。

### 1. 审视

- 对照上游 `pi-mono-main` 和 Tau 当前实现。
- 更新或维护 parity matrix。
- 识别下一批最有价值、可并行、互不冲突的移植切片。
- 明确每个切片的验收标准：行为合同、测试、smoke、是否需要真实外部 e2e。
- 如果需要改共享 contract，主控先做 contract 设计，不把冲突边界直接丢给 worker。

### 2. 执行

- 默认使用 3-5 个 worker 并行；切片耦合高时降到 2-3 个。
- worker 只改自己的模块和相邻测试，不改共享文档、solution、scripts 或其它 worker ownership。
- 主控可以同时处理共享接口、验证脚本、parity matrix 或一个不与 worker 冲突的切片。
- worker 完成后必须交付可审查输出，主控不能盲信“已完成”。

### 3. 提升

- 主控整合 worker 结果，修冲突，补缺口。
- 运行 targeted tests 和必要项目级验证。
- 同步 `next.md`、active plan、`docs/QUALITY_SCORE.md`、必要架构文档和 history。
- 每个可评审、可验证的完整单元形成清晰 commit 边界；不要把未经验证的 worker WIP 混进提交。
- 如果 parity matrix 仍有未完成项，直接进入下一轮。

## 100% parity phases

必须按 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md` 推进。

### Phase 1：上游 inventory freeze

目标：建立完整 parity matrix，避免继续靠零散 `next.md` 条目推进。

必须覆盖：

- `packages/ai`
- `packages/agent`
- `packages/coding-agent`
- `packages/tui`
- `packages/web-ui`
- `packages/mom`
- `packages/pods`
- root scripts、test scripts、release scripts、package manifests

matrix 每个条目至少包含：

- 上游文件/命令/协议/env/config/schema/log/release 行为。
- Tau 目标路径。
- 当前状态：`verified`、`ported`、`partial`、`missing`、`external-e2e-needed`、`non-goal-proposed`。
- 证明方式：test、smoke、真实 e2e、文档或用户决策。

Phase 1 完成标准：

- 没有“未知是否需要移植”的上游目录。
- 后续 worker 都能从 matrix 领任务，而不是重新猜范围。

### Phase 2：Critical contract closure

目标：先关闭会阻塞其它模块的公共合同。

优先级：

- `Tau.Ai`：provider/auth/model/config/secret/runtime log/provider e2e 合同。
- `Tau.Agent`：agent facade、tool execution、event/correlation 合同。
- `Tau.CodingAgent`：JSONL session tree、RPC、tools、share/export/settings/OAuth 合同。
- `Tau.Tui`：terminal host、overlay、selector、theme、hardware cursor 合同。
- `Tau.WebUi`：Web session 与 CodingAgent JSONL branch/tree 持久化合同。
- `Tau.Mom`：Slack event/message/session/tool/status schema。
- `Tau.Pods`：SSH/HF/vLLM operation result、failureKind、rollback、runtime log schema。

Phase 2 完成标准：

- public contract 有测试固定。
- worker 不需要反复改对方模块内部类型才能继续。

### Phase 3：Product runtime parity

目标：补齐用户真实会碰到的产品行为。

重点 lanes：

- CodingAgent + Tui：TreeSelector、metadata inspector、branch/session shortcuts、summary hooks、compaction/retry UI、settings/model/OAuth UI、extension runtime、theme watcher、resource selector、TUI 主屏、overlay compositor、autocomplete popup、Markdown/highlight/custom renderer、share viewer。
- WebUi：CodingAgent JSONL branch/tree true persistence/import、settings/auth/model dialogs、tool/artifact renderers、attachment/streaming/thinking/tool timeline/problem details browser regression、static asset packaging。
- Mom：real Slack smoke、Slack session sync、Docker sandbox smoke、multi-message delegation e2e、runtime trace/correlation 贯通。
- Pods：real SSH smoke、real HF download smoke、real setup run smoke、real vLLM startup/health smoke、多版本 rollout/rollback、transport hardening。

Phase 3 完成标准：

- fake tests 与真实 smoke 分开记录。
- 任何 fake-only 外部集成必须继续标 `external-e2e-needed`。

### Phase 4：External e2e closure

目标：清掉所有 `external-e2e-needed`。

必须覆盖：

- AI providers：text stream、tool call/tool result、vision where supported、auth refresh/error。
- OAuth：OpenAI Codex、Anthropic、Gemini CLI/Antigravity、GitHub Copilot device flow。
- Bedrock/AWS：SigV4、SSO refresh、registration renewal、AssumeRole/profile/credential_process。
- Slack：Socket Mode receive/ack/respond/update/delete/download/stop。
- Docker：Mom sandbox validate + tool execution。
- Pods：SSH/HF/GPU/vLLM full path。
- WebUi：browser smoke with real static/UI flow。

记录要求：

- 命令、环境变量名称、脱敏结果、日志路径、通过/失败必须写入 plan/history 或专门 e2e 记录。
- 敏感值只记录存在和来源类型，不回显 token、JWT、client secret、API key。

### Phase 5：Release、CI 与安装交付 parity

目标：Tau 能以真实交付物发布和验证。

必须对照：

- 上游 `scripts/build-binaries.sh`
- 上游 `scripts/release.mjs`
- 上游 root `test.sh` / `pi-test.sh`
- Tau `scripts/verify-dotnet.ps1`
- Tau CI/release artifacts

完成标准：

- release artifact 是 Tau 真实可执行产物。
- 干净目录可以启动核心命令。
- CI 和本地 PowerShell gate 对同一套 public behavior 给出一致信号。

### Phase 6：Final 100% acceptance

完成最终验收后：

- 关闭或归档 active plans。
- `docs/QUALITY_SCORE.md` 反映最终水位。
- `next.md` 无未完成 pi-mono parity 缺口。
- 本 goal 才能标记 complete。

## Multi-agent ownership

Main Integrator 独占：

- `GOAL.md`
- `AGENTS.md`
- `README.md`
- `next.md`
- `docs/**`
- `scripts/**`
- solution/project 组织文件
- 最终验证、history、commit 边界

Inventory/QA Agent：

- 只读扫描上游和 Tau。
- 维护 parity matrix 或输出 matrix patch 建议。
- 不改业务代码。

Module workers：

- `AiWorker`
  - 允许：`src/Tau.Ai/**`、`tests/Tau.Ai.Tests/**`
  - 上游：`packages/ai/**`
- `AgentWorker`
  - 允许：`src/Tau.Agent/**`、`tests/Tau.Agent.Tests/**`
  - 上游：`packages/agent/**`
- `CodingAgentWorker`
  - 允许：`src/Tau.CodingAgent/**`、`tests/Tau.CodingAgent.Tests/**`
  - 上游：`packages/coding-agent/**`
- `TuiWorker`
  - 允许：`src/Tau.Tui/**`、`tests/Tau.Tui.Tests/**`
  - 上游：`packages/tui/**`
- `WebUiWorker`
  - 允许：`src/Tau.WebUi/**`、`tests/Tau.WebUi.Tests/**`
  - 上游：`packages/web-ui/**`
- `MomWorker`
  - 允许：`src/Tau.Mom/**`、Mom 相关 `tests/Tau.Agent.Tests/**`
  - 上游：`packages/mom/**`
- `PodsWorker`
  - 允许：`src/Tau.Pods/**`、`tests/Tau.Pods.Tests/**`
  - 上游：`packages/pods/**`

默认禁止 worker 修改：

- `GOAL.md`
- `AGENTS.md`
- `README.md`
- `next.md`
- `docs/**`
- `scripts/**`
- `.sln` / `.slnx`
- `Directory.Build.props`
- central package/build files
- 其它 worker ownership 内的文件

如果两个切片需要改同一 shared contract，停止并行实现，由 Main Integrator 先完成 contract，再重新派发。

## Worker task template

```text
你是 Tau 移植 worker，只负责一个模块切片。
你不是独自在仓库里工作，当前可能有其它 worker 并行修改不同模块。不要 revert 他人改动，不要改共享文档，除非本任务明确授权。

必须先读：
- AGENTS.md
- docs/REPO_COLLAB_GUIDE.md
- docs/ARCHITECTURE.md
- docs/QUALITY_SCORE.md
- docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md
- 与本模块相关的 Tau 源码和测试
- C:\Users\zhouh\Desktop\pi-mono-main 中对应源模块

你的 ownership：
- 允许修改：
  - <Tau 目标目录>
  - <Tau 目标测试目录>
- 禁止修改：
  - GOAL.md
  - README.md
  - next.md
  - docs/**
  - scripts/**
  - solution/project 组织文件
  - 与其它 worker ownership 重叠的文件

任务：
1. 对照 pi-mono-main 的 <源模块路径>，识别 Tau 当前缺口。
2. 在 Tau 的 <目标模块路径> 中实现一个最小、完整、可测试的移植切片。
3. 添加或更新对应测试。
4. 运行相关 build/test。
5. 最终报告：
   - 源文件依据
   - 修改文件
   - 行为对齐点
   - 有意偏离点
   - 验证命令和结果
   - 剩余缺口，按 missing/partial/blocked/external-e2e-needed 分类
   - 是否触碰 public API、持久化格式、secret/auth、CLI/RPC/HTTP contract 或 runtime log schema
```

## Validation gates

Worker 阶段优先跑模块级验证：

```powershell
dotnet build src\<Module>\<Module>.csproj --no-restore --verbosity minimal
dotnet test tests\<Module.Tests>\<Module.Tests>.csproj --no-restore --verbosity minimal
```

主控阶段串行跑项目级验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
```

Docs-only 变更至少跑：

```powershell
git diff --check
```

验证规则：

- 不并行跑会写同一 `bin/obj/output/.tau` 的 build/test/smoke。
- Windows 本机优先 PowerShell 验证链；`/bin/bash` 缺失是环境现实，不是 Tau 代码失败。
- 报错后先读日志和真实输出，不盲目重试。
- 文件锁类 Windows/Roslyn 问题可以 `dotnet build-server shutdown` 后重试一次，但必须记录原因。

## Stop, blocked, and complete rules

不要因为任务大、复杂、尚未完成或需要下一轮就停下。继续选择下一批最有价值的切片推进。

允许停下来问用户的唯一情况：

- 存在真正歧义，继续会产出与用户意图相反的成果。

不允许停下来问：

- 可逆实现细节。
- 是否继续下一步。
- 是否补 history、plan、验证。
- 是否运行下一轮可执行的 parity 切片。

只能在同一外部条件连续三轮阻塞、且没有其它可推进 parity 工作时标记 blocked。

只能在 Final 100% acceptance 全部满足后标记 complete。

## Commit and history discipline

- 实质变更必须写 `docs/histories/YYYY-MM/**`。
- 行为变化必须同步相关 docs；但不要为内部 helper 数量变化做低收益大段文档同步。
- 每个 commit 是可评审、可验证的自洽单位。
- 不要提交未验证 worker WIP。
- 不要把敏感值写入 history、plan、日志摘录或 commit message。

## Non-negotiable constraints

- 不改写上游 `pi-mono-main`。
- 不为了形式一致逐字搬 TypeScript 目录结构；Tau 可以使用 .NET-native 抽象，但外部行为必须对齐。
- 不把 fake/stub tests 当真实 e2e。
- 不把 `external-e2e-needed` 当完成。
- 不把 `next.md` 中仍存在的 parity 缺口忽略掉。
- 不在 worker 中修改共享 docs 或其它 worker 文件。
- 不 revert 用户或其它 worker 的改动，除非用户明确要求。
