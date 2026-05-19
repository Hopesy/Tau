## [2026-05-14 16:00] | Task: coding-agent extension diagnostics

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续继续，完成整个框架的移植。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` JSON extension command/resource diagnostics baseline

**Key Actions:**

* **[Loader status]**: 新增 `CodingAgentExtensionCommandStore.LoadStatus()`，把 command、resource paths、extension JSON 文件明细和 diagnostics 作为同一个可查询事实源暴露出来。
* **[Diagnostics]**: 显式记录坏 JSON、不可读 extension 文件、非 JSON 显式路径和缺失显式路径，不再让这些配置问题在 `/extensions` 中静默表现为 `extensions: none`。
* **[Resource/file details]**: `/extensions` 现在会显示每个 extension JSON 文件路径、command 数量、prompt/skill resource 数量、聚合 resource paths 和重复 command 的最终 invocation names。
* **[Tests/docs]**: 新增 store/router diagnostics 回归；README、ARCHITECTURE、QUALITY_SCORE、active plan 和 `next.md` 同步说明这是 Tau-native JSON diagnostics baseline。

### 🧠 Design Intent (Why)

上游 extension system 会在启动资源列表里显示扩展加载错误、冲突和资源发现问题。Tau 当前还不是完整 TypeScript extension runtime，但声明式 JSON extension baseline 如果继续静默吞掉坏 JSON 或缺失路径，会让用户无法判断是“没有扩展”还是“扩展配置坏了”。

这次选择先把 diagnostics 固定在 JSON loader status 上，而不是直接迁入上游 Jiti/TypeScript runner、custom tools、events、theme loader 或 interactive resource selector。原因是当前 Tau extension 边界仍是可审计的 JSON command/resource；先把本地可验证的加载状态做实，后续完整 runtime diagnostics 再建立在同一 `/extensions` 事实面上。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`

### ✅ Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
* `git diff --check`
