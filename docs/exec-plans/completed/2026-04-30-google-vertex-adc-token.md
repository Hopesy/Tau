# Google Vertex ADC token exchange 支持计划

## 目标

把 `google-vertex` provider 从“发现 ADC 后提示未实现”推进到可本地验证的真实 REST 鉴权路径：保留 `GOOGLE_CLOUD_API_KEY` / `x-goog-api-key` 模式，同时支持 Application Default Credentials 文件中的 service account 与 authorized user refresh token，并在调用 Vertex `streamGenerateContent` 前换取 OAuth access token。

## 范围

包含：

- `GoogleVertexProvider` 接入 Bearer access token 鉴权
- 最小 ADC resolver：`GOOGLE_APPLICATION_CREDENTIALS` / 默认 gcloud ADC 文件 / 显式 options credentials file
- service account JSON：RS256 JWT bearer grant
- authorized user JSON：refresh token grant
- API key 路径、ADC 成功路径和错误路径单元测试
- `next.md`、architecture、quality、baseline plan 与 history 同步

不包含：

- workload identity federation / external account STS
- service account impersonation
- gcloud CLI 子进程取 token
- access token 跨请求缓存与刷新持久化
- 真实 Google Cloud e2e

## 依据

- Google Vertex AI REST 可使用 ADC 鉴权，并以 `Authorization: Bearer` 发送 access token。
- Google service account OAuth 2.0 使用 JWT bearer grant：`grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` + `assertion`。
- Google authorized user refresh token flow 使用 `grant_type=refresh_token` 调用 `https://oauth2.googleapis.com/token`。

## 风险

- 风险：手写 JWT / token exchange 容易产生不可观测的云端 401/400。
  - 缓解：用 StubHandler 固定 token endpoint 表单、JWT claim、最终 Vertex Authorization header。
- 风险：ADC 类型很多，过度扩展会拖入 Google auth library 等依赖。
  - 缓解：本切片只支持 service account 与 authorized user，其他类型给明确错误。
- 风险：token 缓存缺失导致每次 stream 多一次 token 请求。
  - 缓解：先保证协议正确和可测试，缓存留后续 auth 持久化切片。

## 验证方式

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test。

## 进度记录

- [x] 建立 active plan
- [x] 实现 ADC access token resolver
- [x] 接入 provider 并补单元测试
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 决策记录

- 2026-04-30：不引入 Google auth SDK，延续 Tau.Ai 当前零 provider SDK 依赖策略；先实现 service account 与 authorized user 两种最常见 ADC 文件。
## 验证结果

- 2026-04-30：项目级顺序 build/test 通过。
- `Tau.Ai.Tests`：44 passed。
- `Tau.Agent.Tests`：2 passed。
- `Tau.Tui.Tests`：4 passed。
- `Tau.CodingAgent.Tests`：6 passed。
- `Tau.Pods.Tests`：7 passed。
- 所有项目级 build 均为 0 warnings / 0 errors。