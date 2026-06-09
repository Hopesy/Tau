## [2026-06-09 10:14] | Task: WebUi JavaScript REPL AgentTool active-browser bridge

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移；从 active matrix / Phase 2 Candidate Queue 继续领取可验证切片。

### Changes Overview

**Scope:** `Tau.WebUi`, WebUi tests, active parity docs/history.

**Key Actions:**

* **Implemented active-browser bridge**: 新增 `WebUiJavaScriptReplBridge`，让 server-side `javascript_repl` AgentTool 入队 session-bound request，由活动 WebUi 页面长轮询领取并通过现有 browser-side `window.executeJavaScriptRepl(code)` sandbox host 执行。
* **Exposed bridge HTTP contract**: 新增 `/api/sessions/{id}/javascript-repl/next` 和 `/api/sessions/{id}/javascript-repl/{requestId}/result`，并在删除 session 时取消 pending REPL request。
* **Registered session tool**: `WebUiRunnerFactory` / `WebChatService` 生产路径现在默认合并 CodingAgent 默认工具、`artifacts` 和 `javascript_repl` WebUi session tools；无活动页面时 `javascript_repl` 返回明确 timeout error，不伪造 server-side JavaScript evaluator。
* **Returned upstream-shaped file details**: browser result 会把 returned files 转成 `fileName/mimeType/size/contentBase64`，`ToolResult.Details` 暴露 `files`。
* **Updated browser page loop**: `WebUiPage` 在打开/创建 session 时启动 polling loop，切 session 时中断旧 loop，`tool_end: artifacts/javascript_repl` 后刷新 artifact pane。
* **Expanded tests**: 覆盖 tool request/result/details、timeout error、runner execution、endpoint long poll/result、页面静态 markers、真实 Chromium 页面领取 pending request 并回传 result。

### Design Intent (Why)

上游 `packages/web-ui/src/tools/javascript-repl.ts` 的核心语义是通过浏览器 sandbox 执行 JavaScript，并把 output 与 returned files 作为 tool result/details 返回。Tau 之前只有 browser-side `window.executeJavaScriptRepl(code)` host 和 LLM-facing schema/gap error；本轮把 server-side AgentTool 与活动浏览器页面桥接起来，保持上游 browser sandbox 语义，同时避免引入错误的 server-side JS evaluator。

### Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "WebUiTools|JavaScriptReplBridgeEndpoints|WebUiPageTests" --no-restore --verbosity minimal` passed 10/10.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "JavaScriptReplBridge" --no-restore --verbosity minimal` passed 2/2.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed 61/61.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter WebUiEndpointTests --no-restore --verbosity minimal` passed 11/11.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed: Ai 280, Agent 119, Tui 251, CodingAgent 456, WebUi 61, Pods 215.

### Files Modified

* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebUiJavaScriptReplBridge.cs`
* `src/Tau.WebUi/Services/WebUiTools.cs`
* `src/Tau.WebUi/Services/WebUiRunnerFactory.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `src/Tau.WebUi/Program.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiToolsTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFixture.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`
* `tests/Tau.CodingAgent.Tests/WebUiEndpointTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
