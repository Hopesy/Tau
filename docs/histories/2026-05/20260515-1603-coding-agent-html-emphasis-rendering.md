## [2026-05-15 16:03] | Task: CodingAgent HTML emphasis rendering

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau repo`

### User Query

> 继续推进 Tau.CodingAgent HTML transcript export / richer rendering parity 的相邻小切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export、router tests、parity docs

**Key Actions:**

* **[Emphasis baseline]**: 普通文本新增 `**strong**` / `__strong__` -> `<strong>`，`*emphasis*` / `_emphasis_` -> `<em>` 的轻量渲染。
* **[安全渲染复用]**: emphasis 内容继续复用现有 inline code / link renderer，并保持 HTML escape。
* **[边界保护]**: inline code、code fence 与单词内部下划线不触发 emphasis rendering，避免破坏命令、代码和标识符。
* **[回归测试]**: 新增 HTML export 测试覆盖 strong/emphasis、emphasis 内 link/inline code、列表项内 emphasis、code fence 内 marker 不渲染，以及 `foo_bar_baz` 保持字面量。
* **[文档同步]**: 更新 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next，继续明确这是 Tau-native span rendering baseline，不等于完整 Markdown/highlight/custom renderer。

### Design Intent

加粗和斜体是 transcript 里高频的内联语义。这个切片只补最小安全子集，不引入完整 Markdown parser，也不改变 code fence、inline code、tool call 或 custom tool rendering 的现有边界。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
