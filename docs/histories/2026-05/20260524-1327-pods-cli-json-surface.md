## [2026-05-24 13:27] | Task: Pods CLI JSON surface 继续收口

### Execution Context

* **Agent ID**: `Codex 主控 + 并行 worker`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 顶层 `status/probe/health` 增加 `--json` 输出，`status` 输出配置级 pod 摘要，`probe/health` 输出结果数组。
* 顶层 `exec/deploy/stop/restart` 增加 `--json` 输出；`exec` 暴露 transport/target/command/exitCode/stdout/stderr/durationMs，`deploy/stop/restart` 暴露 operation、deploymentName 以及可选底层 exec 信息。
* `model list/pull/remove/status` 也已补齐 `--json`，这样 `Tau.Pods` 顶层和 model/vllm 命令面现在大部分都具备机器可读 contract。
* 为 CLI 增加更精确的 JSON flag consume helper，避免 `exec` 把远端命令本身的 `--json` 参数误吞。
* lifecycle result 增加 deployment/model/exec 附带字段，供 JSON 输出直接复用。

### Design Intent (Why)

本轮把 `Tau.Pods` 的命令面继续从“人工可看”推进到“脚本和上层宿主也能消费”。优先补统一 JSON contract，而不去扩真实 SSH/HF/vLLM smoke，这样后续无论是 Web、agent orchestration 还是 shell automation，都有稳定的机器接口可接。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodLifecycleResults.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
