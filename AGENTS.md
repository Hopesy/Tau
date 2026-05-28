
# harness-init
本项目是参考项目C:\Users\zhouh\Desktop\pi-mono-main的.NET移植版。
学习 MAGI 的三脑原则 轮流执行
1.审视，提出问题
2.执行，解决问题
3.提升，指引方向。
4.按照这个大轮次进行完整功能的移植，执行完一个计划后，开始指定下一步移植计划，不要停止思考和行动

## 先记住这三件事

- **复杂任务先落 execution plan**：不要只靠聊天上下文推进；只要任务跨轮次、跨提交、风险偏高或需要阶段性验证，就先读 `docs/PLANS_GUIDE.md` 并把 plan 落到 `docs/exec-plans/`。
- **实质变更必须补 history**：只要仓库内容发生了真实修改，就在同一轮把记录写进 `docs/histories/`；history 是默认动作，不是收尾时可选加分项。
- **当前移植速度优先**：默认用多 Agent 并行推进互不重叠的模块切片，降低大段文档同步和低收益单元测试消耗，把时间优先投到 pi-mono parity 的真实行为缺口和可运行 baseline。

其他文档、规范和检查项，默认都服务于这三件事：让 plan 可执行、让 history 可追溯、让移植速度优先服务真实 parity 缺口。

## 每轮开始先读

- `docs/REPO_COLLAB_GUIDE.md`：仓库级协作、提交、文档同步与测试约定。
- `docs/ARCHITECTURE.md`：仓库整体结构和预期边界。

## 代码改完前要读

- `docs/HISTORY_GUIDE.md`：什么时候记 history、怎么命名、怎么脱敏；这是默认必做项。
- `docs/QUALITY_SCORE.md`：当前质量分层和主要短板。

## 按任务需要选读

- `docs/PLANS_GUIDE.md`：什么时候要写 execution plan，怎么维护；复杂任务优先看这个。
- `docs/RELIABILITY.md`：运行稳定性、观测性和上线前的基本要求。
- `docs/SECURITY.md`：认证、数据处理、外部集成等安全默认约束。
- `next.md`：当前剩余缺口和长期移植路线；具体推进以 active execution plan 为准。

## 工作规则

- 优先选择小而清晰、对仓库和 Agent 都友好的抽象。
- prompt、规则、架构约束尽量都版本化落在仓库里。
- 复杂任务不要只靠聊天上下文，应该先落 execution plan 再推进。
- 完成的代码变更要记到 `docs/histories/`，不要把 history 留到事后补票。

## 移植执行策略

- **多 Agent 并行优先**：当切片能按 `Tau.Ai`、`Tau.CodingAgent`、`Tau.Tui`、`Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 等模块边界隔离时，默认开 4-6 个互斥 worker 并行推进。worker 只改自己的模块和相邻测试，不改 README、architecture、quality、history、scripts 或共享验证链；主控负责冲突整合、事实确认、最小文档同步和提交边界。
- **文档同步降速但不取消**：不再为每个 helper、内部 seam、测试数量变化大段同步 README/ARCHITECTURE/QUALITY。默认只同步用户可见命令/API/env/config 行为、跨模块架构或安全边界、以及会影响后续 Agent 判断的 plan/next/history 决策。
- **单元测试风险分层**：共享 runtime、provider 协议、持久化格式、secret/auth、并发/取消、公开 CLI/API 合同必须有 targeted tests 或 smoke；局部 UI 文案、纯映射和一次性 adapter glue 可以先用 build + smoke + 现有回归覆盖。
- **验证选最短信号链**：worker 阶段跑模块级 build/targeted test/smoke；主控阶段串行跑必要的 `dotnet build/test` 或 `verify-dotnet.ps1 -SkipRestore`。不要并行跑会写同一 `bin/obj/output/.tau` 的 build/test。
