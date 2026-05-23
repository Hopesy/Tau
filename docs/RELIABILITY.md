# 稳定性与可运维性

Tau 当前还处在移植收口期，所以可运维性目标要现实：  
先守住 **“关键路径可运行、失败时能定位、CI 能复现”**，再谈更复杂的观测和恢复体系。

## 当前关键路径

当前唯一必须优先守住的路径是：

1. `bash scripts/verify-dotnet.sh`（或本机已 restore 时 `bash scripts/verify-dotnet.sh --skip-restore`）
2. `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`
3. 启动后完成一轮真实输入、模型响应、工具调用和结束

只要这条路径不稳定，其他产品面一律不算进入可实施状态。

## 启动与基本可用性要求

### 当前基线

- 显式项目级 build/test 链必须长期保持通过。
- `Tau.CodingAgent` 必须能在本地启动并输出明确的启动状态。
- 缺少关键配置时，必须明确失败，不允许静默退化成看似正常但不可用的状态。

### 当前已知现实

- `Tau.CodingAgent` 目前默认依赖 `OPENAI_API_KEY`。
- `Tau.CodingAgent.csproj` 已恢复独立 build / run。
- `Tau.slnx` 仍有 solution metaproj / workload resolver 级异常，因此当前不把 solution build 当作最小可用性门禁。
- `Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 还不是产品态，不纳入当前基本可用性承诺。

## 日志与观测约定

当前阶段先遵循“最小但可定位”的原则。

### 必须能看到的信号

- 模型调用是否开始
- 是否进入流式输出
- 是否触发工具调用
- 工具执行成功还是失败
- 运行中断发生在哪个阶段

### 当前实现原则

- CLI 路径优先使用控制台输出暴露阶段信息。
- 当控制台输出不足以定位问题时，再补最小日志，不提前上复杂日志框架。
- 新增日志时，优先记录阶段、工具名、错误类型，不记录敏感输入全文。

## Timeout / Retry / Abort 原则

- **优先显式失败，不盲目重试。**
- 对外部 provider 调用：
  - 必须支持 `CancellationToken`
  - 必须能从用户中断或运行时取消中安全退出
- 对工具执行：
  - 先把失败显式返回给运行时
  - 不在工具内部做隐式多次重试，避免把错误现场抹平

## 本地与 CI 验证要求

### 本地

- 改 `src/` 下代码后，Windows 本机优先跑 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
- bash 可用时，可跑 `bash scripts/verify-dotnet.sh --skip-restore`
- 改关键运行路径时，补一轮 `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`
- 改 CLI 交互相关代码时，至少手工跑一轮 `Tau.CodingAgent`

### CI

当前真实状态：

- 旧 harness-init 的 docs / hygiene / action pinning / release package 脚手架已移除。
- `scripts/verify-dotnet.ps1` 和 `scripts/verify-dotnet.sh` 是当前项目级 .NET restore / build / test 门禁。
- `Tau.slnx` 已能 build，但日常排障仍优先使用显式项目顺序的 verify 脚本定位失败。

近期目标：

- 继续让 CI 成为 `Tau.CodingAgent` P0 路径的最小守门人
- 真正需要远端 CI 时，基于 Tau 的 .NET 验证链重新接入，而不是恢复旧模板的通用 GitHub workflow 骨架。

## 常见故障排查顺序

出现问题时，默认按这个顺序排：

1. **能否完成项目级验证链**
   - `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
   - 或 `bash scripts/verify-dotnet.sh --skip-restore`
2. **失败发生在启动前还是运行时**
   - 配置缺失 / 依赖装配 / provider 注册 / 命令入口
3. **运行时卡在模型调用前、中、后哪个阶段**
   - 输入
   - 流式响应
   - 工具调用
   - 状态回写
4. **问题属于核心层还是宿主层**
   - `Tau.Ai`
   - `Tau.Agent`
   - `Tau.CodingAgent`
   - `Tau.Tui`

## 当前不做的事

- 还没形成稳定运行面时，就引入 metrics、trace、集中日志平台等复杂设施
- 对 `Tau.WebUi / Tau.Mom / Tau.Pods` 给出超出现状的可用性承诺
- 用“多试几次”代替对失败阶段的定位
