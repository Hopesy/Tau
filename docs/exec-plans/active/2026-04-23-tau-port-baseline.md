# Tau pi-mono 移植基线与多应用面 P1 收口计划

## 目标

把 Tau 从“CLI-first 的可运行移植项目”继续推进到“多应用面都已经脱离模板壳、具备真实最小产品切片”的状态，并在此基础上进入第二层能力收口：

- `Tau.CodingAgent` 继续作为最完整主路径
- `Tau.WebUi` 从最小聊天宿主推进到可配置、可持久化的 Web 宿主
- `Tau.Mom` 从本地文件 worker 继续走向真实委派语义
- `Tau.Pods` 从 config CLI 继续走向 pod lifecycle

这份 plan 不再把 `WebUi / Mom / Pods` 当作“只做规划”，而是把它们视为已经进入实现、但仍处于早期产品切片阶段的真实模块。

## 当前阶段结论

截至 2026-04-24，Tau 的真实状态可以概括为：

- `Tau.Ai` 已完成第一轮 provider / auth / model registry 收口，并补了 request-body source-gen serializer runtime 回归
- `Tau.Agent` 继续稳定提供双层循环与工具执行骨架
- `Tau.CodingAgent` 已恢复独立 build/run/test，并新增了显式 provider/model/history 注入 runner 的宿主边界
- `Tau.Tui` 已具备最小 transcript / input buffer / session 层
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择** 的第二层 Web 宿主
- `Tau.Mom` 已从 Worker 模板推进到 **inbox/outbox/archive + --once** 的本地文件委派 worker
- `Tau.Pods` 已从控制台占位推进到 **init/list/validate/status** 的真实 config CLI

因此当前主线已经从“先把 Tau 变成 CLI-first 项目”转到“在守住 CLI 主路径的同时，把其他应用面从第一层切片继续收口到第二层能力”。

## 范围

- 包含：
  - 维持 `Tau.CodingAgent` 的项目级 build/test/运行闭环
  - 继续收口 `Tau.Ai` 的 provider / auth / registry fidelity
  - 推进 `Tau.WebUi` 的会话、配置、流式体验和前端宿主能力
  - 推进 `Tau.Mom` 的 runner seam、Slack/workspace/sandbox 委派语义
  - 推进 `Tau.Pods` 的 SSH / deploy / lifecycle / model management
  - 同步维护 README、architecture、quality、history 与验证命令
- 不包含：
  - 一次性追求与上游 `pi-mono` 的 1:1 全量完成度
  - 在 `Tau.slnx` 未恢复可信前，强行把 solution build 当主门禁
  - 在 release 仍是仓库元数据制品阶段时，假装已经有完整产品发布链

## 背景

- 相关文档：
  - `docs/ARCHITECTURE.md`
  - `docs/product-specs/tau-port-overview.md`
  - `docs/QUALITY_SCORE.md`
  - `docs/CICD.md`
- 相关代码路径：
  - `src/Tau.Ai/`
  - `src/Tau.Agent/`
  - `src/Tau.CodingAgent/`
  - `src/Tau.Tui/`
  - `src/Tau.WebUi/`
  - `src/Tau.Mom/`
  - `src/Tau.Pods/`
  - `tests/`
- 已知约束：
  - `Tau.slnx` 仍会在当前机器上触发 solution metaproj / workload resolver 异常
  - `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` 仍部分依赖 `Reference + HintPath` workaround
  - 当前 Windows 环境下 `bash` 服务仍可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`，所以本地验证要接受“仓库标准命令写 bash，现场执行可退回等价顺序 dotnet 命令”的现实

## 风险

- 风险：多应用面都进入实现后，文档和计划继续停留在 CLI-only 叙事。
  - 缓解方式：每次产品切片推进后，同轮同步 README / architecture / quality / plan / history。
- 风险：`WebUi / Mom / Pods` 继续横向铺开，但每个模块都缺第二层能力和真实验证。
  - 缓解方式：按“最短变成真实产品宿主”的顺序推进：WebUi 会话与配置、Mom 委派 seam、Pods lifecycle。
- 风险：过早清理 `HintPath` workaround，再次踩回 metaproj / workload resolver 异常。
  - 缓解方式：把引用结构收口单列为独立工程任务，不和产品能力改动混做。
- 风险：只在 README 写“支持了某模块”，但没有真实 build/test/run 证据。
  - 缓解方式：每个新增切片必须至少具备 build + test 或 build + runtime smoke。

## 里程碑

1. CLI-first 基线稳定化（已完成）
2. `Tau.Ai` 第一轮 provider / auth / registry 收口（已完成）
3. `Tau.WebUi / Tau.Mom / Tau.Pods` 第一层真实产品切片（已完成）
4. `Tau.WebUi` 第二层能力：持久化 + provider/model 选择（已完成）
5. 多应用面第二层继续收口：
   - WebUi 流式 / richer UX
   - Mom Slack / workspace / sandbox
   - Pods SSH / lifecycle / model management
6. 工程化收口：
   - 解决 `Tau.slnx` / metaproj / workload resolver 异常
   - 收回 `HintPath` workaround
   - 让 release 开始对应真实 Tau 产物

## 实施切片

### 切片 A：CLI-first 与 provider/auth 基线

- `Tau.Tui` 最小交互层
- `Tau.CodingAgent` 宿主抽象与 smoke 测试
- `Tau.Ai` provider/auth/registry 第一轮收口

### 切片 B：多应用面第一层产品切片

- `Tau.WebUi` 最小聊天宿主
- `Tau.Mom` 本地文件委派 worker
- `Tau.Pods` config CLI

### 切片 C：`Tau.WebUi` 第二层能力

- 会话持久化到本地 store
- `/api/catalog` provider/model 列表
- `PUT /api/sessions/{id}` 配置更新入口
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主接线
- 最小测试覆盖 runner/store

### 切片 D：`Tau.Mom` 第二层入口

- 为 runner / result schema 补更稳定 seam
- 把文件委派抽象继续推进到 Slack/workspace/sandbox 可接线状态

### 切片 E：`Tau.Pods` 第二层入口

- 补 SSH / deploy / lifecycle
- 进入真实 pod transport / model lifecycle

### 切片 F：工程化与门禁

- 保持 `scripts/verify-dotnet.sh` 为主 CI
- 在当前环境接受顺序 `dotnet build/test` 作为 bash 不可用时的本地等价验证
- 后续独立处理 `Tau.slnx` 与引用结构收口

## 验证方式

- 仓库标准命令：
  - `bash scripts/verify-dotnet.sh`
  - `bash scripts/verify-dotnet.sh --skip-restore`
- 当前机器上的等价顺序验证：
  - `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-restore`
  - `dotnet build src/Tau.WebUi/Tau.WebUi.csproj --no-restore`
  - `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore`
- 运行态 smoke：
  - `dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088`
  - `GET /api/status`
  - `GET /api/catalog`
  - `POST /api/sessions`
  - 检查 `output/webui-sessions.json`
  - `dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once`

## 阶段退出标准

只有满足下面条件，才能认为这份计划当前阶段基本完成，并进入更高层能力：

- `Tau.CodingAgent` 主路径继续可重复 build/test/run
- `Tau.WebUi` 已具备会话持久化与 provider/model 选择，不再只是内存聊天页
- `Tau.Mom` / `Tau.Pods` 都已经从模板壳进入真实切片，并有明确第二层 backlog
- 仓库文档、质量评分、next 与当前实现状态一致
- `Tau.slnx` / 引用 workaround 已被单独追踪，而不是继续藏在产品改动里

## 进度记录

- [x] CLI-first P0 收口
- [x] `Tau.Tui` 最小交互层
- [x] `Tau.CodingAgent` 宿主抽象与 smoke 测试
- [x] `Tau.Ai` 第一轮 provider / auth / registry 收口
- [x] `Tau.WebUi / Tau.Mom / Tau.Pods` 第一层真实产品切片
- [x] `Tau.WebUi` 第二层：持久化 session + provider/model 选择 + runtime history rehydrate
- [ ] `Tau.WebUi` 流式 UI / richer rendering / attachment
- [ ] `Tau.Mom` Slack / workspace / sandbox / delegation semantics
- [ ] `Tau.Pods` SSH / lifecycle / model management
- [ ] `Tau.slnx` / metaproj / workload resolver 异常收口
- [ ] `HintPath` workaround 收回到更正常的 `ProjectReference`

## 决策记录

- 2026-04-23：决定先把 Tau 收口为 CLI-first 的 .NET 移植项目，而不是同时平推所有应用面。
- 2026-04-23：决定先补 `Tau.Ai` 的 provider / auth / registry，而不是直接把所有应用面做满。
- 2026-04-24：决定把 `Tau.WebUi / Tau.Mom / Tau.Pods` 从“只做规划”改为“进入真实切片实现”。原因是这三个模块已经有 build/run 级证据，继续把它们写成占位会让仓库知识失真。
- 2026-04-24：决定在 `Tau.WebUi` 内引入持久化 session 与 provider/model 选择，而不是继续依赖全局 `TAU_PROVIDER / TAU_MODEL`。原因是 Web 宿主必须支持会话级配置，不应该把全局环境变量当成 session state。
- 2026-04-24：决定把 `RuntimeCodingAgentRunner` 提升为显式 `Create(provider, model, history)` 工厂，同时保留 `CreateDefault()`。原因是 CodingAgent、WebUi、Mom 都需要共享同一 runtime 内核，但宿主配置边界不同。
- 2026-04-24：决定继续保留 `HintPath` workaround，不在本轮产品能力改动里顺手收引用结构。原因是当前 metaproj / workload resolver 异常仍未解除，强行收口风险高于收益。
