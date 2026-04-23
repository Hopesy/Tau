# Execution Plans

这个目录用于存放 Tau 的长期执行计划。

## 当前状态

- 进行中的计划放在 `active/`
- 已完成的计划移到 `completed/`
- 新计划从 `templates/execution-plan.md` 开始
- 暂时不做但需要持续跟踪的问题放到 `tech-debt-tracker.md`

## 当前 active plan

- `active/2026-04-23-tau-port-baseline.md`
  - Tau 当前主计划
  - 目标是把项目从“可编译骨架”推进到“CLI-first 的真实 P0 路径”
  - 当前优先级：`Tau.Tui` → `Tau.CodingAgent` → 测试与 CI → 再评估 `WebUi / Mom / Pods`

如果未来出现并行的长期计划，仍然要保证 `active/` 下的文件数量可控，并且每份计划都能明确回答：

- 它解决的真实问题是什么
- 它当前处于哪个阶段
- 它和主计划是什么关系
