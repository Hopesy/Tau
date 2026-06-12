# CodingAgent CLI resource discovery flags baseline

- 日期：2026-06-12
- 模块：Tau.CodingAgent
- 切片类型：contract（CLI/config parity）
- 关联：`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` Phase 2 Candidate Queue `CodingAgent top-level CLI/session/config parity`

## 背景

对照上游 `packages/coding-agent/src/cli/args.ts` 的 `Args` 与 `main.ts` 的
`resolveCliPaths(...)` / `createAgentSessionServices({ resourceLoaderOptions })`，上游
支持以下资源发现 CLI 选项：

- 可重复路径：`--extension` / `-e`、`--skill`、`--prompt-template`（外加已实现的 `--theme`）
- 发现开关：`--no-extensions` / `-ne`、`--no-skills` / `-ns`、`--no-prompt-templates` / `-np`
  （外加已实现的 `--no-themes`、`--no-context-files`）

Tau 此前的 `CodingAgentCliArguments.Parse` 把 `--extension`/`-e`、`--skill`、
`--prompt-template` 放进 `OptionsWithValue` 吃掉，把 `--no-extensions`/`-ne`、
`--no-skills`/`-ns`、`--no-prompt-templates`/`-np` 放进 `BooleanOptions` 吃掉：参数被
消费但从不存储，`Program.cs` 也从不把它们接到资源 store。结果是这些上游 CLI 选项在 Tau 中
静默无效，而 `CodingAgentExtensionCommandStore`、`CodingAgentSkillStore`、
`CodingAgentPromptTemplateStore` 早已通过 `explicitPaths` + `includeDefaults` 支持该能力
（`--theme` / `--no-themes` 已经这样接线）。

## 改动

`src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
（`CodingAgentCliArguments`）：

- 记录新增 `NoExtensions` / `ExtensionPaths`、`NoSkills` / `SkillPaths`、
  `NoPromptTemplates` / `PromptTemplatePaths` 字段。
- 从 `OptionsWithValue` 移除 `--extension`/`-e`、`--skill`、`--prompt-template`；从
  `BooleanOptions` 移除 `--no-extensions`/`-ne`、`--no-skills`/`-ns`、
  `--no-prompt-templates`/`-np`。
- 新增 `--no-extensions`/`-ne`、`--no-skills`/`-ns`、`--no-prompt-templates`/`-np`
  布尔解析，与现有 `--no-themes`/`-nt`、`--no-context-files`/`-nc` 一致。
- 新增 `TryConsumeRepeatablePathOption(...)` helper，按 `--theme` 同样的语义解析
  `--extension`/`-e`、`--skill`、`--prompt-template`，支持空格与 `=` 内联两种形式，缺值
  抛出 `error: <option> requires a path argument`。

`src/Tau.CodingAgent/Program.cs`：

- 读取 `cli.NoExtensions/NoSkills/NoPromptTemplates` 与 `cli.ExtensionPaths/SkillPaths/
  PromptTemplatePaths`。
- 把它们接到 `CodingAgentExtensionCommandStore`、`CodingAgentPromptTemplateStore`、
  `CodingAgentSkillStore` 的 `explicitPaths` + `includeDefaults`，沿用 `--theme` /
  `CodingAgentThemeStore` 的接线模式：显式 CLI 路径非空时作为 explicit paths（优先于
  env 配置路径），`no*` 开关关闭对应默认目录发现。

## 行为合同

- `--extension <p>` / `-e <p>`：追加额外 extension 发现路径（可重复），支持相对路径，由 store
  内 `ResolvePath` 解析。
- `--skill <p>` / `--prompt-template <p>`：同上，分别接到 skill / prompt template store。
- `--no-extensions` / `-ne`：跳过默认 user/project extension 目录发现（显式 `-e` 路径仍生效）。
- `--no-skills` / `-ns`、`--no-prompt-templates` / `-np`：同上，跳过对应默认目录。
- 缺值的 `--extension` / `--skill` / `--prompt-template`（含空 `--extension=`）抛出明确错误。

## 验证

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj`：0 警告 0 错误。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter CodingAgentInitialMessageBuilderTests`：60/60。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj`：623/623（新增
  `Parse_CapturesRepeatableResourcePaths`、`Parse_RecognizesResourceDiscoveryToggles`）。

## 剩余缺口

- 该切片只关闭本地 CLI 解析 + 资源 store 接线合同，不验证 extension/skill/prompt-template
  实际运行时行为的端到端 parity。
- 上游 `resolveCliPaths` 的 `isLocalPath` 精确判定（区分本地路径与 package 名）未逐字复刻；
  Tau store 内 `ResolvePath` 对相对路径解析，绝对路径与包名行为后续随 package consumer
  smoke 一起审计。
- 仍不关闭完整 jiti/custom tool extension runtime、interactive config selector、真实
  package/network smoke 与 CodingAgent CLI/session/config 行的 final `verified` 状态。
