## [2026-05-15 10:44] | Task: CodingAgent HTML plaintext links

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续推进 Tau.CodingAgent 的 pi-mono parity 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export

**Key Actions:**

* **[HTML rendering]**: 在普通文本段中识别 Markdown-style `[label](http/https...)` 和裸 `http(s)` URL，渲染为安全外链。
* **[Safety boundary]**: 保持 code fence 内 URL 不链接，非 http(s) scheme 继续按普通文本安全输出。
* **[Regression]**: 新增 HTML export 回归，覆盖 Markdown 链接、裸 URL、code fence 内 URL 和 `javascript:` scheme。
* **[Docs]**: 同步 README、架构、质量评分、active plan 和 next backlog，明确这是 plaintext link rendering baseline，不是完整 Markdown/highlight/custom renderer。

### 🧠 Design Intent (Why)

HTML transcript 已经具备 code fence、tool result folding 和 tool call JSON formatting；普通文本里的链接仍不可点击，影响导出和 `/share` 后的阅读体验。本次只在非 code fence plaintext 段做最小链接化，继续保留 `<pre>` 换行语义，并限制为 http/https，避免把完整 Markdown renderer 或 custom tool renderer 提前写成完成。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
