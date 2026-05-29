## [2026-05-29 02:32] | Task: Release artifact baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 按 `GOAL.md` 继续推进 Tau 对 `pi-mono-main` 的 100% 可审计移植。

### Changes Overview

**Scope:** `scripts` / release parity docs

**Key Actions:**

* 新增 `scripts/build-release-artifacts.ps1`，对照上游 `scripts/build-binaries.sh` 建立 current-RID Tau release artifact baseline。
* 新增 `scripts/smoke-release-artifacts.ps1`，在 artifact 目录直接验证 wrapper 和 publish 输出，而不是通过 `dotnet run` 验证源码目录。
* release artifact 当前输出到 `artifacts/tau-<rid>/`；Windows 当前平台会生成 `artifacts/tau-win-x64/`，包含 `apps/**` publish 输出、`bin/pi.cmd`、`bin/tau-ai.cmd`、`bin/pi-ai.cmd`、`bin/mom.cmd`、`bin/pi-pods.cmd`、`bin/tau-web-ui.cmd`、`manifest.json`、`README.md`、`LICENSE` 和 release notes。
* 同步 active parity matrix、100% parity plan、`next.md`、`docs/QUALITY_SCORE.md` 和 `README.md`，把 release 产物缺口从 missing 推进到 current-RID partial，并明确后续仍缺 cross-platform archives、no-env wrapper、version/changelog/tag/publish automation、CI 接入和真实外部 e2e release smoke。

### Design Intent (Why)

上游 `pi-mono-main` 的 release 链会用 `scripts/build-binaries.sh` 生成平台二进制并暴露 `pi`、`pi-ai`、`mom`、`pi-pods` 等可执行入口。Tau 之前只有源码级 build/test/smoke，不能证明干净输出目录里的真实交付物可运行。本轮先做 PowerShell-first current-RID artifact baseline：只解决“能产出并 smoke 真实 Tau executable artifact”这一件事，不混入 version/tag/publish 或跨平台 archive 自动化，避免把 Phase 5 发布流水线一次性堆复杂。

### Files Modified

* `scripts/build-release-artifacts.ps1`
* `scripts/smoke-release-artifacts.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Verification

* `dotnet restore Tau.slnx -r win-x64 --verbosity minimal` followed by `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Configuration Release -SkipRestore` passed. The RID restore is required when reusing restore assets for `net10.0/win-x64` publish. It published `Tau.CodingAgent`, `Tau.Ai.Cli`, `Tau.Mom`, `Tau.Pods` and `Tau.WebUi` to `artifacts\tau-win-x64\apps\**`, generated wrappers and `manifest.json`, then ran artifact smoke.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64` passed. Smoke covered `tau-ai list`, `pi-ai list`, `pi --mode rpc get_state`, `pi-pods --help`, WebUi `/healthz` / `/api/status` / `/api/catalog` / session store write, and Mom `--once` local event/inbox/outbox/status/log/runtime-log flow.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 190, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44 and `Tau.Pods.Tests` 166.
