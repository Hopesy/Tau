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

这部分仍然是仓库层的第一门禁。

### 2. .NET 项目级验证链

主 CI 现在直接调用：

- `bash scripts/verify-dotnet.sh`

本地 Windows 兜底入口：

- `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1`
- `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`

这个脚本会显式按顺序执行：

- restore `src/*.csproj`
- restore `tests/*.csproj`
- build `src/*.csproj`
- build `tests/*.csproj`
- test `tests/*.csproj --no-build --no-restore`

当前覆盖的真实项目包括：

- `Tau.Ai`
- `Tau.Agent`
- `Tau.Tui`
- `Tau.CodingAgent`
- `Tau.WebUi`
- `Tau.Mom`
- `Tau.Pods`
- `Tau.Ai.Tests`
- `Tau.Agent.Tests`
- `Tau.Tui.Tests`
- `Tau.CodingAgent.Tests`
- `Tau.Pods.Tests`

这样做不是为了回避真实构建，而是为了在 CI 中保留更细粒度、可定位的项目级门禁；当前 `Tau.slnx` 也已经能通过 `dotnet build Tau.slnx --verbosity minimal`。

### 3. 运行态 smoke 的当前现实

除了 build/test，当前仓库已经有两个高价值 runtime smoke 证据：

- `Tau.WebUi`
  - `GET /api/status`
  - `GET /api/catalog`
  - `POST /api/sessions`
  - `output/webui-sessions.json` 持久化落盘
- `Tau.Mom`
  - `--once` 能真实处理 `mom/inbox/*`
  - 写出 `mom/outbox/*.json`
  - 归档到 `mom/archive/*`

CI 还没有把这些 runtime smoke 接到 GitHub workflow，但 Windows 本机 `scripts/verify-dotnet.ps1 -SkipRestore -RunSmoke` 已能自动执行这两条最小 smoke。

### 4. Markdown / workflow 基础检查

- Markdown lint
- GitHub Action 固定 SHA 检查

### 5. 供应链骨架

- Dependency Review
- OSV / SBOM / Scorecard / provenance 相关 workflow 骨架

## 当前环境约束与现实处理

### `Tau.slnx` 当前已可 build

当前已经验证通过：

- `dotnet build Tau.slnx --verbosity minimal`
- `dotnet build Tau.slnx --no-restore --verbosity minimal`

因此 solution build 可以作为辅助门禁，但当前 CI 仍保留项目级验证链来获得更直接的失败定位。

### 本机 bash 服务不稳定

仓库规范仍要求：

- workflow 和脚本统一走 bash

但在当前 Windows 环境，本地直接调用 bash 可能报：

- `Bash/Service/CreateInstance/E_ACCESSDENIED`

这属于环境问题，不是仓库脚本语义错误。

因此当前接受的现实是：

- **仓库标准命令继续写 bash**
- **Windows 本机可优先执行 `scripts/verify-dotnet.ps1`**
- **如果脚本入口不可用，再退回等价顺序 `dotnet build/test/run` 命令**

### restore 污染时的离线收口

如果 `obj/project.assets.json` 被失败 restore 污染，当前可能出现：

- `NU1801`
- `NU1900`
- `NU1101 Microsoft.NET.ILLink.Tasks`

当前环境下更稳的修法是：

```powershell
$env:DOTNET_CLI_HOME='C:\Users\zhouh'
dotnet restore <project> --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
```

然后再执行：

```powershell
dotnet build <project> --no-restore
```

这不是理想状态，而是当前网络受限 / audit 源不可达时的现实兜底。

## 当前 release 的真实含义

`release.yml` 现在会调用：

- `scripts/release-package.sh`

当前打出的仍然是：

- `repo-metadata.tgz`
- `release-manifest.json`
- `sbom.spdx.json`

它封装的是仓库元数据，不是：

- `Tau.CodingAgent` 可执行产物
- `Tau.WebUi` 发布包
- `Tau.Mom` 或 `Tau.Pods` 的真实交付件

所以当前 release 只能理解为：

> 仓库级可追溯制品骨架已搭好，但尚未替换成 Tau 的真实发布链。

## 近期接入顺序

### 第一阶段：守住真实 .NET 项目级门禁

现已加入：

- `bash scripts/verify-dotnet.sh`

并且已经覆盖到 `WebUi / Mom / Pods` 三个应用面。

### 第二阶段：把已存在的本地 smoke 接到 CI

优先考虑：

- 把 `scripts/verify-dotnet.ps1 -RunSmoke` 中的 `Tau.WebUi` smoke 接到 workflow
- 把 `scripts/verify-dotnet.ps1 -RunSmoke` 中的 `Tau.Mom --once` smoke 接到 workflow

### 第三阶段：让 release 对应真实产物

优先考虑：

- `Tau.CodingAgent` 的发布产物

然后再扩展到：

- `Tau.WebUi`
- `Tau.Mom`
- `Tau.Pods`

### 第四阶段：收口 solution / 引用结构

单独处理：

- 运行态 smoke 自动化
- Windows / bash 双入口验证脚本的长期维护

## 当前默认约束

- GitHub Actions 必须继续固定到 commit SHA
- 仓库脚本与 workflow 统一走 bash
- 不因为接入 .NET 构建链就绕开现有仓库检查脚本
- 不把当前本机环境噪音误写成仓库代码问题

## 当前推荐命令

仓库标准入口：

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
bash scripts/ci.sh
```

Windows 本机入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
```

当前机器上的等价顺序验证：

```powershell
dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore
dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore
dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore
dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore

dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-build --no-restore
dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-build --no-restore
dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-build --no-restore
```

运行态 smoke：

```powershell
dotnet run --project src\Tau.WebUi\Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088
dotnet run --project src\Tau.Mom\Tau.Mom.csproj --no-build -- --once
```

因此当前文档必须反映真实情况：**仓库已有可重复的项目级验证路径，`Tau.slnx` 当前也已可 build，Windows 本机 `verify-dotnet.ps1 -RunSmoke` 已可自动执行 `WebUi/Mom` 最小 smoke，但真实 release 与 CI 级 smoke 接线仍未收口。**
