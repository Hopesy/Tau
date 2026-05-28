# 2026-05-28 GOAL 100% parity prompt

## 用户诉求

用户希望按照 100% parity 多 Agent 清单使用 Codex `/goal` 命令持续执行，并要求更新 `GOAL.md`。

## 主要变更

- 重写 `GOAL.md`，把旧的通用多 Agent 移植提示升级为可直接供 `/goal` 长期执行的 100% pi-mono parity prompt。
- 明确当前执行起点：baseline commit 和 100% parity plan commit 已推送，下一步从 Phase 1 上游 inventory freeze 开始。
- 把 active plan 的 100% 判定标准、Phase 1-6、worker ownership、validation gates、blocked/complete 规则和 history/commit 纪律写入 `GOAL.md`。

## 设计意图

`GOAL.md` 是长期 `/goal` 入口，必须比普通任务提示更硬。新版本避免后续 agent 把局部实现、fake tests 或未跑真实 e2e 的能力误判为完成，同时把多 Agent 并行边界、主控职责和最终验收条件写清楚，保证持续执行可以从仓库事实恢复。

## 关键受影响文件

- `GOAL.md`
- `docs/histories/2026-05/20260528-1050-goal-100-percent-parity-prompt.md`

## 验证

- docs-only 变更，未改业务代码。
- 执行 `git diff --check` 检查文档 diff 格式。
