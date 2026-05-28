# 2026-05-28 100% 多 Agent 移植计划

## 用户诉求

先提交当前 git 变更并 push，然后制定一份详细的多 Agent 移植计划，目标按 100% 移植率收口。

## 主要变更

- 已先把当前多面 parity baseline 提交为 `4be4459 feat: close multi-surface parity baseline` 并推送到 `origin/main`。
- 新增 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`，把后续移植收口定义为 inventory、行为合同、真实 e2e、release artifact 和 docs/history 闭环共同判定。
- 在 `next.md` 顶部增加本计划指针，让后续 Agent 从最终 100% parity plan 接管，而不是只沿旧 plan 或零散缺口推进。

## 设计意图

当前 Tau 已有较大的多模块 baseline，但距离 100% pi-mono parity 仍有真实外部 e2e、完整 TUI/CodingAgent parity、Slack/Docker/SSH/HF/vLLM smoke、WebUi branch/tree semantic import、release/CI 产物等缺口。新计划把多 Agent 并行工作拆成可审计的模块边界，并明确 Main Integrator 负责共享文档、验证、提交和最终验收，避免 worker 之间互相踩共享文件或把 fake tests 当成真实完成。

## 关键受影响文件

- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/histories/2026-05/20260528-1032-100-percent-multi-agent-parity-plan.md`
- `next.md`

## 验证

- docs-only 变更，未改业务代码。
- 执行 `git diff --check` 检查文档 diff 格式。
