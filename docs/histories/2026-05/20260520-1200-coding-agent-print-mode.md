## [2026-05-20 12:00] | Task: 添加 Print Mode baseline

### 🤖 Execution Context

* **Agent ID**: `claude-code`
* **Base Model**: `Claude Opus 4.7 (1M context)`
* **Runtime**: `Claude Code CLI on Windows`

### 📥 User Query

> 计划模式分析参考项目 pi-mono 中尚未移植到 Tau.CodingAgent 的功能，并按项目规则落到文档后逐步执行。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、`docs/exec-plans/active/`

**Key Actions:**

* **差距分析 plan 落地**: 新增 `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`，按 7 个 tier 整理上游 pi-mono 与 Tau.CodingAgent 的功能差距，并给出 8 个推荐执行切片。
* **Print Mode 实现**: 新增 `CodingAgentPrintMode`，订阅 runner `RunAsync` 流，把 `TextDeltaEvent` 写到 stdout，`AgentEndEvent.ErrorMessage` 和 runner exception 写到 stderr 并返回退出码 1，OperationCanceled 输出 "Cancelled."。
* **CLI 入口接线**: `Program.cs` 提取 `--print/-p` / `--print=value` 参数，print 模式下跳过 InteractiveInputEditor 与 CodingAgentHost，单次执行后退出。
* **测试覆盖**: 新增 4 个 `CodingAgentPrintModeTests`：text delta 流式输出、AgentEnd 错误退出码 1、runner 抛异常退出码 1、cancellation 输出 Cancelled。全量测试 185/185 通过。

### 🧠 Design Intent (Why)

Print Mode 是参考项目里相对独立的运行模式（不依赖 extension runtime / TUI 组件 / event bus），最小投入即可解锁 CI 脚本和自动化批处理场景。先做 text 输出 baseline，复用 runner 现有 AgentEvent 流，跳过 session 持久化和 UI 渲染，让命令行 `tau --print "prompt"` 立即可用；JSON event stream 模式留作后续切片，避免一次性引入 AgentEvent 序列化层。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentPrintMode.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentPrintModeTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
