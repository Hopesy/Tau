## [2026-06-07 22:58] | Task: Pods vLLM option planning

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows PowerShell / .NET 10`

### User Query

> 继续执行 `GOAL.md` 的 100% pi-mono parity 目标，从当前 active plan 和 matrix 领取下一块可验证移植切片。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* `PodVllmServeOptions` 新增 `RequestedGpuCount`、`Memory`、`Context`，`PodVllmServePlan` 新增 `requestedGpuCount`、`selectedGpus`、`memoryUtilization`、`contextTokens` 等可审计字段。
* `PodVllmCommandPlanner` 在非显式 `--vllm` 路径支持 known-model requested GPU count、基于 `PodDefinition.Gpus` 的本地 GPU id 选择、单 GPU `CUDA_VISIBLE_DEVICES`、`--memory` 到 `--gpu-memory-utilization`、`--context` 到 `--max-model-len` 的规划转换。
* `PodsCli` 的 `vllm plan/deploy` 增加 `--gpus`、`--memory`、`--context` 解析，并在 text/JSON plan 与 metadata 中输出规划结果；显式 `--vllm` 继续覆盖自动 known-model/GPU/memory/context 规划。
* 增加 planner 与 CLI 回归，固定 known-model option planning、unknown model `--gpus` error、explicit `--vllm` override 和 JSON contract。
* 同步 `next.md`、pi-mono parity matrix、100% parity active plan 和 `docs/QUALITY_SCORE.md`，保留真实 SSH/HF/GPU/vLLM e2e、usage-aware round-robin GPU allocation state 和直接 start/log-tail flow 为未关闭缺口。

### Design Intent (Why)

上游 `packages/pods/src/commands/models.ts` 的 start flow 已经把 known-model config、requested GPU count、memory/context convenience flags 和 custom `--vllm` override 合在一起。Tau 上一轮只完成 known-model args/env 自动注入，本轮补齐本地可规划的 option contract，使 `vllm plan/deploy` 在不触碰真实 SSH/GPU 环境的情况下先具备可审计行为。

Tau 当前 `PodDefinition` 没有上游 `pod.models[*].gpu` 的运行中 usage state，因此本轮只做基于本地 GPU inventory 顺序的 selected GPU ids baseline；usage-aware round-robin 仍留作后续 schema/runtime 切片，避免为了一个 planner 子缺口提前扩大 config schema。

### Files Modified

* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/QUALITY_SCORE.md`
* `next.md`

### Validation

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodsCli|Model|Config|PodVllmCommandPlanner" --no-restore --verbosity minimal` passed 108/108.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed 181/181.
* `git diff --check` passed; only CRLF/LF normalization warnings were reported.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 181.
