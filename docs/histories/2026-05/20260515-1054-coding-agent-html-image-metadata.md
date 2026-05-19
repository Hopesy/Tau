## [2026-05-15 10:54] | Task: CodingAgent HTML image metadata

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续推进 Tau.CodingAgent 的 pi-mono parity 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export

**Key Actions:**

* **[HTML rendering]**: `ImageContent` 在 HTML transcript 中继续以内嵌 data URI 渲染，并新增 caption。
* **[Metadata]**: caption 显示图片 mime type 和根据 base64 payload 估算的字节数。
* **[Regression]**: 新增 HTML export 回归，覆盖图片 data URI、mime type 和 `5 bytes` caption。
* **[Docs]**: 同步 README、架构、质量评分、active plan 和 next backlog，明确这是 image metadata caption baseline，不等于完整附件/gallery parity。

### 🧠 Design Intent (Why)

HTML transcript 已能展示图片，但缺少可审计的基础元信息。导出和 `/share` 场景里，mime type 与大小是最小有用的 inspectability 信息。本次只补 caption，不改变图片 payload、不引入下载协议或附件管理层，避免把 WebUi attachment parity 或 richer HTML template 写成完成。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
