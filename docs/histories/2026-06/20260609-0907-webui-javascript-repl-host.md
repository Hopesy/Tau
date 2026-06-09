## [2026-06-09 09:07] | Task: WebUi JavaScript REPL host baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows / PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续 100% pi-mono parity 迁移，从 active matrix 领取一个可审计切片推进。

### Changes Overview

**Scope:** `Tau.WebUi`

**Key Actions:**

* 对照上游 `packages/web-ui/src/tools/javascript-repl.ts`、`components/SandboxedIframe.ts` 和 `components/sandbox/*RuntimeProvider.ts`，在 `WebUiPage` 增加 browser-side `window.executeJavaScriptRepl(code)`。
* REPL 每次执行创建独立隐藏 sandbox iframe，注入现有 runtime bridge、当前 session attachment snapshot、artifact helper、`returnDownloadableFile` 和 console capture。
* REPL 执行 async browser JS 后汇总 console output、return value、execution error 和 returned files；完成、错误和 timeout 都清理 iframe 与 active sandbox map。
* REPL mutating artifact operation 复用现有 `/api/sessions/{id}/runtime/messages` 与 `WebArtifactService`，写入同一 per-session artifact store 并刷新 artifact pane。
* 补充静态 HTML 合同测试和真实 Chromium browser flow，验证 REPL 可读取 session attachment、创建/读取 artifact、捕获 console/return value 并返回 one-time file。
* 同步 `docs/QUALITY_SCORE.md`、`next.md`、active parity matrix 和 100% active plan，明确本轮关闭的是本地 browser sandbox execution host，不是 LLM-facing `javascript_repl` AgentTool 完整 parity。

### Design Intent

上游 `javascript_repl` 的关键语义是浏览器 sandbox iframe 执行环境，而不是服务端普通 JS evaluator。Tau 已经在前三个 WebUi artifact/runtime 子切片中具备 artifact store、runtime message endpoint、attachment provider、file-return provider 和 artifact operation semantics，因此本轮选择最小闭环：在页面侧把这些 provider 组合成可执行 REPL host，并用 browser test 固定真实 iframe/message bridge 行为。

本轮没有把 REPL 注册成 Agent 可调用 tool，因为那需要 session-bound WebUi tool factory、tool result file details/base64 和 runner 注入边界，属于后续独立切片。这样可以避免把页面 smoke 误标为完整 upstream package parity。

### Files Modified

* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFlowTests.cs`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "JavaScriptRepl|WebUiPageTests" --no-restore --verbosity minimal`
  * Result: passed, 4/4.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`
  * Result: passed, 53/53.
* `git diff --check`
  * Result: passed; Git only reported existing CRLF-to-LF normalization warnings for touched files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Result: passed. Counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 456, `Tau.WebUi.Tests` 53, `Tau.Pods.Tests` 215.

### Remaining Boundaries

* LLM-facing `javascript_repl` AgentTool registration remains open.
* Prompt constants/runtime provider descriptions remain open.
* Tool result returned file details/base64 persistence/display remains open.
* Full artifact message reconstruction, renderer registry, Docx/Excel rich viewers, pdfjs renderer/toggles and reusable Lit component package remain open.
* Extension sandbox URL mode and release/static browser smoke remain open.
