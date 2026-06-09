## [2026-06-09 00:30] | Task: WebUi artifact operation semantics

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的 100% parity，本轮领取 WebUi sandbox/artifact runtime 缺口。

### 🛠 Changes Overview

**Scope:** `Tau.WebUi`、`Tau.WebUi.Tests`、parity plan/docs

**Key Actions:**

* **[Runtime contract]**: 扩展 `WebRuntimeMessageRequest`，新增上游式 `old_str` / `new_str` JSON 字段。
* **[Artifact semantics]**: `WebArtifactService` 新增 `create`、`update`、`rewrite`、`logs` / `htmlArtifactLogs` runtime action，并保留旧 `createOrUpdate` 兼容路径。
* **[Console logs]**: 对 HTML artifact sandbox 的 `console` runtime message 做 bounded in-memory log 收集，让 `htmlArtifactLogs` 可返回当前 artifact 日志。
* **[Frontend bridge]**: HTML artifact iframe bridge 新增 `createArtifact`、`updateArtifact`、`rewriteArtifact`、`htmlArtifactLogs` helper，并在 create/update/rewrite/delete 后刷新 artifact pane。
* **[Tests/docs]**: 新增 endpoint/page 回归，更新 WebUi parity matrix、active plan、`next.md` 和 `QUALITY_SCORE.md`。

### 🧠 Design Intent (Why)

上游 `packages/web-ui/src/tools/artifacts/artifacts.ts` 区分 create、update、rewrite、get、delete、logs。Tau 之前只有 `createOrUpdate`，足够支撑 iframe baseline，但无法固定 LLM-facing artifact tool 的 update-vs-rewrite 合同。本轮先在 server/runtime bridge 层关闭可本地验证的操作语义，为后续 JS REPL 和完整 artifact tool renderer/message reconstruction 留出稳定接口。

### ✅ Validation

* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "ArtifactEndpoints|WebUiPageTests" --no-restore --verbosity minimal`：5/5 通过
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`：50/50 通过
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter WebUiEndpointTests --no-restore --verbosity minimal`：11/11 通过
* `git diff --cached --check`：通过
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 119、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 456、`Tau.WebUi.Tests` 50、`Tau.Pods.Tests` 215
* 临时运行 `Tau.WebUi` 于 `http://127.0.0.1:5088`，Playwright snapshot 确认页面加载到 `Tau Web UI` / Artifacts 面板；DOM 检查确认 `window.createArtifact`、`window.updateArtifact`、`window.rewriteArtifact`、`window.htmlArtifactLogs` 和 `old_str/new_str` bridge 已注入；服务端控制台显示正常启动与 shutdown。

### 📁 Files Modified

* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebArtifactService.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
