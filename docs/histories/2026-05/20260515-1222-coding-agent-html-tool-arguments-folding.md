## [2026-05-15 12:22] | Task: CodingAgent HTML tool arguments folding

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / Tau repo`

### 📥 User Query

> 继续推进 Tau.CodingAgent HTML transcript export / richer rendering parity 的相邻小切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` HTML transcript export、router tests、parity docs

**Key Actions:**

* **长参数折叠**: 为超长 `ToolCallContent.Arguments` 增加 `<details class="tool-call-arguments-fold">` 默认折叠，summary 显示原始字符数，折叠内保留完整内容。
* **渲染保持**: 折叠内继续复用现有 JSON `code-block` 格式化；不可解析参数仍走 raw `<pre>` fallback，并继续 HTML escape。
* **回归覆盖**: 新增 HTML export 测试覆盖长 JSON arguments、长 raw arguments、全文保留和短 arguments 不折叠。
* **文档同步**: 更新 README、架构、质量评分、active plan 与 next，明确这是 Tau-native baseline，不等于完整 Markdown/highlight/custom tool renderer 或 richer template parity。

### 🧠 Design Intent (Why)

大 JSON tool arguments 会在导出的 HTML transcript 中淹没对话主线。当前 Tau exporter 已有长 tool result 折叠和 tool call JSON 格式化，最小正确做法是在参数过长时只增加一层可展开容器，不截断、不改协议、不引入完整 custom tool renderer。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
