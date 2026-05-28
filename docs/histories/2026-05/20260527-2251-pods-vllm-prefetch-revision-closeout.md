## [2026-05-27 22:51] | Follow-up: vLLM prefetch/revision closeout

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### User Query

> 下一轮，继续快速移植，优先推进 Tau.Pods 的 vLLM prefetch / revision 闭环。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **Prefetch contract**: `vllm deploy --prefetch` 保持 preflight-first，只有模型 cache / snapshot / revision 类失败才复用 `model pull`，pull 成功后再跑第二次 preflight。
* **Nested result detail**: `PodVllmOperationResult` 的 `prefetch` 嵌套结果新增触发失败种类字段，JSON/text 都能解释为什么发生了拉取。
* **CLI contract**: `vllm deploy --prefetch --revision/--snapshot`、`vllm preflight --revision/--snapshot` 和 `model pull --revision/--snapshot` 的 targeted 覆盖补齐，文本输出增加 `[prefetch]` / `[prefetch-output]` 合同。
* **Docs sync**: 更新 README、ARCHITECTURE、QUALITY_SCORE、next.md 和 active execution plan，避免把已实现的 prefetch 闭环继续记成缺口。

### Design Intent (Why)

Tau 的 `--prefetch` 不应该变成“无条件先下载”，而应是一个修复型闭环：先用 preflight 排除 vLLM/SSH 等不可修复问题，只在 HF cache / snapshot 缺口时补拉，再回到严格 preflight。这样既保留了 Tau 的确定性 snapshot 解析，也避免在远端不可用时制造不必要的下载副作用。

### Files Modified

* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
