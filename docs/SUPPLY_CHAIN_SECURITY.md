# 供应链安全

Tau 当前的供应链安全重点，不是部署平台，而是：

- GitHub Actions 工作流不可漂移
- 仓库依赖变化可见
- 仓库级 release artifact 可追溯
- 后续 .NET 真实构建链接入时不把这些基础能力绕开

## 当前默认控制项

- Pull Request 上做依赖变更审查
- 用 OSV 扫描依赖清单
- 为 release artifact 生成 SBOM
- 为 release artifact 生成 provenance attestation
- 用 OpenSSF Scorecard 做仓库姿态分析
- 所有 GitHub Actions 固定到 commit SHA

## Tau 当前真正的依赖面

### 已进入仓库主线的依赖面

- GitHub Actions
- bash 脚本
- .NET 项目文件与 NuGet 依赖

### 还没有形成真实交付面的依赖面

- `Tau.WebUi` 的浏览器端/前端依赖链
- `Tau.Mom` 的外部集成依赖链
- `Tau.Pods` 的远程运行时与基础设施依赖链

因此当前供应链文档要反映的是真实状态：  
**骨架有了，但产品级依赖治理还没真正打通。**

## 当前 workflow 对应关系

- `actions/dependency-review-action`
  - 用于审查 PR 中的依赖变化
- `google/osv-scanner-action`
  - 用于扫描已知漏洞
- `anchore/sbom-action`
  - 生成 SPDX SBOM
- `actions/attest-build-provenance`
  - 为 release artifact 生成 provenance
- `ossf/scorecard-action`
  - 分析仓库级安全姿态
- `scripts/check-action-pinning.sh`
  - 防止 workflow 回退到浮动 tag

## 当前局限

### 1. SBOM 和 provenance 还没有绑定真实产品产物

现在 release 产物还是仓库元数据包，不是 Tau 的真实可执行发布物。  
因此：

- SBOM 是“仓库级 SBOM”
- provenance 是“元数据产物 provenance”

不是最终面向用户交付件的完整供应链证明。

### 2. .NET 构建链还没成为 CI 主线

这意味着：

- NuGet 依赖虽然存在
- 但还没有被主 CI 持续验证

这也是当前必须补齐的工程缺口之一。

## Tau 后续要补的供应链动作

### 近期

- 把 `dotnet build` / `dotnet test` 接入主 CI
- 让 NuGet 依赖真正进入持续验证面

### 中期

- 让 `scripts/release-package.sh` 产出真实 Tau 发布件
- 让 SBOM 与 provenance 对应真实构建输出

### 后期

- 针对 `WebUi / Mom / Pods` 各自的交付链细分依赖面
- 如果存在部署平台，再考虑部署侧 provenance 验证
