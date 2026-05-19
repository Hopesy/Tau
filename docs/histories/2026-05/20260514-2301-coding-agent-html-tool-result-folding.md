## [2026-05-14 23:01] | Task: CodingAgent HTML long tool result folding baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续推进 Tau.CodingAgent P2 parity，把 HTML richer rendering 的相邻缺口继续收口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript exporter、命令路由测试和移植计划文档。

**Key Actions:**

* **[HTML exporter]**: 对 `ToolResultMessage` 中超过阈值的长文本输出默认渲染为 `<details class="tool-result-fold">`，summary 显示字符数，正文保留完整内容。
* **[Renderer reuse]**: 折叠正文继续复用现有 text/code fence renderer，因此 fenced code block 与 HTML escape 行为保持一致。
* **[Regression test]**: 增加 `/export <path.html>` 测试，覆盖长 tool result 折叠、字符数 summary、unsafe 文本转义、code fence 渲染，以及普通 assistant 长文本不折叠。
* **[Docs]**: 同步 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next.md，明确这是 Tau-native baseline，仍不是完整 Markdown/highlight/custom tool renderer 或上游 richer HTML template parity。

### 🧠 Design Intent (Why)

HTML transcript 已经能展示 branch timeline、deep-link 和文本 code fence，但大段工具输出仍会淹没可读内容。这里先做最小可审计切片：只折叠长 `ToolResultMessage` 文本，不截断、不影响普通 user/assistant 内容，也不引入外部 Markdown/highlight 依赖。这样能改善导出/分享阅读体验，同时保持后续完整 renderer/template parity 的边界清楚。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
