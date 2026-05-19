## [2026-05-15 13:28] | Task: CodingAgent HTML inline code rendering

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau repo`

### 📥 User Query

> 继续推进 Tau.CodingAgent HTML transcript export / richer rendering parity 的相邻小切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export、router tests、parity docs

**Key Actions:**

* **[Inline code baseline]**: 普通文本段新增 backtick inline code span 渲染，输出 `<code class="inline-code">`，并继续做 HTML escape。
* **[Link 边界]**: inline code 内的 URL 不再进入裸链接解析；fenced code block 仍走独立 `code-block` 渲染，不复用 inline code 逻辑。
* **[回归测试]**: 新增 HTML export 测试覆盖 inline code escape、inline code 内 URL 不链接、普通 Markdown-style link / bare URL 继续可链接，以及 code fence 内 backtick 不被 inline 渲染。
* **[文档同步]**: 更新 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next，明确这是 Tau-native inline code baseline，不等于完整 Markdown/highlight renderer。

### 🧠 Design Intent (Why)

HTML transcript 里命令、文件名和短代码片段出现频率高。先支持 backtick inline code span 可以明显提高导出/分享可读性；同时保持解析边界只在普通文本段内生效，避免把完整 Markdown renderer、syntax highlighting 或 custom tool renderer 混入这个切片。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
