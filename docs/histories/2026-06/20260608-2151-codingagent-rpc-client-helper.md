## [2026-06-08 21:51] | Task: CodingAgent RPC client helper baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **RPC client helper**: 对照上游 `packages/coding-agent/src/modes/rpc/rpc-client.ts`，补齐 `CodingAgentRpcClient` / `CodingAgentRpcProcessTransport` 的本地 baseline，覆盖 RPC 进程启动、`req_N` 请求 id、LF JSONL send + flush、pending response correlation、event listener、stderr-backed timeout、typed helper、bash result deserialize 和 stop-time pending rejection。
* **Timeout lifecycle fix**: 修正 `WaitForIdleAsync` / `CollectEventsAsync` 中 timeout token source 生命周期，避免 helper 返回未完成 task 后 timeout source 被提前释放；timeout 诊断统一通过加锁的 `Stderr` property 读取 stderr。
* **Tests and docs**: 新增 `CodingAgentRpcClientTests`，同步 active plan、matrix、`next.md`、`GOAL.md` 和 `docs/QUALITY_SCORE.md` 中的 RPC client helper 状态。

### 🧠 Design Intent (Why)

上游 RPC 面不只有 host 协议类型，还包含 `rpc-client.ts` 这一层面向程序化集成的 helper。Tau 之前已经有 headless host、streamed bash 和 extension UI bridge，但 matrix 仍明确标出 RPC client helper open。本次用可注入 transport 固定客户端行为，避免测试依赖真实子进程，同时保留 exact TypeScript schema、TypeScript extension runtime、package-loaded extensions 和真实 extension UI 调用作为后续缺口。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcClient.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcClientTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`

### ✅ Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`: passed, 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentRpcClientTests" --no-restore --verbosity minimal`: passed, 7/7.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`: passed, 456/456.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`: passed; `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 456, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 215.
* `git diff --check`: passed; only existing CRLF normalization warnings were reported for `docs/QUALITY_SCORE.md`, `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`, and `next.md`.
