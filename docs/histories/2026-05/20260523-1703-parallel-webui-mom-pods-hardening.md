## [2026-05-23 17:03] | Task: parallel WebUi/Mom/Pods hardening

### Execution Context

* **Agent ID**: `Codex main controller + WebUi worker 019e53f6-3cc1-7303-92fa-a217ba553963 + Mom worker 019e53f6-7d17-7242-870a-c76269f4fa7e + Pods worker 019e53f6-bb1e-76d0-b921-c04642965673`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 开始下一轮并行执行；继续继续。

### Changes Overview

**Scope:** `Tau.WebUi` / `Tau.Mom` / `Tau.Pods` / tests / docs

**Key Actions:**

* **[Parallel strategy]**: 继续使用 3 个 worker，分别处理 WebUi、Mom、Pods 三个互斥写入范围；主控负责集成、文档同步、验证和提交。
* **[WebUi JSONL import]**: 新增 `WebChatJsonlImporter` 和 `POST /api/sessions/import.jsonl`，让 WebUi-local 线性 JSONL transcript 可以 export -> import roundtrip。导入会校验 session header、message entry、version、必填字段、重复 id 和线性 `parentId`，endpoint 导入后复用 `WebChatService.ImportSession` 生成新的 WebChat session id。
* **[Mom sandbox validation]**: 新增 `MomCommandLine` 和 `MomSandboxValidator`，接入 `--validate-sandbox` CLI 入口；该入口不启动普通 worker，host sandbox 快速成功，`docker:<container>` 复用既有 Docker validate seam，失败时返回非零 exit code。
* **[Pods exec hardening]**: `PodExecService` 将本地 `ssh` 启动失败、runner 异常、`Process.Start` 返回 null 和 cancellation 转成结构化 `PodExecResult` failure，统一使用 `ExitCode=-1`，并在默认进程取消时 best-effort kill；`PodLifecycleService` restart failure 继续透传底层 exec summary。
* **[Docs/history/plan]**: 同步 `README.md`、`next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、release notes 和 active execution plan，明确本轮 WebUi 仍是 WebUi DTO 线性 transcript roundtrip，Mom 仍未完成真实 Docker container smoke，Pods 仍不是完整远端 transport hardening。

### Design Intent (Why)

这轮延续上一轮并行策略，目标是把已有 baseline 补成更可用、更可诊断的合同，而不是扩大热点修改面。

* WebUi 先固定 WebUi-local JSONL 的导入导出闭环，避免在 WebChat DTO 和 CodingAgent JSONL tree store 之间过早耦合。
* Mom 把 sandbox validation 做成显式运维入口，避免普通 `--once` 或长期 worker 因缺 Docker 在启动期被误伤。
* Pods 先把本地进程层故障收敛成稳定结果对象，让 CLI/lifecycle 可以继续展示可诊断 summary；全局 timeout 和真实远端 smoke 留给后续 transport hardening。

### Files Modified

* `src/Tau.WebUi/Services/WebChatJsonlImporter.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/WebChatJsonlExporterTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/MomCommandLine.cs`
* `src/Tau.Mom/MomSandboxValidator.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `src/Tau.Pods/Services/PodExecService.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `tests/Tau.Pods.Tests/PodExecServiceTests.cs`
* `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`
* `README.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `docs/histories/2026-05/20260523-1703-parallel-webui-mom-pods-hardening.md`

### Verification

* `git diff --check` passed; only existing CRLF normalization warnings were reported.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed; final output: `Tau .NET project-level validation passed`.
* Full verify test counts: `Tau.Ai.Tests` 194/194, `Tau.Agent.Tests` 67/67, `Tau.Tui.Tests` 86/86, `Tau.CodingAgent.Tests` 333/333, `Tau.Pods.Tests` 44/44.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed: 12/12 tests.
* `dotnet run --project src\Tau.Mom\Tau.Mom.csproj --no-build -- --validate-sandbox --Mom:Sandbox host` passed with exit code 0 and logged `Mom sandbox 'host' is valid. No Docker checks were required.`
