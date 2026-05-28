## [2026-05-26 23:05] | Task: Pods setup plan-only baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### User Query

> 继续下一轮快速移植，优先推进 pi-mono parity，减少低收益文档和单元测试开销。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Setup plan-only CLI**: 新增 `setup plan [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id>`，输出远端 setup command plan，不执行 SSH/SCP/远端脚本。
* **vLLM version metadata**: `PodDefinition` 增加 `VllmVersion`，validator 限定为 `release/nightly/gpt-oss`，sample SSH pod 默认 `release`。
* **Secret boundary**: `PodSetupPlanner` 只暴露 `HF_TOKEN` / `PI_API_KEY` 是否配置，setup command 使用 shell placeholder，不回显真实 token。
* **Targeted coverage**: 增加 planner 与 CLI focused tests，覆盖 token 不泄漏、version override/default、invalid version 和非 SSH pod 拒绝。

### Design Intent (Why)

上游 `pods setup` 会复制并执行远端脚本，依赖 SSH、`HF_TOKEN`、`PI_API_KEY` 和远端环境副作用。本轮先把 Tau 的 config/CLI/JSON 合同、vLLM version 元数据和 secret 不回显边界固定为可评审的 plan-only baseline；真实 setup run、SCP、远端脚本 smoke 和长期 vLLM 健康验证后续单独推进。

### Files Modified

* `src/Tau.Pods/Models/PodDefinition.cs`
* `src/Tau.Pods/Models/PodSetupResults.cs`
* `src/Tau.Pods/Services/PodSetupPlanner.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Services/PodsConfigValidator.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodSetupPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 21:23] | Follow-up: Model pull revision/snapshot option

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Model pull revision**: `model pull` now accepts `--revision <rev>` and `--snapshot <rev>`.
* **Download command parity**: `PodModelService.PullAsync` passes the selected revision to both `huggingface-cli download` and the python `huggingface_hub` fallback.
* **CLI contract**: `model pull --json --config ... --pod ... --revision rev ...` and the `--snapshot` alias preserve the existing JSON operation output while using the revision-aware remote command.
* **Parser regression guard**: the old vLLM missing-model usage test now uses explicit `--pod`, because one positional argument is now a valid active-pod model-id form.
* **Focused validation**: `Tau.Pods` build passed, and pull/revision focused tests plus the vLLM missing-model usage guard passed.

### Design Intent (Why)

The previous slice let vLLM preflight/deploy choose a specific local cache revision, but `model pull` still had no way to populate that revision intentionally. This follow-up closes the pull side of the cache workflow without making deploy implicitly download large model artifacts; explicit deploy prefetch remains a separate slice.

### Files Modified

* `src/Tau.Pods/Services/PodModelService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodModelServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 00:58] | Follow-up: Deployments and vLLM control active fallback

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Deployments active fallback**: `deployments [--json] [path] [pod-id]` now defaults to config `active` when no pod id is supplied.
* **vLLM control active fallback**: `vllm status/health/stop [--json] [path] [pod-id] <deployment-name>` now also defaults to config `active` when pod id is omitted.
* **Compatibility**: Explicit pod ids still override active, and `vllm plan/preflight/deploy` remain unchanged because serve-style commands would otherwise collide with model ids such as `org/model`.
* **Validation**: Added focused tests for deployments active fallback plus combined active-pod coverage for vLLM status/health/stop.

### Design Intent (Why)

Upstream pods commands resolve the active pod by default for query/control operations. Tau already had `active` persisted and consumed by model commands, so this follow-up extends the same behavior to deployments and vLLM control commands without touching serve-style parsing. That keeps the shell contract predictable and avoids introducing a false default on the ambiguous `vllm plan/preflight/deploy` forms.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 19:04] | Follow-up: Pods explicit config/pod options for control commands

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Explicit option entry**: `model list/pull/remove/status`, `deployments`, and `vllm status/health/stop` now accept `--config path` / `--pod id` in addition to the existing positional form.
* **Parser hardening**: The option-aware parser now only falls back to positional path detection when `--config` is not already explicit, so model ids like `org/model` do not get mistaken for config paths.
* **Compatibility**: Legacy positional forms still work unchanged, including active pod fallback for model and vLLM control commands.
* **Targeted coverage**: Added focused CLI tests for option-based `model list`, `model status`, `deployments`, and `vllm status` paths, alongside existing positional/active fallback coverage.

### Design Intent (Why)

The previous slice gave `vllm plan/preflight/deploy` a safe active-default escape hatch, but the rest of the Pods control surface was still relying on positional config/path heuristics. This follow-up makes the command family consistent: explicit config/pod overrides are available everywhere that already has an active-default path, while the old positional contract remains intact for compatibility.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 17:55] | Follow-up: vLLM serve active fallback disambiguation

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Serve CLI disambiguation**: `vllm plan/preflight/deploy` now support `--config path` / `--pod id` / `--name name` as explicit escapes, while still preserving the legacy `[path] <pod-id> <model-id> [deployment-name]` positional form.
* **Active pod fallback**: When `--pod` is omitted, `vllm plan/preflight/deploy` resolve the pod from `PodsConfig.active` and report `No active pod. Use 'active <pod-id>' to set one.` if no active pod exists.
* **Model id safety**: The active-default path now uses `--name` for deployment names so Hugging Face ids such as `org/model` no longer collide with path heuristics or pod-id slots.
* **Targeted coverage**: Added focused CLI tests for explicit `--config/--pod/--name`, active-default plan/preflight/deploy, and missing-active error handling.

### Design Intent (Why)

`vllm plan/preflight/deploy` had a real contract collision: the upstream-friendly active-default behavior needs a way to omit pod id, but Tau still had legacy positional serve syntax and a path heuristic that would misread `org/model`. This follow-up keeps the old positional form alive for compatibility while giving the active path an unambiguous option-based escape hatch, so the CLI can match upstream defaults without inventing a false parser rule.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-26 23:45] | Follow-up: Setup result config writeback

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Config writeback**: `setup run` 成功后会把当前 pod 的 `modelsPath`、`vllmVersion` 和 detected `gpus` 写回 `tau.pods.json`；失败、缺 token 或 SSH/SCP/setup 失败不会写 config。
* **GPU metadata**: `PodDefinition` 增加 `Gpus` 字段，复用 setup run 返回的 `PodGpuInfo`，让后续 model/vLLM/deploy 切片能消费 setup detect 结果。
* **Secret boundary**: focused CLI test 重新读取保存后的 config，确认 `HF_TOKEN` / `PI_API_KEY` 真实值不会进入 stdout 或配置文件。

### Design Intent (Why)

上游 `setupPod()` 的闭环不只是执行远端脚本，还会把 GPU、models path 和 vLLM version 保存到 pod config。Tau 已经完成 setup plan/run 执行链，本次把成功结果持久化到已有 pod 配置，避免后续 `model` / `vllm` / `deploy` 继续依赖旧 config。新增 pod registration / active pod 语义暂不在本刀处理。

### Files Modified

* `src/Tau.Pods/Models/PodDefinition.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-26 23:55] | Follow-up: Setup run writeback output contract

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **CLI output contract**: `setup run` text and JSON output now include `configUpdated` and `configPath`, so callers can distinguish a successful persisted setup from a failed no-writeback run without diffing `tau.pods.json`.
* **Failure no-writeback coverage**: focused CLI coverage now verifies SSH failure stops after the first step, returns `configUpdated=false`, and leaves `modelsPath` / `vllmVersion` / `gpus` unchanged.
* **Boundary**: config persistence remains a CLI/store concern; `PodSetupService` still only reports remote setup execution results.

### Design Intent (Why)

The previous slice persisted setup results after success, but automation consuming `setup run --json` could not tell whether the config file was updated. This follow-up makes the persistence side effect explicit while preserving the service boundary and keeping failure paths no-writeback by contract.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-26 23:31] | Follow-up: Pods setup run executable baseline

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Setup run CLI**: 新增 `setup run [--json] [--script <path>] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id>`，执行 SSH test、SCP setup script、远端 setup command 和 GPU detect。
* **Secret boundary**: 真实 `HF_TOKEN` / `PI_API_KEY` 通过 stdin 传入远端 shell；display command、JSON/text 输出继续只显示 `$HF_TOKEN` / `$PI_API_KEY` placeholder，stdout/stderr 会把本进程 token 值替换为 `[REDACTED]`。
* **Execution seam**: setup run 使用可注入 process runner，避免复用会返回真实 command 的 `PodExecService`，并用 fake runner focused tests 固定 SSH/SCP/setup/GPU 步骤。
* **CLI discoverability**: `help` 和未知 setup subcommand usage 已同步 `setup <plan|run>`。

### Design Intent (Why)

上游 setup 需要真实 SSH/SCP、HF/PI token、远端脚本和 GPU 环境。本次不宣称真实 pod setup smoke 已完成，而是先把可执行链路、token 传输方式、输出脱敏和失败早停行为固定为可评审 baseline；后续真实环境 smoke 可以直接复用同一 CLI 合同。

### Files Modified

* `src/Tau.Pods/Models/PodSetupResults.cs`
* `src/Tau.Pods/Services/PodSetupService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodSetupServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 00:20] | Follow-up: Pod registration and active config baseline

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Active config parity**: `PodsConfig` now persists upstream-compatible JSON field `active`; config validation rejects active pod ids that do not exist.
* **Active/remove CLI**: Added `active [--json] [path] <pod-id>` and `remove [--json] [path] <pod-id>` plus `list/status --json` `activePodId` and per-pod `active` output.
* **Setup registration**: Added local `setup [--json] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss] [path] <pod-id> "ssh [-p port] <host>"` registration. It creates or updates local config only; it does not run SSH, SCP, token transport, GPU detection, or remote setup.
* **SSH boundary**: Registration accepts simple `ssh host`, `ssh -p 2200 host`, `ssh -p2200 host`, or bare `host`; complex SSH options are explicitly rejected for now.

### Design Intent (Why)

Upstream `packages/pods` treats pod setup as both registration and remote setup. Tau already has a separate `setup run` execution chain, so this follow-up closes the config/CLI parity gap first: named pod registration, active pod tracking, and safe local persistence. Complex SSH command persistence is left out because the current remote execution services consume structured `SshHost` / `SshPort`; saving arbitrary SSH options would expand quoting and argv semantics across every later pod operation.

### Files Modified

* `src/Tau.Pods/Models/PodsConfig.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Services/PodsConfigValidator.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodsConfigValidatorTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 00:42] | Follow-up: Model commands active pod fallback

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Model active fallback**: `model list [--json] [path]` now uses config `active` when no pod id is supplied.
* **Model operation fallback**: `model pull/remove/status [--json] [path] <model-id>` treats the single trailing argument as the model id and resolves the pod from `active`.
* **Compatibility**: Existing explicit forms such as `model status [--json] [path] <pod-id> <model-id>` still work.
* **Error contract**: Missing active pod now reports `No active pod. Use 'active <pod-id>' to set one.` instead of forcing a pod id usage error.

### Design Intent (Why)

Upstream `packages/pods` model commands use the active pod by default and only require an override when the user wants a different pod. Tau already had `active` persisted in config, but the model command surface still forced explicit pod ids. This follow-up connects the consumer side without touching vLLM serve commands, because `vllm plan/preflight/deploy` would introduce real ambiguity between `<pod-id> <model-id>` and active-pod `<model-id> [deployment-name]`, especially for Hugging Face ids containing `/`.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

---

## [2026-05-27 20:48] | Follow-up: vLLM revision-aware snapshot selection

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Revision option**: `vllm plan/preflight/deploy` now accept `--revision <rev>` and `--snapshot <rev>` alongside existing `--config` / `--pod` / `--name` disambiguation.
* **Remote preflight resolution**: revision-aware preflight first checks `refs/<revision>`, then `snapshots/<revision>`, and returns `model-snapshot-revision-missing` when the requested revision is not present.
* **Plan/deploy contract**: plan output and metadata carry `revision`; preflight JSON/text carry `requestedRevision`; deploy still injects the concrete resolved snapshot path into `vllm serve`.
* **Focused validation**: added targeted planner/orchestration/CLI coverage for revision-aware plan, preflight success/failure, and deploy using a resolved revision snapshot.

### Design Intent (Why)

Upstream pods does not expose a first-class revision/snapshot option; it passes the repo id to `hf download` and `vllm serve`. Tau already has stricter HF cache snapshot preflight and refuses ambiguous multi-snapshot caches. This follow-up closes the missing escape hatch by letting the caller select the intended cache revision explicitly, while leaving automatic prefetch and `model pull --revision` for a later slice.

### Files Modified

* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
