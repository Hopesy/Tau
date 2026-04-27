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

这样做不是为了回避真实构建，而是因为当前 `Tau.slnx` 的 solution-level 异常已经坐实：**项目级验证链是真实稳定的，solution build 还不是。**

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

CI 还没有把这些 runtime smoke 全自动化进 workflow，但文档和本地验证链已经把它们作为真实可重复的检查项记录下来。

### 4. Markdown / workflow 基础检查

- Markdown lint
- GitHub Action 固定 SHA 检查

### 5. 供应链骨架

- Dependency Review
- OSV / SBOM / Scorecard / provenance 相关 workflow 骨架

## 当前环境约束与现实处理

### `Tau.slnx` 仍不可信

当前 `dotnet build Tau.slnx --no-restore` 在这台机器上仍会碰到：

- solution metaproj 异常
- workload resolver 异常
- “0 warning / 0 error 但构建失败”

因此当前不能把 solution build 作为唯一可信门禁。

### 本机 bash 服务不稳定

仓库规范仍要求：

- workflow 和脚本统一走 bash

但在当前 Windows 环境，本地直接调用 bash 可能报：

- `Bash/Service/CreateInstance/E_ACCESSDENIED`

这属于环境问题，不是仓库脚本语义错误。

因此当前接受的现实是：

- **仓库标准命令继续写 bash**
- **本地现场验证可退回等价顺序 `dotnet build/test/run` 命令**

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

### 第二阶段：补运行态 smoke 自动化

优先考虑：

- `Tau.WebUi` 的 status/catalog/session persistence smoke
- `Tau.Mom` 的 `--once` 文件委派 smoke

### 第三阶段：让 release 对应真实产物

优先考虑：

- `Tau.CodingAgent` 的发布产物

然后再扩展到：

- `Tau.WebUi`
- `Tau.Mom`
- `Tau.Pods`

### 第四阶段：收口 solution / 引用结构

单独处理：

- `Tau.slnx` / metaproj / workload resolver 异常
- `HintPath` workaround 收回到更正常的 `ProjectReference`

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

因此当前文档必须反映真实情况：**仓库已有可重复的项目级验证路径，WebUi/Mom 也已有运行态 smoke，但 solution build 与真实 release 仍未完全收口。**
