# Tau AgentCore examples

这些示例验证 `Tau.AgentCore` 可以作为普通 .NET Agent 应用底座使用，而不是只能通过 `Tau.CodingAgent` 产品宿主间接使用。

## Console example

```powershell
dotnet run --project examples\Tau.AgentCore.ConsoleExample\Tau.AgentCore.ConsoleExample.csproj
```

该示例使用 `Tau.Ai.Providers.Faux`、`AgentApplication`、delegate tool、`InMemoryAgentSessionStore` 和一个本地 log sink，完成一次 `prompt -> tool call -> tool result -> final assistant` 回合，并输出 run result、session 保存状态和 runtime log 摘要。

## HTTP example

```powershell
dotnet run --project examples\Tau.AgentCore.HttpExample\Tau.AgentCore.HttpExample.csproj -- --urls http://127.0.0.1:5099
```

运行后可调用：

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:5099/agent -Method Post -ContentType 'application/json' -Body '{"prompt":"hello","sessionId":"demo"}'
```

该示例为每个请求创建一个 Faux provider-backed Agent app，复用同一个 `InMemoryAgentSessionStore`，返回 assistant text、message count、tool event count、stop reason 和 correlation id。

## Smoke

```powershell
dotnet run --project examples\Tau.AgentCore.ConsoleExample\Tau.AgentCore.ConsoleExample.csproj
```
