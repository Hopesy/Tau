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

本目标恢复后，下一步不是重新做 inventory，而是：

1. 先收口当前 dirty WIP 的提交/验证边界：Agent platform baseline 与 `/settings select` UI parity 必须可分开评审。
2. 再从 `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 的 `Phase 2 Candidate Queue` 选择下一个 implementation/e2e worker 切片。
3. 优先选择会解除其它模块阻塞的合同或真实 e2e：AI provider/OAuth real e2e、CodingAgent CLI/RPC/session/config、Tui live terminal/settings runtime、WebUi branch/tree/artifact runtime、Mom Slack/Docker smoke、Pods SSH/HF/GPU/vLLM e2e、release/package final parity。

所有后续完成声明都必须用当前仓库验证和上游对照支撑，不能用历史记忆或文档声称替代证据。
