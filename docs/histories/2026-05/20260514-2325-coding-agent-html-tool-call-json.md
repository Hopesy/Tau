## [2026-05-14 23:25] | Task: CodingAgent HTML tool call JSON arguments baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续推进 Tau.CodingAgent P2 parity，把 HTML richer rendering 的相邻缺口继续收口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript exporter、命令路由测试和移植计划文档。

**Key Actions:**

* **[HTML exporter]**: 将可解析的 `ToolCallContent.Arguments` JSON 格式化为 `code-block` / `<code data-language="json">`，提高 standalone transcript 里的 tool call 可读性。
* **[Fallback]**: 对不可解析的 tool arguments 保留原始 `<pre>` 渲染，并继续做 HTML escape，避免破坏 legacy 或非 JSON tool 参数。
* **[Escaping]**: JSON 格式化阶段保留 `<tau>` 这类原始字符，再交给最终 HTML escape 处理，避免输出 `\u003C...\u003E` 这类不利于阅读的序列。
* **[Regression test]**: 增加 `/export <path.html>` 测试，覆盖 JSON tool arguments 格式化、HTML escape 和 raw fallback。
* **[Docs]**: 同步 README、ARCHITECTURE、QUALITY_SCORE、active plan 和 next.md，明确这是 Tau-native tool call argument rendering baseline，仍不是完整 custom tool renderer 或上游 richer HTML template parity。

### 🧠 Design Intent (Why)

HTML transcript 已经有文本 code fence 和长 tool result 折叠，但 tool call arguments 仍以紧凑原始 JSON 出现，阅读成本高。这个切片只利用已有 `ToolCallContent.Arguments` 字符串做安全、可回退的格式化：解析成功就漂亮打印为 JSON code block，解析失败完全保留旧行为。这样能改善导出/分享可读性，同时不引入 Markdown/highlight 依赖，也不改工具协议。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
