# Bedrock shared credentials/profile 支持计划

## 目标

在刚完成的 Bedrock ConverseStream provider 基础上，补齐最小 AWS shared credentials/profile 读取能力，让 `AWS_PROFILE` 不再只能触发“未实现”错误：

- 支持 `~/.aws/credentials` 与 `~/.aws/config` 的静态 access key / secret / session token
- 支持 profile 选择：explicit options > `AWS_PROFILE` > `default`
- 支持 region 选择：explicit options > env > shared config/credentials profile > `us-east-1`
- 用单元测试固定 request signing scope、Authorization credential、session token 与 region endpoint

## 范围

包含：

- Bedrock provider credential resolver 小模块
- `BedrockOptions` 增加 `Profile / CredentialsFile / ConfigFile`
- 单测覆盖 profile 文件 credentials 与 region 解析
- 文档、history、quality/next 同步

不包含：

- AWS SSO / `sso_session`
- AssumeRole `role_arn/source_profile`
- `credential_process`
- IMDS/ECS/web identity

## 验证

- `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
- `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore --verbosity minimal`
- 完成后跑项目级顺序 build/test

## 进度记录

- [x] 建立 active plan
- [x] 实现 shared credentials/profile resolver
- [x] 补单元测试
- [x] 同步文档与 history
- [x] 运行验证并归档 completed plan

## 验证结果

- 2026-04-30：项目级顺序 build/test 通过。
- `Tau.Ai.Tests`：37 passed。
- `Tau.Agent.Tests`：2 passed。
- `Tau.Tui.Tests`：4 passed。
- `Tau.CodingAgent.Tests`：6 passed。
- `Tau.Pods.Tests`：7 passed。
- 所有项目级 build 均为 0 warnings / 0 errors。
