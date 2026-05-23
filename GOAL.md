# Tau multi-agent porting goal

## /goal prompt

持续把 `C:\Users\zhouh\Desktop\pi-mono-main` 完整移植到 `C:\Users\zhouh\Desktop\Tau`。

Tau 是 harness-init 协作仓库，必须按 `AGENTS.md` 和 `docs/` 下的协作规范执行：复杂任务维护 active execution plan，实质变更写 history，文档和测试同步。

你是主控 agent / integrator。必须使用多 agent 并行，但只能把互不重叠的模块实现交给 worker。主控负责架构边界、共享 contract、计划、合并、验证、文档和 history。

## Core loop

每轮执行：

1. 读取 `AGENTS.md`、`docs/REPO_COLLAB_GUIDE.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`docs/PLANS_GUIDE.md`、`docs/HISTORY_GUIDE.md`、active execution plan、`next.md`。
2. 对照 `C:\Users\zhouh\Desktop\pi-mono-main` 和 Tau 当前代码，维护源模块到 Tau 模块的 parity matrix。
3. 选择 3-5 个互不冲突的移植切片并行派 worker。每个 worker 只负责一个模块，必须有明确允许修改路径和禁止修改路径。
4. worker 不得修改 `README.md`、`next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`docs/exec-plans/**`、`docs/histories/**`、`docs/releases/**`，除非主控明确授权。
5. 主控同时处理共享接口或关键路径，不要等待 worker 时空转。
6. 收回 worker 结果后审查、集成、修冲突、补测试。
7. 运行相关测试和项目级验证。
8. 主控统一更新 `README.md` / `docs/ARCHITECTURE.md` / `docs/QUALITY_SCORE.md` / `next.md` / active execution plan / release notes / history。
9. 如果 parity matrix 仍有未完成项，继续下一轮；只有全部移植项有代码、测试、文档和验证证据后，目标才能 complete。

## MAGI cycle

每轮必须按三脑循环推进：

1. 审视：对照 `pi-mono-main` 和 Tau 当前实现，找出下一批最有价值、可并行、低冲突的移植切片。
2. 执行：派 worker agent 并行处理互不重叠模块；主控 agent 同时处理关键路径、共享接口或一个不与 worker 冲突的切片。
3. 提升：集成结果后更新架构、计划、质量评分、`next.md` 和 history，并指定下一轮移植计划。

## Multi-agent rules

- 并行度默认 3-5 个 worker；如果切片耦合高，降到 2-3 个。
- 每个 worker 必须有明确 ownership：允许修改哪些目录/文件，禁止修改哪些共享文件。
- 每个 worker 必须知道：不是独自在代码库里工作，不能 revert 他人改动，必须适配并发变更。
- 不要让多个 worker 同时修改同一模块、同一 shared contract 或同一共享文档。
- 不要把关键阻塞问题全部丢给 worker；共享接口、架构边界、最终合并和验证必须由主控负责。
- worker 的输出必须包括源文件依据、修改文件、行为对齐点、有意偏离点、验证命令和结果、剩余缺口。
- 主控必须审查 worker 结果，不盲信、不直接合并未经验证的实现。

默认禁止 worker 修改：

- `AGENTS.md`
- `README.md`
- `next.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/**`
- `docs/histories/**`
- `docs/releases/**`
- `.sln`
- `Directory.Build.props`
- central package files

这些共享文件默认由主控 agent 统一维护。

## Architecture strategy

- 先做 contracts / seams，再做实现。
- 如果多个模块依赖同一接口、DTO、配置、日志或 session 格式，主控 agent 先在 Tau 中建立最小稳定接口，再派 worker 分别实现。
- 不允许盲目照搬 `pi-mono-main` 目录结构；必须结合 Tau 当前架构：
  - `Tau.Ai`：provider、auth、model catalog、observability、通用安全能力。
  - `Tau.CodingAgent`：agent runtime、session、CLI/RPC、tree JSONL、retry、export/share。
  - `Tau.Tui`：终端交互、组件、selector、输入和渲染。
  - `Tau.WebUi`：Web 宿主、session API、导入导出、browser flow。
  - `Tau.Mom`：本地/Slack 风格委派、channel log、prompt debug、sandbox/tool delegation。
  - `Tau.Pods`：部署、probe、exec、vLLM planner。
  - `tests/*`：每个模块对应测试。
- 如果当前架构阻碍移植，可以改架构，但必须先在 active execution plan 中记录：
  - 为什么现有架构不够。
  - 新边界是什么。
  - 哪些模块受影响。
  - 迁移顺序。
  - 回滚或验证策略。

架构原则：

```text
主控 agent 拥有共享边界。
worker agent 拥有模块实现。
```

## Suggested initial workers

- Worker A / Ai：对照 `pi-mono-main` provider/auth/model/observability，移植缺失 provider 能力和安全边界。Ownership: `src/Tau.Ai`, `tests/Tau.Ai.Tests`。
- Worker B / CodingAgent：对照 `pi-mono-main` agent runtime/session/RPC，移植缺失 session、retry、export、command 行为。Ownership: `src/Tau.CodingAgent`, `tests/Tau.CodingAgent.Tests`。
- Worker C / Tui：对照 `pi-mono-main` terminal UI，移植 selector、输入、渲染、快捷键、状态展示。Ownership: `src/Tau.Tui`, `tests/Tau.Tui.Tests`。
- Worker D / WebUi：对照 `pi-mono-main` web/chat/session/import/export，移植 Web API、前端行为、browser flow。Ownership: `src/Tau.WebUi`, `tests/Tau.WebUi.Tests`。
- Worker E / Mom：对照 `pi-mono-main` mom/delegation/slack/sandbox，移植 channel flow、prompt debug、transport seam。Ownership: `src/Tau.Mom` plus Mom-related tests under `tests/Tau.Agent.Tests` when already established。
- Worker F / Pods：对照 `pi-mono-main` deployment/probe/vLLM，移植 pod lifecycle、probe、exec、planner。Ownership: `src/Tau.Pods`, `tests/Tau.Pods.Tests`。

同一轮最多 4 个 worker 真正改代码。其它模块可以先由 explorer 做只读差距分析。

如果两个切片需要改同一共享 contract，停止并行实现，由主控先完成 contract 再重新派发。

## Worker task template

```text
你是 Tau 移植 worker，只负责一个模块切片。
你不是独自在仓库里工作，当前可能有其它 worker 并行修改不同模块。不要 revert 他人改动，不要改共享文档，除非本任务明确授权。

必须先读：
- AGENTS.md
- docs/REPO_COLLAB_GUIDE.md
- docs/ARCHITECTURE.md
- docs/QUALITY_SCORE.md
- 当前 active execution plan
- 与本模块相关的 Tau 源码和测试
- C:\Users\zhouh\Desktop\pi-mono-main 中对应源模块

你的 ownership：
- 允许修改：
  - <Tau 目标目录 1>
  - <Tau 目标测试目录 1>
- 禁止修改：
  - README.md
  - next.md
  - docs/ARCHITECTURE.md
  - docs/QUALITY_SCORE.md
  - docs/exec-plans/**
  - docs/histories/**
  - docs/releases/**
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
   - 剩余缺口
```

## Acceptance

- 每个移植切片必须有可运行验证，不能只说“已实现”。
- 每个模块必须能回答：
  - `pi-mono-main` 源行为是什么。
  - Tau 中移植到哪里。
  - 哪些行为完全对齐。
  - 哪些行为有意偏离，为什么。
  - 哪些测试证明它工作。
  - 哪些缺口仍在 active plan 中。
- 如果 parity matrix 仍有未完成项，就继续选择下一批可验证切片推进。
- 只有 active parity matrix 全部完成，项目级验证通过，文档和 history 同步后，才能标记 goal complete。
- 如果遇到连续三轮被同一外部条件阻塞，才标记 blocked；否则不要因为任务大、复杂或未完全完成就停止。
