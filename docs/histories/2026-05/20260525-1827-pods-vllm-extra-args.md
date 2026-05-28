## [2026-05-25 18:27] | Task: Pods vLLM extra args

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 继续快速移植下一轮，优先真实 pi-mono parity，少做大文档和低收益单元测试。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* Added `--vllm <args...>` sentinel support to `vllm plan` and `vllm deploy`.
* The sentinel consumes the remaining argv as vLLM serve arguments, so Tau-level flags do not accidentally swallow vLLM flags after `--vllm`.
* Passed parsed extra args into existing `PodVllmServeOptions.ExtraArgs`; planner and deploy orchestration reuse the existing shell quoting and serve command path.
* Updated targeted CLI/planner assertions for current snapshot-discovery serve command behavior and extra arg passthrough.

### Design Intent

Upstream Pods exposes a custom vLLM args escape hatch where everything after `--vllm` belongs to vLLM. Tau already had the lower-level `ExtraArgs` model and quoting; this slice exposes that behavior at the CLI surface without changing orchestration or claiming real GPU/vLLM smoke parity.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --filter "FullyQualifiedName~PodsCliTests|FullyQualifiedName~PodVllmCommandPlannerTests" --verbosity minimal` passed: 36/36.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* Plan-only CLI smoke in a temp directory passed: `vllm plan --json <temp-config> gpu-pod-2 org/model served-model --vllm --tensor-parallel-size 2 --max-model-len 32768` produced a `serveCommand` containing the quoted custom vLLM args.
