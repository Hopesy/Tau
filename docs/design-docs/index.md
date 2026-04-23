# 设计文档索引

这个目录用于集中存放 Tau 的长期设计判断，重点记录那些会影响实现顺序、模块边界和宿主选择的设计决策。

## 当前已落地的文档

- `core-beliefs.md`
  - Agent-first 的长期工作原则
- `../DESIGN.md`
  - 当前阶段的交互与宿主设计原则
- `../ARCHITECTURE.md`
  - Tau 与 `pi-mono` 的结构映射和模块边界
- `../exec-plans/active/2026-04-23-tau-port-baseline.md`
  - 当前真实实施计划，定义了 CLI-first 的阶段收口顺序

## 当前阶段关注主题

当前最需要持续维护的设计主题不是视觉风格，而是：

- 为什么 Tau 先走 CLI-first
- `Tau.Ai / Tau.Agent / Tau.CodingAgent / Tau.Tui` 的职责边界
- `WebUi / Mom / Pods` 为什么暂时只规划不实作

如果后续产生新的长期设计判断，优先新增独立文档，而不是把所有内容继续堆到一个总文档里。
