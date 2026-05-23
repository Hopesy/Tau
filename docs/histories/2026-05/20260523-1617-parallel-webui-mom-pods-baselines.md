## [2026-05-23 16:17] | Task: parallel WebUi/Mom/Pods parity baselines

### Execution Context

* **Agent ID**: `Codex main controller + WebUi worker 019e53db-f2a7-7cb1-a9d7-8675e6ce60c2 + Mom worker 019e53dc-26ff-7e02-b4c3-a7bd908046b7 + Pods worker 019e53dc-66f0-76c2-a2af-960f31c41306`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 先提交当前仓库，然后开启多个 agent 并行；判断开启多少个合适。

### Changes Overview

**Scope:** `Tau.WebUi` / `Tau.Mom` / `Tau.Pods` / tests / docs

**Key Actions:**

* **[Pre-flight commit]**: 先把当时已有的 CodingAgent/TUI selector parity 进展提交为 `c6ed266 feat: advance coding agent selector parity`，确保并行 worker 从干净基线开始。
* **[Parallel strategy]**: 开启 3 个并行 worker，分别处理 WebUi、Mom、Pods；这些模块写入范围基本隔离。暂不让 worker 继续碰刚提交的大热点路径 `Tau.CodingAgent` / `Tau.Tui`，避免合并冲突和语义漂移。
* **[WebUi JSONL export]**: 新增 WebUi-local 线性 JSONL transcript exporter 和 `GET /api/sessions/{id}/export.jsonl` endpoint；首行为 `type=session` header，后续为 `type=message` entries，并用稳定顺序 message id 和线性 `parentId` 串联。
* **[Mom sandbox seam]**: 为 Mom Docker sandbox validate/exec 增加可注入 `IMomSandboxProcessRunner` seam，固定 `docker --version`、`docker inspect` 和 `docker exec -w /workspace <container> sh -c <command>` 的本地可测试命令构造。
* **[Pods SSH argv hardening]**: 将 `PodExecService` 的系统 `ssh` 调用从字符串 `Arguments` 改为 `ProcessStartInfo.ArgumentList`，并补测试固定 port、options、host 和复杂 remote command 作为独立 argv 的行为。
* **[Docs/history/plan]**: 同步 `README.md`、`next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、release notes 和 active execution plan，明确这些切片是 baseline，不夸大为完整 branch/session-tree parity、真实 Docker smoke 或完整远端 transport hardening。

### Design Intent (Why)

这轮并行的目标不是扩大修改面，而是在已提交的稳定基线上选择彼此隔离、可独立验证的模块推进。3 个 worker 是当前最合适的上限：WebUi、Mom、Pods 分别对应独立应用面和测试项目；继续增加 worker 会更可能踩到刚完成的大批 `Tau.CodingAgent` / `Tau.Tui` 热点改动，合并成本会高于收益。

边界保持明确：

* WebUi JSONL 是 WebUi DTO 的线性 transcript baseline，不等于 CodingAgent JSONL branch/session-tree 语义完整对齐。
* Mom Docker sandbox 本轮固定 validate/exec command construction seam，没有宣称真实 Docker container smoke 已完成。
* Pods `ArgumentList` 解决本地 `ssh` argv 构造和 quoting 漂移，不等于完整远端 transport hardening。

### Files Modified

* `src/Tau.WebUi/Services/WebChatJsonlExporter.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/WebUiApplication.cs`
* `tests/Tau.WebUi.Tests/WebChatJsonlExporterTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `src/Tau.Mom/MomSandbox.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `src/Tau.Pods/Services/PodExecService.cs`
* `tests/Tau.Pods.Tests/PodExecServiceTests.cs`
* `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`
* `README.md`
* `next.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `docs/histories/2026-05/20260523-1617-parallel-webui-mom-pods-baselines.md`

### Verification

* Worker WebUi: `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal`
* Worker WebUi: `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal`
* Worker Mom: `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore --verbosity minimal`
* Worker Mom: `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal --filter MomSandboxAndToolsTests`
* Worker Pods: `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal`
* Worker Pods: `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal`
* Main controller: `git diff --check` passed; only existing CRLF normalization warnings were reported.
* Main controller: `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* Main controller: `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed: 6/6 tests.
* Main controller: `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* Main controller: `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal --filter MomSandboxAndToolsTests` passed: 10/10 tests.
* Main controller: `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* Main controller: `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed: 33/33 tests.
* Main controller: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed; final output: `Tau .NET project-level validation passed`.
