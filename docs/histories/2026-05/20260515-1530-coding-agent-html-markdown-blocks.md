## [2026-05-15 15:30] | Task: CodingAgent HTML markdown block rendering

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau repo`

### User Query

> 继续推进 Tau.CodingAgent HTML transcript export / richer rendering parity 的相邻小切片。

### Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export、router tests、parity docs

**Key Actions:**

* **[Markdown block baseline]**: 普通文本段检测到 heading/list/blockquote block marker 时，改用轻量 rich-text block rendering，输出 `h1`-`h6`、`ul/ol/li` 和 `blockquote`。
* **[安全渲染复用]**: heading、list item、quote 与 paragraph 内容继续复用现有 inline code / link renderer，并保持 HTML escape。
* **[Code fence 边界]**: fenced code block 仍走独立 `code-block` renderer，代码里的 `#`、`-`、`>` marker 不触发 block rendering。
* **[回归测试]**: 新增 HTML export 测试覆盖 heading、paragraph inline code/link、无序列表、有序列表、blockquote，以及 code fence 内 marker 不被结构化渲染。
* **[文档同步]**: 更新 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next，明确这是 Tau-native block rendering baseline，不等于完整 Markdown/highlight/custom renderer。

### Design Intent

HTML transcript 里标题、步骤列表和引用是高频结构。这个切片只补最容易影响可读性的 block markers，避免把完整 Markdown parser、syntax highlighting、table/image markdown 或 custom tool renderer 混进当前 standalone exporter。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
