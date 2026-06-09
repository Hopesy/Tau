## [2026-06-09 17:54] | Task: CodingAgent JavaScript extension command runtime

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, dotnet, local Node subprocess`

### User Query

> 继续 `GOAL.md` 100% pi-mono parity 主线，不停止，继续关闭可验证迁移缺口。

### Changes Overview

**Scope:** `Tau.CodingAgent` extension command runtime, `/extensions` display, CodingAgent tests, parity docs.

**Key Actions:**

* 新增 `CodingAgentJavaScriptExtensionRuntime`，通过本地 Node 子进程加载 `.js` extension module，支持 default export factory 和 limited Tau runtime payload。
* 让 `.js` extension 的 `pi.registerCommand(...)` 进入 `CodingAgentExtensionCommandStore`，在 `/extensions` 中显示为 `(project, javascript)`，module status 显示 `loaded; commands N; limited runtime`。
* 让 `TryInvoke` 可执行 JS command handler：handler 返回值转 status text，`pi.sendMessage(...)` / `ctx.sendMessage(...)` 转 runner input，失败和 timeout 变为结构化 diagnostic/error。
* 保留 `.ts` module 为 `runtime pending`，并把 `registerTool`、`registerFlag`、`registerShortcut`、events、message renderer、provider registration 计入 unsupported summary，不伪造成已支持。
* 新增 focused tests 覆盖 JS command load、invoke runner message、handler status return、load failure diagnostic，以及 `/extensions` router display。
* 同步 `GOAL.md`、active plan、parity matrix、`next.md`、`docs/QUALITY_SCORE.md`，明确本轮只关闭 `.js registerCommand` baseline。

### Upstream Evidence

* `packages/coding-agent/src/core/extensions/loader.ts`：extension factory 创建 `ExtensionAPI`，`registerCommand(name, options)` 写入 extension command map，`registerTool/registerFlag/registerShortcut/on` 是更宽的 extension runtime surface。
* `packages/coding-agent/src/core/extensions/runner.ts`：`resolveRegisteredCommands()` 处理重复 invocation name，`getCommand(name)` 按 slash invocation 查找，`createCommandContext()` 构造 command handler context。
* `packages/coding-agent/src/core/agent-session.ts`：`_tryExecuteExtensionCommand(text)` 解析 slash command，调用 `command.handler(args, ctx)`；上游注释说明 extension commands 通过 `pi.sendMessage()` 自己管理 LLM 交互。

### Design Intent

这个切片优先选择最小可运行基线，而不是一次性移植完整 TypeScript extension host。`.js registerCommand` 是上游 extension runtime 中低歧义、可本地验证、对 slash command 用户可见的第一层执行合同；用 Node subprocess 隔离 JS module load/handler side effects，避免把 JS runtime 状态直接嵌入 .NET 进程。

当前刻意不声明完成 TypeScript/jiti runtime、custom tools、flags、shortcuts、events lifecycle、extension UI real calls、package consumer smoke 或最终 extension runtime `verified` 状态。

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "ExtensionCommandStore|ExtensionsCommand" --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `git diff --check`
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`

Final counts after this slice: focused CodingAgent 13/13, full `Tau.CodingAgent.Tests` 503/503, project gate Ai 280 / Agent 121 / Tui 251 / CodingAgent 503 / WebUi 61 / Pods 216.

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentJavaScriptExtensionRuntime.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionCommandStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionCommandStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
