## [2026-06-09 09:34] | Task: WebUi artifacts AgentTool baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows / PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续 100% pi-mono parity 迁移，从 active matrix 的 WebUi sandbox/artifact runtime bridge 缺口继续推进。

### Changes Overview

**Scope:** `Tau.WebUi` plus the shared CodingAgent runner factory boundary.

**Key Actions:**

* 对照上游 `packages/web-ui/src/tools/artifacts/artifacts.ts`，新增 `WebUiArtifactsTool`，暴露 `command/filename/content/old_str/new_str` schema，并通过 session-bound `WebArtifactService.HandleRuntimeMessage(...)` 真实执行 create/update/rewrite/get/delete/logs。
* 让生产 `WebChatService` 在注入 `WebArtifactService` 时通过 `WebUiRunnerFactory` 合并 CodingAgent 默认工具和 WebUi session tools，使模型可调用 `artifacts` tool 写入同一 per-session artifact store。
* 前端 streaming parser 在收到 `tool_end: artifacts` 后刷新 artifact pane，避免 server-side tool 写入后 UI 不更新。
* 新增 `WebUiToolPrompts`，把 artifacts、javascript_repl、attachments、artifacts runtime 和 file-download provider description baseline 移入 Tau.WebUi。
* 新增 `WebUiJavaScriptReplTool` contract helper，保留上游 `title/code` schema，但执行时返回明确 error；测试固定它暂不注册进 session tools，避免把未连接 active browser `window.executeJavaScriptRepl(code)` 的能力伪装成 server-side 可执行 tool。

### Design Intent

上游 `artifacts` tool 的关键行为是模型可直接创建和维护会话侧持久文件。Tau 已经具备 server-backed artifact store、operation semantics、runtime message bridge 和 browser-side REPL host，因此本轮把真正可审计、可执行的部分接入 Agent runner：`artifacts` tool 通过同一个 `WebArtifactService` 改写当前 session 的 artifact state。

`javascript_repl` 不能在服务端 runner 中假装执行，因为上游语义依赖浏览器 sandbox iframe、runtime providers 和 active page message bridge。当前只保留 schema/prompt contract 与 explicit gap，等后续 active browser bridge 能把 AgentTool execution 转发到 `window.executeJavaScriptRepl(code)` 后再注册为可用工具。

### Files Modified

* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.WebUi/Properties/AssemblyInfo.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiRunnerFactory.cs`
* `src/Tau.WebUi/Services/WebUiTools.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiToolsTests.cs`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "WebUiTools|WebUiPageTests" --no-restore --verbosity minimal`
  * Result: passed, 7/7.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`
  * Result: passed, 57/57.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Result: passed. Counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 456, `Tau.WebUi.Tests` 57, `Tau.Pods.Tests` 215.

### Remaining Boundaries

* LLM-facing `javascript_repl` execution through an active WebUi browser bridge remains open.
* JavaScript REPL returned file details/base64 display remains open.
* Full artifact message reconstruction and renderer registry remain open.
* Docx/Excel rich viewers, pdfjs page renderer/view-code toggles and reusable Lit component package remain open.
* Extension sandbox URL mode and release/static browser smoke remain open.
