# 2026-05-29 22:57 CodingAgent deprecated extension dirs audit helper

## 用户诉求

按 `goal.md` 继续推进 Tau 对 pi-mono 的 100% 可审计移植，不把局部 helper 或 smoke 当成最终完成。

## 主要变更

- 新增 `scripts/audit-coding-agent-deprecated-extension-dirs.ps1`，对照上游 `packages/coding-agent/src/migrations.ts` 的 `checkDeprecatedExtensionDirs(...)` warning audit，检查 Tau agent base directory 下 deprecated `hooks/` 目录和 `tools/` 目录中的 custom entries。
- 新增 `scripts/verify-coding-agent-deprecated-extension-dirs-audit.ps1`，用临时 base directory fixture 固定 hooks warning、custom tools warning、managed fd/rg ignore、hidden entry ignore、file tools path skip、missing base 和 no-mutation boundary。
- 将 `coding-agent-deprecated-extension-dirs-audit-smoke` 接入 `.github/workflows/tau-ci.yml`、`scripts/plan-release.ps1` 和 `scripts/verify-release-contracts.ps1`。
- 同步 `README.md`、`docs/QUALITY_SCORE.md`、两个 active execution plan 和 `next.md`，明确该切片只关闭 deprecated hooks/tools warning audit helper，不关闭 actual hooks/custom tools migration to extensions、auth/settings/session/keybindings migrations、general custom tool migration parity 或 TypeScript extension runtime。

## 设计意图

上游 `checkDeprecatedExtensionDirs(...)` 只收集 warning：`hooks/` 存在时提示 hooks 已更名为 extensions，`tools/` 中存在非 managed fd/rg 且非隐藏 entry 时提示 custom tools 已并入 extensions。Tau helper 因此保持只读审计边界，默认检查用户 `~/.tau` 与当前项目 `./.tau`，显式 `-BaseDirectory` / `-Label` 可固定测试或迁移审计目标；脚本不移动、不删除、不覆盖任何文件，避免把 warning audit 误写成未完成的 hooks/custom tools migration runtime。

## 关键文件

- `scripts/audit-coding-agent-deprecated-extension-dirs.ps1`
- `scripts/verify-coding-agent-deprecated-extension-dirs-audit.ps1`
- `.github/workflows/tau-ci.yml`
- `scripts/plan-release.ps1`
- `scripts/verify-release-contracts.ps1`
- `README.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `next.md`
