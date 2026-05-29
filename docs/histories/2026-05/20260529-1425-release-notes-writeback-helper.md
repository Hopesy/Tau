## [2026-05-29 14:25] | Task: release notes writeback helper

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell; cwd=C:\Users\zhouh\Desktop\Tau`

### User Query

> 按 `GOAL.md` 继续推进 pi-mono -> Tau 100% parity；当前切片继续 Phase 5 release automation parity。

### Changes Overview

**Scope:** release scripts, README, active parity plans, quality/next docs

**Key Actions:**

* **Release notes helper**: 新增 `scripts/update-release-notes.ps1`，默认 dry-run，显式 `-Apply` 才把 `v<version>` release notes 表格行写入 `docs/releases/feature-release-notes.md`。
* **Release plan integration**: `scripts/plan-release.ps1` 现在把 release notes preview 纳入 planned commands 和 non-executed mutations，并把 `update-release-notes.ps1` 列为 release script preflight。
* **Docs sync**: 同步 README、QUALITY、active 100% plan、parity matrix 和 `next.md`，明确本切片只关闭 release notes helper baseline，不声明 commit/tag/publish/push execution automation 完成。

### Design Intent (Why)

上游 `scripts/release.mjs` 会在真实 release flow 里更新 changelog、commit、tag、publish 和 push。Tau 当前还没有完整 release execution automation，因此先把 release notes mutation 拆成一个可审计、默认不写文件、只在 `-Apply` 下生效的窄 helper，避免 dry-run planner 直接变成有副作用的 release 脚本。

### Files Modified

* `scripts/update-release-notes.ps1`
* `scripts/plan-release.ps1`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `next.md`
