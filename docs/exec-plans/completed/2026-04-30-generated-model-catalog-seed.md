# Tau.Ai generated model catalog seed 计划

## 目标

把 Tau.Ai 的 model registry 从“完全手写 `BuiltInModels`”推进到“手写基线 + generated catalog”双层结构：新增可再生成的 seed JSON 与 generator 脚本，产出 `GeneratedBuiltInModels.g.cs`，并先导入一批当前 Tau 已支持 API 的额外模型，再把这批 seed 扩到更接近上游的已支持模型族覆盖。

## 范围

包含：

- 新增 generated model seed JSON
- 新增 generator 脚本，把 seed JSON 生成 `GeneratedBuiltInModels.g.cs`
- `ModelCatalog` 同时合并 `BuiltInModels` 与 generated catalog
- 先导入当前 Tau 已支持 API 的一批新增模型：
  - `azure-openai-responses`
  - `openai-codex`
  - `github-copilot`（仅 `openai-responses` 路径）
  - `google-gemini-cli`
  - `google-antigravity`
- 当前完成时 generated seed 共 66 个模型（Azure 37 / Codex 7 / Copilot Responses 10 / Gemini CLI 4 / Antigravity 8）
- 单测覆盖 generated catalog 合并与新增模型可查询
- 同步 `next.md`、architecture、quality、baseline plan 与 history

不包含：

- 全量同步上游 `models.generated.ts`
- 当前不支持 API 的模型族
- compatibility / capability / routing 元数据全量移植
- default model 策略重写

## 背景

`next.md` 已把 generated models 管线列为 Tau.Ai 的下一个 P0。当前 `BuiltInModels` 仍完全手写，导致每补一个 provider fidelity 切片就要继续手补模型表。上游已经有 `models.generated.ts` 和 `scripts/generate-models.ts`，但 Tau 还没有自己的生成接缝。

## 风险

- 风险：直接照搬上游全量 generated models，会把大量当前 Tau 还没补完的 API 路径一并引入。
  - 缓解：这轮只导入“当前 Tau 已支持 API”的 provider 族，剩余模型全集后续再扩。
- 风险：generated catalog 与手写 `BuiltInModels` 重名冲突。
  - 缓解：本轮 seed 只放新增模型，避免覆盖已有手写条目；合并逻辑保留后写覆盖能力，便于后续迁移。

## 验证方式

- `powershell -ExecutionPolicy Bypass -File scripts/generate-tau-ai-models.ps1`
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test

## 进度记录

- [x] 建立 active plan
- [x] 落 seed JSON / generator / generated catalog
- [x] 补 Tau.Ai 单测
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 验证结果

- `powershell -ExecutionPolicy Bypass -File scripts/generate-tau-ai-models.ps1`：通过，重新生成 `src/Tau.Ai/Registry/GeneratedBuiltInModels.g.cs`。
- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：通过，0 warnings / 0 errors。
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`：通过，65 passed。
- 项目级顺序 build/test：本轮完成后再次执行，用于确认 generated catalog 扩容没有破坏其他项目。

## 决策记录

- 2026-04-30：这轮先建立 Tau 自己的 generated seed/generator 接缝，不直接硬搬上游 `generate-models.ts`。原因是 Tau 当前 Model 结构和 provider 支持面还没与上游完全对齐，先把“可再生成”的最小链做实，比同步一整套上游脚本更稳。

## 后续扩展

- 2026-05-01：在 generated catalog 之上补了共享 typed/default model 解析：`ModelCatalog` 统一负责 default provider/model、canonical `provider/model` 引用和冲突检测，`RuntimeCodingAgentRunner`、`WebChatService`、`FileDelegationProcessor` 复用同一条解析链。
