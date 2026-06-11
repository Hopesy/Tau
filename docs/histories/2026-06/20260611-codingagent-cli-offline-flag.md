## [2026-06-11] | Task: CodingAgent CLI `--offline` flag parity

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，领取下一条低歧义 CodingAgent CLI contract 切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` CLI argument parsing + startup wiring

**Key Actions:**

* **`--offline` parsing**: `CodingAgentCliArguments` 现在解析 `--offline` 为 `Offline` 标志，不再把它当作被丢弃的 boolean option。
* **Startup wiring**: `Program.cs` 在 parse 后、其它处理前，如果 `cli.Offline` 为真就设置 `PI_OFFLINE=1` 环境变量，对齐上游 `main.ts` 的 `process.env.PI_OFFLINE = "1"`，使所有下游 `PI_OFFLINE` 消费者（`CodingAgentPackageManager` 的 update skip、`CodingAgentStartupNoticeService` 的 telemetry 禁用）都能感知。
* **Tests**: `CodingAgentInitialMessageBuilderTests` 新增 `--offline` 解析回归。

### 🧠 Design Intent (Why)

上游 `--offline` 只是 `PI_OFFLINE=1` 的 CLI 等价入口：它在启动早期设置 env var，让所有读取 `PI_OFFLINE` 的子系统统一进入离线模式。Tau 之前已经有两个 `PI_OFFLINE` 消费者，但 `--offline` flag 被静默丢弃，导致用户必须显式设置环境变量。本切片把 flag 解析出来并在启动早期设置 env var，复用现有消费链，不新增离线状态分叉。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter CodingAgentInitialMessageBuilderTests --no-restore --verbosity minimal`：30/30 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 562、WebUi 61、Pods 216）

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
