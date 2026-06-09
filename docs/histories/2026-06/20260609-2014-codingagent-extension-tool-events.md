# 2026-06-09 20:14 | Task: CodingAgent extension tool event hooks

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，推进下一个可验证移植切片。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* 对照上游 `core/extensions/types.ts`、`core/extensions/runner.ts` 和 `core/agent-session.ts`，补齐 JS/TS limited runtime 中 `pi.on("tool_call")` / `pi.on("tool_result")` 的本地 hook baseline。
* `CodingAgentExtensionCommandStore` 现在记录 supported event handlers，并暴露 `LoadToolInterceptors()`。
* 新增 `CodingAgentExtensionToolEventInterceptor`，通过现有 `IToolInterceptor` 接入 runner：`tool_call` 可阻断工具调用并返回 reason，`tool_result` 可改写文本 content、JSON details 和 isError。
* `Program.cs` 在创建默认 runner 时把 extension tool interceptors 注入 Agent runtime。
* 新增回归测试覆盖 handler discovery、`tool_call` block 和 `tool_result` rewrite。
* 同步 `GOAL.md`、active plan、parity matrix、`next.md` 和 `docs/QUALITY_SCORE.md`，明确该切片不关闭 event.input mutation、完整 lifecycle events、flags/shortcuts、rich content/renderers/details、真实 extension UI、运行中 `/reload` tool hot-swap 或 package/network smoke。

### Design Intent (Why)

上游把 extension tool hooks 安装在 Agent before/after tool call 层，而 Tau.Agent 已有相同语义的 `IToolInterceptor`。本轮选择复用现有拦截器而不是改 Agent 内核，保持 shared runtime 风险最小，同时让 CodingAgent extension runtime 进入真实工具调用路径，不再只是 command/tool execute 的孤立 shim。

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionCommandStore|RuntimeCodingAgentRunner" --no-restore --verbosity minimal`：39/39 通过。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：516/516 通过。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；`Tau.Ai.Tests` 280、`Tau.Agent.Tests` 121、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 516、`Tau.WebUi.Tests` 61、`Tau.Pods.Tests` 216。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionToolEventInterceptor.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
