# CI/CD 说明

Tau 当前的 CI/CD 已经不再是纯模板仓库，但也还没有进入完整产品发布阶段。  
这份文档记录的是 **当前真实存在的流水线能力**，以及下一步该怎么把它接到 Tau 的实际 .NET 构建链上。

## 当前已有的 workflow

仓库当前实际存在：

- `ci.yml`
- `docs-check.yml`
- `repo-hygiene.yml`
- `supply-chain-security.yml`
- `release.yml`

## 当前 CI 覆盖了什么

### 1. 仓库与文档基线

通过脚本：

- `scripts/check-docs.sh`
- `scripts/check-repo-hygiene.sh`
- `scripts/check-action-pinning.sh`
- `bash -n` 校验 `scripts/*.sh`

这部分已经真实工作，并且本轮文档收口时已再次验证通过。

### 2. .NET 项目级验证链

主 CI 现在直接调用：

- `bash scripts/verify-dotnet.sh`

这个脚本会显式按顺序执行：

- restore `src/*.csproj`
- restore `tests/*.csproj`
- build `src/*.csproj`
- build `tests/*.csproj`
- test `tests/*.csproj --no-build --no-restore`

这样做不是为了回避真实构建，而是因为当前 `Tau.CodingAgent` 的工程结构和 `Tau.slnx` 的 solution-level 异常都已经坐实：**项目级验证链是真实稳定的，solution build 还不是。**

### 3. Markdown / workflow 基础检查

- Markdown lint
- GitHub Action 固定 SHA 检查

### 4. 供应链骨架

- Dependency Review
- OSV / SBOM / Scorecard / provenance 相关 workflow 骨架

## 当前还没完全收口的关键能力

这是 Tau 当前还没完全收口的 CI/CD 缺口：

- **主 CI 已改成项目级 restore/build/test，但 release 产物还不是 Tau 的真实构建制品**
- **当前 `Tau.slnx` 在部分本机环境上还会碰到 .NET 10 SDK/workload 解析异常**

也就是说，当前流水线更像“仓库治理层 + 项目级门禁”，还不是“产品交付层”。

## 当前 release 的真实含义

`release.yml` 现在会调用：

- `scripts/release-package.sh`

而这个脚本当前打出来的本质上是：

- `repo-metadata.tgz`
- `release-manifest.json`
- `sbom.spdx.json`

它封装的是仓库元数据，不是：

- `Tau.CodingAgent` 可执行产物
- `Tau.WebUi` 发布包
- `Tau.Mom` 或 `Tau.Pods` 的真实交付件

所以当前 release 只能理解为：

> “仓库级可追溯制品骨架已搭好，但尚未替换成 Tau 的真实发布链。”

## 近期接入顺序

按 Tau 当前目标，CI/CD 应按下面顺序推进：

### 第一阶段：守住真实 .NET 项目级门禁

现已加入：

- `bash scripts/verify-dotnet.sh`

当前主 CI 不再直接调用 `dotnet build Tau.slnx`，原因是这台机器上已经坐实有 solution metaproj / workload resolver 异常；而逐项目验证链已经真实可跑，也更贴合目前 `Tau.CodingAgent` 使用 `HintPath` 引用 sibling 输出 DLL 的工程现状。

### 第二阶段：让 release 对应真实产物

优先考虑：

- `Tau.CodingAgent` 的发布产物

在此之前，不要假装已经有完整发布体系。

### 第三阶段：再考虑应用面制品

只有在对应模块真正进入实现后，才考虑：

- `Tau.WebUi`
- `Tau.Mom`
- `Tau.Pods`

各自的构建、打包与发布。

## 当前默认约束

- GitHub Actions 必须继续固定到 commit SHA
- 脚本统一走 bash
- 不因为接入 .NET 构建链就绕开现有仓库检查脚本

## 当前推荐命令

当前最稳的本地验证链：

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
bash scripts/ci.sh
```

原因不是 Tau 不需要 solution 级验证，而是当前阶段：

- 所有 `src/*.csproj` / `tests/*.csproj` 已能逐项目 restore / build / test
- `Tau.CodingAgent.csproj` 已恢复独立 build，`Tau.CodingAgent` 也可最小启动
- `Tau.slnx` 仍有 solution metaproj / workload resolver 级异常，`dotnet build Tau.slnx` 会直接失败但不给出正常编译错误

因此当前文档必须反映真实情况：**仓库已有可重复的项目级验证路径，但 solution build 还没有回到完全可信状态。**
