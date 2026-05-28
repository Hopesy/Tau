# 2026-05-28 parity matrix inventory start

## 用户诉求

用户要求开始持续执行 100% pi-mono parity `/goal`。

## 主要变更

- 创建 `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 作为 Phase 1 上游 inventory freeze 的长期矩阵入口。
- 记录上游 package 初始文件计数、root build/test/release 脚本清单和 Tau 当前等价面。
- 把已知真实外部 e2e 缺口集中成初始 `external-e2e-needed` 列表，后续按 explorer 输出继续扩展。
- 合并 capability-level inventory：AI/Agent、CodingAgent/Tui、WebUi、Mom、Pods/root scripts 均已有 upstream evidence、Tau target、status 和 gap。
- 记录多 Agent explorer 的现实结果：Mom explorer 完成；WebUi、Pods/root scripts 和 CodingAgent/Tui retry 因额度或服务错误中断，主控改用本地扫描补齐本轮 matrix，不让 Phase 1 卡在工具状态上。
- 更新 100% parity active plan 与 `next.md`，明确当前已完成 capability-level inventory，但 file-level mapping 和命令/API/env/config/log/schema 子矩阵仍未冻结。

## 设计意图

100% parity 不能靠零散 `next.md` 条目推进，需要一个可持续维护的 matrix，把上游源文件、Tau 目标路径、状态和验证证据放在同一位置。第一步先落骨架和 root scripts 事实面，再把模块级能力缺口收敛到同一 matrix；后续继续扩成 file-level mapping，避免在没有清单的情况下盲目开实现切片。

## 关键受影响文件

- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
- `docs/histories/2026-05/20260528-1100-parity-matrix-inventory-start.md`

## 验证

- docs-only 变更，未改业务代码。
- 执行 `git diff --check` 检查文档 diff 格式。
