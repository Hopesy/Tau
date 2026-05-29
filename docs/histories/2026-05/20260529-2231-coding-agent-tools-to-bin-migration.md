# 2026-05-29 22:31 CodingAgent tools-to-bin migration helper

## 用户诉求

按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植，不把局部 helper 当成最终完成。

## 主要变更

- 新增 `scripts/migrate-coding-agent-tools-to-bin.ps1`，对照上游 `packages/coding-agent/src/migrations.ts` 的 `migrateToolsToBin()`，扫描 agent `tools/` 下 managed binaries `fd`、`rg`、`fd.exe`、`rg.exe`，默认 dry-run，显式 `-Apply` 才移动到 `bin/` 或删除已存在目标对应的旧 `tools/` 副本。
- 新增 `scripts/verify-coding-agent-tools-to-bin-migration.ps1`，用临时 agent fixture 固定 dry-run、apply、duplicate removal、content preservation、custom tool preserved、directory source skip、missing tools dir 和二次 apply 幂等。
- 将 `coding-agent-tools-to-bin-migration-smoke` 接入 `.github/workflows/tau-ci.yml`、`scripts/plan-release.ps1` 和 `scripts/verify-release-contracts.ps1`。
- 同步 `README.md`、`docs/QUALITY_SCORE.md`、两个 active execution plan 和 `next.md`，明确该切片只关闭 managed fd/rg binary relocation helper，不关闭 auth/settings/session/keybindings migrations、hooks/tools deprecation warnings 或 general custom tool migration parity。

## 设计意图

上游 `migrateToolsToBin()` 只处理 pi 自动提取的 fd/rg managed binaries；custom tools 的提示和迁移属于 `checkDeprecatedExtensionDirs()` 及后续 extension/custom tool parity。Tau helper 因此保持窄边界：不扫描任意工具、不覆盖已有 `bin/<name>`，只在目标缺失时 move，目标存在时删除旧 source，以匹配上游行为并降低用户自定义工具被误动的风险。

## 关键文件

- `scripts/migrate-coding-agent-tools-to-bin.ps1`
- `scripts/verify-coding-agent-tools-to-bin-migration.ps1`
- `.github/workflows/tau-ci.yml`
- `scripts/plan-release.ps1`
- `scripts/verify-release-contracts.ps1`
- `README.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
