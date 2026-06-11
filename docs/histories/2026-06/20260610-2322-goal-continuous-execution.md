## [2026-06-10 23:22] | Task: 让 `/goal` 成为持续执行主控

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 更新下一步计划，目标是在 `/goal` 命令辅助下完成所有迁移，并把后续详细迁移计划、验收目标和最终目标写进 `goal.md`，保证程序能够按指令持续运行直到迁移完成。

### 🛠 Changes Overview

**Scope:** `GOAL.md`, `next.md`

**Key Actions:**

* **Continuous-execution contract**: 在 `GOAL.md` 顶部新增 `/goal 执行协议`，明确 `/goal` 是持续运行主控入口，收到 `继续` / `继续继续` / `下一步` 时不重新询问是否继续，直接读取当前工作树、`GOAL.md`、`next.md`、matrix、active plan、history 和验证证据，进入下一轮。
* **Loop discipline**: 把每轮固定成 `审视 -> 执行 -> 提升`，要求每次只领取一个互斥切片，完成后同步 `next.md`、`docs/QUALITY_SCORE.md`、active plan、matrix 和 history，再进入下一轮。
* **Stop conditions**: 把可停止条件收窄为三类：当前切片验收完成、用户确认 `non-goal`、或同一阻塞连续三轮复现且确实无法前进。
* **Next queue framing**: 在 `next.md` 顶部补一行，说明它和 `GOAL.md` 一起构成持续运行的 next 队列，`/goal` 继续时永远从当前 active plan 和队列领取下一刀。
* **History**: 记录这次纯文档收口，保证 `/goal` 的持续运行协议、下一步计划和 final audit 目标都有可追溯 history。

### 🧠 Design Intent (Why)

这次不是改移植方向，而是把移植方向写成程序能持续执行的控制协议：`GOAL.md` 负责定义最终目标、分阶段迁移计划和验收门槛，`next.md` 负责当前未完成缺口队列，二者组合后，`/goal` 就可以按固定循环不断推进，而不是在某个 checkpoint 停下等待人工重新发起。这样可以减少上下文漂移，保证每轮都从当前 repo 事实继续前进，直到 final audit 真正闭合。

### ✅ Validation

* 本次为文档收口，没有运行代码测试。
* `git diff --check`：待在本轮最终校验时统一确认。

### 📁 Files Modified

* `GOAL.md`
* `next.md`