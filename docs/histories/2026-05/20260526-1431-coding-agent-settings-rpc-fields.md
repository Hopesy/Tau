## [2026-05-26 14:31] | Task: CodingAgent settings RPC fields

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `PowerShell / .NET 10`

### User Query

> 继续多轮移植，移植速度优先，少做低收益文档和单元测试。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **Settings snapshot/document**: 在 `CodingAgentSettingsStore` 中补齐上游 settings-manager 已有的 shell、npm、startup/changelog/telemetry、terminal、images、markdown、cursor 与 editor numeric settings 字段。
* **RPC settings contract**: `get_settings` / `update_settings` 现在可读取和写入新增字段；`markdown.codeBlockIndent` 保留空格，`npmCommand` 保留 argv 顺序和重复项。
* **Focused coverage**: 扩展 settings store round-trip、RPC get/update 和 invalid input tests，覆盖新增字段、bounded numeric clamp 和 nested object validation。
* **Minimal docs**: 只同步 `next.md` 的当前 parity 状态，不扩写大段架构文档。

### Design Intent (Why)

上游 `packages/coding-agent/src/core/settings-manager.ts` 已暴露这些设置。Tau 之前的 RPC settings surface 只覆盖默认模型、tree filter、retry、thinking、queue、auto-compaction、theme 和 enabled models，headless client 无法读写 terminal/images/markdown/shell 这类无副作用配置。该切片只固定持久化和 RPC 合同，不接 package install/runtime、不执行 telemetry，避免把尚未移植的上游运行面伪装成完成。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `next.md`

### Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
  * Passed: 0 warnings, 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentSettingsStoreTests|FullyQualifiedName~CodingAgentRpcHostTests" --verbosity minimal`
  * Passed: 53/53.
