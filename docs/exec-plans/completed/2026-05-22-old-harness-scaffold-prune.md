# 旧 harness-init scaffold 清理计划

## 目标

把 Tau 仓库里旧版 `harness-init` 初始化时带入、但当前瘦身模板已经不再需要的通用脚手架删掉，同时保留 Tau 自己已经写实的项目文档、验证脚本和迁移计划。

## 范围

- 包含：
  - 删除旧模板的 `Makefile`、`CONTRIBUTING.md`。
  - 删除旧模板的通用 docs / hygiene / action pinning / release package / init helper 脚本。
  - 删除已经与当前仓库事实不一致的旧 `docs/CICD.md` 与 `docs/SUPPLY_CHAIN_SECURITY.md`。
  - 删除空的 generated docs 占位和仍在描述模板本身的 design-docs 目录。
  - 同步更新仍指向这些旧文件的导航、安全/稳定性说明和 release notes 模板残留。
- 不包含：
  - 不删除 Tau 真实业务代码。
  - 不删除 `scripts/verify-dotnet.ps1`、`scripts/verify-dotnet.sh`、`scripts/generate-tau-ai-models.ps1`。
  - 不删除已写实的 `docs/QUALITY_SCORE.md`、`docs/PRODUCT_SENSE.md`、`docs/DESIGN.md`、`docs/FRONTEND.md`、`docs/SECURITY.md`、`docs/RELIABILITY.md`、`docs/product-specs/`、`docs/references/`、`docs/releases/`。

## 背景

- 当前 `harness-init` 瘦身版不再默认带 Makefile、scripts、CONTRIBUTING、GitHub workflow、CI/CD 或供应链占位文档。
- Tau 是旧模板初始化后的真实 .NET 移植项目，部分旧脚手架已经和现状冲突：仓库无 `.github/workflows`，但旧检查脚本仍要求 workflow、action pinning 和仓库元数据 release package。
- Tau 当前仍需要保留自己的 .NET 验证入口和项目文档。

## 风险

- 风险：误删仍被 Tau 使用的脚本或文档。
- 缓解方式：只删除明确来自旧模板、且引用已不符合当前仓库事实的文件；保留项目级 verify/model generator 脚本和已写实文档。
- 风险：删除后留下过期引用。
- 缓解方式：用 `rg` 检查已删除文件名和旧脚手架命令引用。

## 里程碑

1. 盘点当前瘦身模板与 Tau 文件树差异。
2. 删除旧模板脚手架并同步导航文档。
3. 验证引用和 diff 格式。

## 验证方式

- 命令：`rg -n "Makefile|CONTRIBUTING|check-docs|check-repo-hygiene|check-action-pinning|release-package|scripts/ci|init-project|new-history|new-exec-plan|SUPPLY_CHAIN_SECURITY|CICD.md|docs/CICD|docs/SUPPLY" AGENTS.md README.md docs scripts`
- 命令：`git diff --check`
- 手工检查：确认保留 Tau 项目级脚本和业务文档。

## 进度记录

- [x] 里程碑 1
- [x] 里程碑 2
- [x] 里程碑 3

## 决策记录

- 2026-05-22：按当前 `harness-init` 瘦身模板删除旧通用脚手架，但不把“当前瘦身模板默认没有”机械等同于“Tau 不需要”。凡是已经承载 Tau 真实产品、架构、质量或验证事实的文档与脚本，本轮保留；仍是空占位或描述模板本身的旧目录则删除。
