## [2026-05-23 21:58] | Task: WebUi JSONL redaction boundary

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续推进 Tau 的 pi-mono 移植链路，沿当前 JSONL 脱敏缺口继续收口。

### Changes Overview

**Scope:** `Tau.WebUi`, repo docs

**Key Actions:**

* `WebChatJsonlExporter` 写 WebUi-local JSONL transcript 时默认复用 `JsonlSecretRedactor`，只脱敏 JSON string value，保留 field key、number、bool 和 null。
* `WebChatJsonlImporter` 解析 WebUi-local JSONL 时先按同一规则脱敏每行，再校验和反序列化，避免 imported session 把常见 token pattern 原文持久化进 WebChatStore。
* `CodingAgentJsonlSessionPreviewer` 解析 CodingAgent JSONL preview/import 时也先脱敏 JSON string value，preview text 和 conservative import 生成的 WebChat DTO 都使用脱敏后的内容。
* WebUi endpoint 显式按 `TAU_WEBUI_REDACT_SECRETS` 构造 redactor；设置为 `0/false` 时可关闭该行为。
* README、ARCHITECTURE、QUALITY_SCORE、总 plan、next 和 release notes 同步 WebUi JSONL 脱敏边界与剩余缺口。

### Design Intent (Why)

WebUi 已经具备 WebUi-local JSONL transcript export/import，也具备 CodingAgent JSONL preview-derived conservative import。如果只保护 HTML/Markdown export 或 CodingAgent 自身 tree writer，WebUi JSONL 文件仍会成为独立泄漏面。

本切片复用已有 `JsonlSecretRedactor`，避免为 WebUi 再写一套规则。脱敏只处理 string value，是为了维持 JSONL 结构可解析，避免误改 field key、数值、布尔和 null；非标准 secret pattern 仍需要后续基于真实样本扩展。

### Validation

* `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` passed.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --verbosity minimal` passed with a repo-local ignored NuGet cache at `.dotnet\nuget-packages`: 32/32.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1` passed with the same repo-local NuGet cache: `Tau.Ai.Tests` 205/205, `Tau.Agent.Tests` 76/76, `Tau.Tui.Tests` 110/110, `Tau.CodingAgent.Tests` 346/346, `Tau.Pods.Tests` 66/66.
* `git diff --check` exited 0; Git only reported existing CRLF normalization warnings for `docs/ARCHITECTURE.md`, `docs/QUALITY_SCORE.md`, and `next.md`.
* The global NuGet cache under the user profile had access-denied / stale package symptoms during verification, so this run intentionally used the ignored repo-local package cache instead of deleting or mutating the global cache.

### Files Modified

* `src/Tau.WebUi/Services/WebChatJsonlExporter.cs`
* `src/Tau.WebUi/Services/WebChatJsonlImporter.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/WebChatJsonlExporterTests.cs`
* `tests/Tau.WebUi.Tests/CodingAgentJsonlSessionPreviewerTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `next.md`
