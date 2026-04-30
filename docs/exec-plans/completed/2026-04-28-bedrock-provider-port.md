# Tau.Ai Bedrock ConverseStream provider 移植计划

## 目标

把 `bedrock-converse-stream` 从“注册表可发现但运行时直接返回占位错误”推进到可本地验证的真实调用路径：

- 构造 Amazon Bedrock Runtime `ConverseStream` 请求体
- 支持 Bedrock bearer token 与 AWS SigV4 两条认证路径
- 解析 Bedrock event stream，把 `messageStart / contentBlock* / messageStop / metadata` 映射回 Tau 的 13 类 stream event
- 用 `StubHandler` 测试 request body、认证 header、流式事件和无凭证错误，不依赖真实 AWS 网络

## 范围

包含：

- `src/Tau.Ai/Providers/Bedrock/` provider 实现
- `tests/Tau.Ai.Tests/BedrockProviderTests.cs` 单元测试
- `next.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、基线 plan 与 history 同步

不包含：

- AWS shared credentials/profile/SSO 完整链路
- 真实 AWS e2e 调用
- Bedrock 全模型 family 的所有附加字段
- Copilot/OAuth/generated model catalog 的其他 P0 缺口

## 官方协议依据

- Bedrock Runtime `ConverseStream` 是 `POST /model/{modelId}/converse-stream`，流里包含 `messageStart`、`contentBlockStart`、`contentBlockDelta`、`contentBlockStop`、`messageStop`、`metadata` 等事件。
- SigV4 需要 canonical request、string-to-sign、credential scope 与 `AWS4-HMAC-SHA256` Authorization header。
- Bedrock bearer token 路径通过 `Authorization: Bearer <token>`，权限侧对应 `bedrock:CallWithBearerToken`。

## 实施步骤

1. 替换 Bedrock 占位 provider，新增：
   - 请求 URL/JSON payload builder
   - bearer token auth
   - env/explicit AWS credentials SigV4 signer
   - binary AWS event stream parser
   - Bedrock event translator
2. 增加单元测试：
   - 注册表使用专用 `BedrockProvider`
   - bearer token path 不走 SigV4，并可解析 text/usage stream
   - SigV4 path 写入 `x-amz-date`、`x-amz-content-sha256`、`x-amz-security-token` 和 AWS4 Authorization
   - no credentials path 返回 clean `ErrorEvent`
   - tool call event lifecycle 与 request-side tool/result payload
   - `StreamSimple` reasoning 写入 `additionalModelRequestFields`
3. 同步文档与 history。
4. 运行 `Tau.Ai` 局部 build/test，再运行当前 Windows 等价全量项目级 build/test。

## 风险与取舍

- 取舍：本轮先做 env/explicit credentials，不实现 shared profile/SSO。原因是 SigV4 与 ConverseStream parser 才是当前 provider 从占位到真实可用的关键闭环。
- 风险：Bedrock event stream 是 binary event stream，不是 SSE。缓解方式是单独实现最小 AWS event stream 解码，并用构造的 binary stream 做回归。
- 风险：不同模型的 reasoning / cache / image schema 有差异。缓解方式是先覆盖 Claude 主路径与 Tau 当前抽象能表达的字段，把更细模型差异继续留在 `next.md`。

## 验证命令

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore --verbosity minimal
dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal
```

完成后再跑仓库当前采用的项目级顺序 build/test。

## 退出标准

- Bedrock provider 不再返回 SigV4 未实现的占位错误
- bearer 与 SigV4 两条认证路径都有测试
- text/tool/usage event stream translation 有测试
- 文档、质量评分、next、history 与实际状态一致
- 项目级 build/test 通过

## 进度记录

- [x] 建立独立 active plan
- [x] 实现 Bedrock provider / signer / event stream parser
- [x] 补单元测试
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan


## 验证结果

- 2026-04-29：项目级顺序 build/test 通过。
- `Tau.Ai.Tests`：36 passed。
- `Tau.Agent.Tests`：2 passed。
- `Tau.Tui.Tests`：4 passed。
- `Tau.CodingAgent.Tests`：6 passed。
- `Tau.Pods.Tests`：7 passed。
- 所有项目级 build 均为 0 warnings / 0 errors。
