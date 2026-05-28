## [2026-05-26 13:44] | Task: Pods vLLM snapshot preflight hardening

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户要求继续快速推进 pi-mono -> Tau 移植，默认进入下一轮，不询问下一步，少做低收益文档和单测，把核心行为写进 baseline。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **[Remote snapshot preflight]**: 收口当前 `vllm preflight` / deploy preflight 行为，固定远端 HF cache `snapshots/` 与 `refs/main` 解析边界。
* **[Failure diagnostics]**: preflight summary 对 `vllm-missing`、`model-cache-missing`、`model-snapshots-missing`、`model-snapshot-missing`、`model-snapshot-ref-missing`、`model-snapshot-ambiguous`、SSH failure 和 unknown output 增加可操作诊断。
* **[Shell quoting]**: `$HOME/...` 形式的 model cache path 改为双引号展开，避免路径含空格时 preflight shell 赋值断裂；非 `$HOME` 路径继续使用单引号。
* **[Focused coverage]**: `PodVllmOrchestrationServiceTests` 增加 failure-kind table、SSH runner failure 映射和 `$HOME` path quoting 回归。
* **[Minimal docs]**: `next.md` 只更新 Tau.Pods 段，把“远端模型 snapshot 解析”从剩余缺口改为已有 preflight baseline，剩余缺口收窄到真实远端 smoke、多 revision 选择策略、rollout 状态机和 transport hardening。

### Design Intent (Why)

上游 `pi-mono` Pods 当前没有独立 `vllm preflight` 或 HF cache `snapshots/refs/main` 解析，主要在 `pi start` 中直接下载并启动 vLLM。Tau 已经走了更结构化的 plan/preflight/deploy 分层，因此本轮不倒退为上游单体流程，而是把已有 snapshot preflight 收口成稳定、可评审、可脚本化的合同：preflight 只检查和分类，不下载、不启动；deploy 只有在 preflight 成功解析到 concrete snapshot path 后才继续。

### Files Modified

* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `next.md`

### Validation

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 99/99 passed
