## [2026-05-15 22:43] | Task: CodingAgent HTML table rendering

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau repo`

### User Query

> 继续推进 Tau.CodingAgent HTML transcript export / richer rendering parity 的相邻小切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export、router tests、parity docs

**Key Actions:**

* **[Pipe table baseline]**: 普通文本新增常见 Markdown pipe table 渲染，支持 header row、separator row 和 data rows，并输出可横向滚动的 `<table>`。
* **[Inline renderer reuse]**: table header / cell 内容继续复用现有 inline code、link、strong 和 emphasis 安全渲染。
* **[Code fence boundary]**: fenced code block 内的 pipe table 文本保持代码块，不参与 table rendering。
* **[Regression test]**: 新增 HTML export 测试覆盖 table、inline code cell、strong/emphasis cell、Markdown link cell，以及 code fence 内表格文本不被解析。
* **[Docs sync]**: 更新 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next，继续明确这是 Tau-native table rendering baseline，不等于完整 Markdown/highlight/custom renderer。

### Design Intent

表格是 transcript 里状态对照和检查结果的高频结构。这个切片只识别 header + separator + data rows 的常见 pipe table，不引入完整 Markdown parser，也不实现对齐、转义管道或复杂表格扩展语法。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
