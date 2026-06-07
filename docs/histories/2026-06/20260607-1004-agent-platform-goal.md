## [2026-06-07 10:04] | Task: Agent platform goal pivot

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### 📥 User Query

> 先不管 UI，先把整个系统完整搭建，方便用此 Agent 底座搭建 Agent 应用；更新计划，尤其是 `GOAL.md`。

### 🛠 Changes Overview

**Scope:** goal / execution plans / next / quality docs

**Key Actions:**

* **Goal pivot**: 将 `GOAL.md` 从 100% pi-mono product parity 主线改为 `Tau Agent Platform /goal`，明确当前优先搭建可复用 .NET Agent 应用底座。
* **Goal refinement**: 进一步补充 `GOAL.md` 的当前事实面和 immediate execution order，明确 `Tau.Agent` / `Tau.Ai` 已有内核基线，下一步不是重写 runtime，而是补薄 platform facade、delegate tool helper、basic session/log adapter、examples 和 smoke。
* **New execution plan**: 新增 Agent platform baseline 计划，定义平台 API、tool/session/observability、examples、可靠性和交付里程碑；验收后已归档到 `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`。
* **Executable work packages**: 在 active plan 中新增 platform surface checklist 与 WP1-WP5，固定 Agent 创建、provider/model、tool registration、streaming events、usage/stop reason、session/state、observability、examples 和 package boundary 的验收信号。
* **Checkpoint tightened**: 将 `GOAL.md` 和 active plan 从“路线冻结”进一步收紧为当前 checkpoint：Phase 0 已完成，下一步直接进入 WP1/WP2，不再重新讨论是否可行。
* **First API skeleton candidates**: 在 active plan 中明确第一版 API 骨架候选：`AgentApplication`、`AgentApplicationBuilder`、`AgentRunResult`、`DelegateAgentTool`、`IAgentSessionStore`、`AgentSessionSnapshot`、`InMemoryAgentSessionStore` 或等价 UI-free 类型；这些类型只作为现有 Agent/Ai 内核的薄包装，不引入 CodingAgent CLI/TUI public 依赖。
* **Priority sync**: 更新 `next.md` 顶部 P0，明确先推进 `Tau.Agent` / `Tau.Ai` / tool-session-observability / examples / package boundary；TUI/UI polish 和完整 product parity 后置。
* **Parity plan retained**: 在旧 `2026-05-28-tau-100-percent-pi-mono-parity.md` 顶部标注其继续作为长期审计路线保留，但不再是当前优先执行线。
* **Quality note**: 更新 `docs/QUALITY_SCORE.md`，记录这次是计划和质量口径切换，不声明新增运行时代码能力。

### 🧠 Design Intent (Why)

继续追 UI parity 会把当前投入压在 TUI/selector/theme/focus stack 等产品体验细节上；用户当前目标是用 Tau 作为 Agent 底座搭建 Agent 应用。因此先把主线切换到稳定、可复用、可嵌入、可发布的 Agent platform：清晰 public API、provider/tool/session/log 边界、可运行 examples 和验证链。旧 pi-mono parity matrix 仍保留，避免丢失长期审计事实。

二次细化的设计意图是避免“Agent 底座”停留在方向描述：现有 `Agent` facade、`AgentRuntime`、`IAgentTool`、`Faux` provider 和 `JsonlTauLogSink` 已是可复用内核，下一步应在其上补外部应用开发者真正需要的薄入口和示例，而不是把 CodingAgent CLI/TUI 产品类型下沉为 public SDK，也不是继续 UI polish。

三次收紧的设计意图是给后续执行 Agent 一个更窄的落点：当前不再做 broad parity exploration，也不再停留在 docs discussion。下一步应读取 `Tau.Agent` / `Tau.Ai` 当前源码和测试后，在 `Tau.Agent` 内实现最薄 platform API、delegate tool helper、basic session/log adapter 和 targeted tests；examples 与 package boundary 在 API baseline 后推进。

### 📁 Files Modified

* `GOAL.md`
* `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-06/20260607-1004-agent-platform-goal.md`

---

## [2026-06-07 13:30] | Task: Agent platform API and examples baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续，先完成 Agent 底座的完整移植验收，方便其他人用这个包搭建 Agent 应用。

### 🛠 Changes Overview

**Scope:** `Tau.Agent`, examples, verification scripts, goal/plan/quality/architecture/next docs

**Key Actions:**

* **Platform public surface**: 新增 `src/Tau.Agent/Platform/**`，包含 `AgentApplication`、`AgentApplicationBuilder`、`AgentRunResult`、`DelegateAgentTool`、`AgentToolContext`、`IAgentSessionStore`、`AgentSessionSnapshot` 和 `InMemoryAgentSessionStore`。
* **App-facing builder**: 让外部 .NET 应用可以用薄 builder 组合 provider registry、model、system prompt、delegate tools、session id、session store、log sink、stream options 和 initial/restored messages。
* **Run result**: `AgentRunResult` 暴露 final messages、assistant text、usage、stop reason、error/cancel 状态、tool start/end events、session id 和 `TauRuntimeLogContext`。
* **Session boundary**: `AgentApplication` 成功时保存 session；失败或取消时不保存，并把内存 conversation 恢复到回合前消息，保留 result/events 作为审计证据。
* **Examples**: 新增 Console example 与 ASP.NET Core HTTP example，均使用 Faux provider、delegate tool、`InMemoryAgentSessionStore` 和 runtime log sink 证明 Tau 可作为普通 Agent 应用底座消费。
* **Smoke**: 新增 `scripts/verify-agent-platform-examples.ps1`，build/smoke 两个 examples；同时把该 smoke 接入 `scripts/verify-dotnet.ps1 -RunSmoke`。
* **Tests**: 新增 `AgentPlatformTests` 并扩展 public API compile sample，覆盖 fake provider -> tool call -> final assistant text、session save/restore、runtime log trace、usage/stop reason 和 cancellation rollback。
* **Docs sync**: 更新 `GOAL.md`、Agent platform active plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，把 WP1-WP4 从“待实现”同步为“首版已落地”，并保留 Phase 4/5 验收缺口。

### 🧠 Design Intent (Why)

用户目标是先完成可复用 Agent 底座，而不是继续 UI parity。当前实现选择薄平台层而不是重写 runtime：`AgentApplication` 包装已有 `Agent` / `AgentRuntime` / `ProviderRegistry` / `IAgentTool` / `TauRuntimeLogContext`，提供外部应用最小可用入口；CLI/TUI 产品内部类型仍不进入 public SDK surface。

Faux provider smoke 只证明平台合同，不声明真实 provider/OAuth e2e 完成。失败/取消回合回滚内存消息和 session store，是为了让应用侧能把 `AgentRunResult` 当审计输出，而不把失败回合误持久化为正常 conversation。

### ✅ Verification

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPublicApiCompileSampleTests|AgentPlatform" --no-restore --verbosity minimal`：通过 5/5。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-platform-examples.ps1 -SkipRestore`：通过，覆盖 Console example 和 HTTP example build/smoke。
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：通过 119/119。
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过 280/280。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；项目级计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 119、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 438、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`：通过；覆盖 tau-ai、Agent platform examples、WebUi 和 Mom smoke。

### 🚧 Remaining Boundary

* Agent platform baseline 的本地 completion audit 已满足 `GOAL.md` 的完成条件：public API、provider/model/auth/config 基线说明、tool/session/observability 合同、两个示例、本地验证链、package/release 消费边界和 docs/history 同步均有当前证据。
* 真实 provider/OAuth e2e、真实 NuGet/package registry 发布演练、真实 package signing/provenance 实战和后续 product parity 接回仍是后续边界，不能被 fake provider/platform smoke 误读为关闭。

### 📁 Files Modified

* `src/Tau.Agent/Platform/AgentApplication.cs`
* `src/Tau.Agent/Platform/AgentApplicationBuilder.cs`
* `src/Tau.Agent/Platform/AgentRunResult.cs`
* `src/Tau.Agent/Platform/DelegateAgentTool.cs`
* `src/Tau.Agent/Platform/AgentToolContext.cs`
* `src/Tau.Agent/Platform/AgentSessionStore.cs`
* `tests/Tau.Agent.Tests/AgentPlatformTests.cs`
* `tests/Tau.Agent.Tests/AgentPublicApiCompileSampleTests.cs`
* `examples/README.md`
* `examples/Tau.Agent.ConsoleExample/Program.cs`
* `examples/Tau.Agent.ConsoleExample/Tau.Agent.ConsoleExample.csproj`
* `examples/Tau.Agent.HttpExample/Program.cs`
* `examples/Tau.Agent.HttpExample/Tau.Agent.HttpExample.csproj`
* `scripts/verify-agent-platform-examples.ps1`
* `scripts/verify-dotnet.ps1`
* `Tau.slnx`
* `GOAL.md`
* `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-06/20260607-1004-agent-platform-goal.md`

---

## [2026-06-07 21:32] | Task: Agent platform provider run observability closure

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续并完成 Agent platform baseline 的剩余本地验收。

### 🛠 Changes Overview

**Scope:** `Tau.Agent` runtime observability, Agent platform tests, goal/plan/quality/next docs

**Key Actions:**

* **Provider run trace**: 在 `AgentRuntime` 的每次 provider stream 边界新增 `provider/run.start` 与 `provider/run.end` runtime log。
* **Safe fields**: provider run fields 只写 provider/model/api、message/tool count、transport、provider session id、usage/cost、stopReason、failureKind、duration 和 `TauRuntimeLogContext` 的 correlation/session/message id；不写 prompt、headers、api key、metadata、完整 messages、tool arguments 或 tool result。
* **Failure paths**: provider cancellation、stream error、missing done message 和 provider stream exception 都会写出 `provider/run.end` 摘要，避免只覆盖成功路径。
* **Tests**: 扩展 `AgentPlatformTests`，断言 fake provider + tool call 会产生 provider run start/end 和 tool execution trace；同时断言取消路径写 `failureKind=cancelled`，且日志字段不含 prompt、tool 参数、tool result 或 secret-ish 文本。
* **Runner test compatibility**: `RuntimeCodingAgentRunnerTests` 改为按 `Category=agent` 筛选 agent run events，避免 provider `run.start|run.end` 与 agent `run.start|run.end` 共享 event name 后产生误匹配；同步断言 provider 同步异常会写 `provider/run.end failureKind=exception`。
* **Docs sync**: 更新 `GOAL.md`、completed plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，把 provider run observability 纳入 Agent platform baseline 的本地完成证据。

### 🧠 Design Intent (Why)

completion audit 发现 `GOAL.md` 的 Observability 完成条件明确要求 provider run、tool execution、usage/cost 和 redaction 都能在本地 sink 中验证；旧实现只有 tool execution trace，`AgentRuntime` 直接调用 provider stream 时没有 provider-level run log。因此本次选择在 runtime provider stream 边界补最小摘要日志，而不是改平台 builder 或示例层，保证所有 `Agent` / `AgentApplication` 消费路径都获得同一 observability baseline。

### ✅ Verification

* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPlatform" --no-restore --verbosity minimal`：通过 4/4。
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPublicApiCompileSampleTests|AgentPlatform" --no-restore --verbosity minimal`：通过 5/5。
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：通过 119/119。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "RuntimeCodingAgentRunnerTests" --no-restore --verbosity minimal`：通过 16/16。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-platform-examples.ps1 -SkipRestore`：通过，覆盖 Console / HTTP examples build/smoke。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过；项目级计数为 `Tau.Ai.Tests` 280、`Tau.Agent.Tests` 119、`Tau.Tui.Tests` 251、`Tau.CodingAgent.Tests` 438、`Tau.WebUi.Tests` 44、`Tau.Pods.Tests` 166。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`：通过；覆盖 tau-ai、Agent platform examples、WebUi 和 Mom smoke。
* `git diff --check -- ...`：通过；只有 CRLF/LF warning，无 whitespace error。

### 🚧 Remaining Boundary

* 本次关闭的是本地 fake provider / test sink 可验证的 provider run observability 缺口；真实 provider/OAuth e2e、真实 NuGet/package registry 发布演练、真实 package signing/provenance 实战和后续 product parity 接回仍是后续边界。

### 📁 Files Modified

* `src/Tau.Agent/Runtime/AgentRuntime.cs`
* `tests/Tau.Agent.Tests/AgentPlatformTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `GOAL.md`
* `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/histories/2026-06/20260607-1004-agent-platform-goal.md`
