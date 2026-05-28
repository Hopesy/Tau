## [2026-05-26 14:11] | Task: WebUi current-branch CodingAgent JSONL import

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户要求继续下一轮快速移植，默认多 Agent 并行推进真实 parity 缺口，降低低收益文档和单元测试消耗。

### Changes Overview

**Scope:** `Tau.WebUi`

**Key Actions:**

* **[Import option]**: `POST /api/sessions/import.coding-agent-jsonl` 增加 `currentBranchOnly=true` query 参数；默认未传时仍导入全部 CodingAgent timeline messages。
* **[Conservative branch import]**: WebUi import service 现在把 `CodingAgentJsonlPreviewOptions` 传入 preview parser，`currentBranchOnly=true` 时只把当前 branch 上的 timeline message 持久化为 WebChat messages，off-branch message 仅留在 source tree/audit 元数据。
* **[Audit contract]**: `CodingAgentJsonlImportAuditDto.WillImportCurrentBranchOnly`、`ImportedMessageCount`、`NonImportedEntryCount`、summary 和 persisted `SourceMetadata.Audit` 现在反映实际 import 策略；默认全量 timeline import 行为不变。
* **[Focused coverage]**: 更新 previewer audit 断言，并新增 HTTP endpoint 回归，固定 branched JSONL 下 current-branch import 只生成 `root / left / after summary` 三条 WebChat messages。
* **[Minimal docs]**: `next.md` 只同步 WebUi session lifecycle 行，记录当前 conservative import 支持 `?currentBranchOnly=true`。

### Design Intent (Why)

WebUi 已经能 preview CodingAgent JSONL 的 current branch，但 import 入口只能导入全部 timeline，branched session 会把 off-branch message 也线性化进 WebChat。该切片保持默认导入兼容，同时给 import 增加显式保守模式，让用户可以只导入当前 branch 的可见上下文，并保留完整 source tree/audit 供审计，而不提前把 WebChatStore 改成 CodingAgent branch tree store。

### Files Modified

* `src/Tau.WebUi/WebUiApplication.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `tests/Tau.WebUi.Tests/CodingAgentJsonlSessionPreviewerTests.cs`
* `next.md`

### Validation

* `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` -> 40/40 passed
