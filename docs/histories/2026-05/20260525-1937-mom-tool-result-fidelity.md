## [2026-05-25 19:37] | Task: Mom 工具结果保真补齐

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows / PowerShell / dotnet`

### 📥 User Query

> 继续不断移植，在移植进度 100% 前持续制定下一步计划并推进；当前按 pi-mono parity 继续快速移植。

### 🛠 Changes Overview

**Scope:** `Tau.Mom` sandbox tool delegation

**Key Actions:**

* **Bash result fidelity**: `MomBashTool` 在输出截断时把完整 stdout/stderr 写入 temp `mom-bash-*.log`，返回文本提示 `Full output: <path>`，并通过 `MomBashToolDetails` 暴露 truncation 与 `fullOutputPath`。
* **Edit result fidelity**: `MomEditTool` 成功文本补齐 old/new 字符数变化，并通过 `MomEditToolDetails.Diff` 返回带行号的上下文 diff。
* **Targeted coverage**: `MomSandboxAndToolsTests` 补 bash full output path 和 edit diff details 回归，避免退回旧的纯文本成功提示。
* **Plan sync**: `next.md` 与 active migration plan 只做最小同步，明确这只是 Mom sandbox/tool delegation 的相邻 parity 补齐，真实 Docker/Slack smoke 仍未完成。

### 🧠 Design Intent (Why)

上游 mom `bash` 在长输出截断时会保存完整 temp log，并在 details 中返回 full output path；`edit` 成功时会报告字符数变化并返回 diff details。Tau 已经有 `ToolResult.Details` 承载附加信息，因此本轮直接在现有 Mom 工具层补齐行为，不改公共 `IAgentTool` 合同，不引入额外 diff 依赖。

### 📁 Files Modified

* `src/Tau.Mom/MomTools.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
