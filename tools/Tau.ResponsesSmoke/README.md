# Responses 在线实测

作者：xxx

通过 Tau 的 `OpenAiResponsesProvider` 和 `Agent` 验证真实 Responses 服务。默认使用 `https://api.deepseek.com`、`deepseek-flash`；密钥仅从进程环境变量读取，不写入仓库、会话或报告。

从仓库根目录运行以下 PowerShell。交互输入不会显示密钥，也不会把密钥字面量写入命令历史：

```powershell
$smokeSecret = Read-Host 'API key' -AsSecureString
$smokeCredential = [System.Net.NetworkCredential]::new('', $smokeSecret)
try {
    $env:TAU_SMOKE_API_KEY = $smokeCredential.Password
    dotnet run --project tools/Tau.ResponsesSmoke -- --live
} finally {
    Remove-Item Env:TAU_SMOKE_API_KEY -ErrorAction SilentlyContinue
    $smokeCredential = $null
    $smokeSecret.Dispose()
}
```

可选环境变量：`TAU_SMOKE_BASE_URL`、`TAU_SMOKE_MODEL`。地址应为 HTTPS API 根地址，不包含 `/responses`、凭据或查询参数。`--live` 是必选参数，在线请求会产生供应商用量。

默认六个场景合计最多八次请求；每次最多 512 token，工具场景每次最多 1024 token。单次请求超时 45 秒，整轮 4 分钟，不自动重试。HTTP 重定向关闭，目的地址限定为指定同源服务。任一断言失败立即停止，进程返回 1。

| 场景 | 检查内容 |
|---|---|
| `text` | `StreamSimple` 关闭思考、正文、用量、响应 ID 和实际模型名 |
| `reasoning` | 思考增量与可回传签名 |
| `history` | 随机标记经完整上下文回传后可被读取 |
| `tool` | Agent 参数校验、工具执行、推理和工具结果回传、最终答案 |
| `limit` | 输出上限映射 `MaxTokens` 和原始停止原因 |
| `cancel` | 首个正文增量后取消，保留已接收内容及响应 ID |

可在命令末尾加场景名，仅运行该项，例如 `--live cancel`。工具仅返回本轮随机测试值，不执行代码或修改文件。输出只包含断言结果、停止原因和用量，不打印请求正文、模型思考文本或认证头。取消时若服务端尚未发送 usage，用量为空；这不表示没有产生费用。

2026-10-04 使用用户提供的服务完成验证：最终整轮 **8 次请求、6 个场景全部通过**。初次工具探测发现 DeepSeek 开启思考时拒绝强制 `tool_choice`（HTTP 400），因此工具场景采用 `auto`。服务端限制不会在 Provider 中被静默改写。

协议依据见 [DeepSeek Responses 文档](https://api-docs.deepseek.com/guides/responses_api/)：接口为无状态会话，需回传完整历史；SSE 以终态事件结束。真实取消发现的内容丢失、思考关闭映射错误及响应实际模型名缺失已修复；其余服务商仅完成离线回归，本工具不代表全部迁移验收。

## CodingAgent CLI 配置实测

`deepseek.models.example.json` 提供无密钥样例，使用独立 provider 名称 `deepseek-responses`，避免覆盖内置 DeepSeek 的其他协议配置。它通过 `TAU_SMOKE_API_KEY` 读取密钥，设置 512 token、45 秒超时和零重试。

在上方密钥读取示例的 `try` 内，将 `dotnet run` 那一行替换为以下命令，即可通过正式 CLI 测试；结束时仍执行原有的密钥清理：

```powershell
$smokePreviousModels = $env:TAU_MODELS_FILE
try {
    $env:TAU_MODELS_FILE = (Resolve-Path tools/Tau.ResponsesSmoke/deepseek.models.example.json).Path
    dotnet run --project src/Tau.CodingAgent -- --offline --no-session --no-tools --no-extensions --no-skills --no-prompt-templates --no-themes --no-context-files --provider deepseek-responses --model deepseek-flash --thinking off --system-prompt 'You are a test client.' --print 'Reply with exactly TAU_CLI_OK.'
} finally {
    $env:TAU_MODELS_FILE = $smokePreviousModels
}
```

将 `--print` 改成 `--mode json` 可检查 JSONL 事件输出。`--offline` 关闭启动检查等辅助网络行为，模型请求仍会发送。文本模式现在对齐 pi，只输出运行完成后的最终助手正文；需要流式消费时使用 JSON 模式。

2026-10-04 第二十一批的两次真实 CLI 请求通过：文本模式退出码 0、正文 `TAU_CLI_OK`；JSON 模式退出码 0、17 条合法事件、唯一 agent_end、正文 `TAU_CLI_JSON_OK`。这两次使用临时工作目录、无工具且不保存会话。

## CLI 多轮提示与会话恢复

第二十二批支持将多个位置参数依次作为独立提示执行，共享同一历史。stdin、`@file` 和图片只合并到首轮；文本模式只输出最后一轮助手正文，JSON 模式保留所有轮次事件。移除 `--no-session` 后，已提交消息会同步到会话文件；取消和异常也会补存运行器中的已提交内容。保存失败返回非零退出码。

在已设置 `TAU_SMOKE_API_KEY` 和 `TAU_MODELS_FILE` 的上述 `try` 内，可运行以下代码。先构建项目，再用两次 CLI 启动验证三轮请求；使用独立会话目录，便于检查 JSONL 结果：

```powershell
dotnet build src/Tau.CodingAgent --verbosity quiet
$smokeCli = (Resolve-Path src/Tau.CodingAgent/bin/Debug/net10.0/Tau.CodingAgent.dll).Path
$smokeSessionDirectory = Join-Path ([IO.Path]::GetTempPath()) ('tau-session-check-' + [guid]::NewGuid().ToString('N'))
$smokeMarker = 'TAU_SESSION_' + [guid]::NewGuid().ToString('N')
$smokeArguments = @('--offline', '--no-tools', '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-themes', '--no-context-files', '--thinking', 'off', '--system-prompt', 'You are a test client.', '--session-dir', $smokeSessionDirectory)
'' | dotnet $smokeCli @smokeArguments --provider deepseek-responses --model deepseek-flash --print "Remember this marker: $smokeMarker. Reply with exactly READY." 'Return only the marker I asked you to remember.'
'' | dotnet $smokeCli @smokeArguments --continue --mode json 'Return only the marker I asked you to remember earlier.'
Write-Host "Session directory: $smokeSessionDirectory"
```

第一条命令的正文应只包含随机标记；第二条命令不再指定 provider/model，由会话恢复模型，其 JSON 助手终值应包含同一标记。目录内应只有一个会话文件，包含三条用户消息、三条助手消息和一个系统基线。`--session <path.jsonl>` 可指定树会话文件，`--session <path.json>` 保留 Tau 平面格式兼容；`--no-session` 禁止保存及恢复。

2026-10-04 第二十二批实际验证：**3 次 DeepSeek Responses 请求、2 个 CLI 进程全部通过**，包含多轮顺序、最终文本、随机标记恢复、自动恢复 provider/model、会话 ID 一致、JSON 事件及无重复消息；临时文件检查未包含认证密钥。另用回环 Responses 服务完成 11 次请求，覆盖 JSON/JSONL 文件、stdin、多轮、目录继续、新会话及 `--no-session`。该批尚未验收默认会话生命周期、思考等级恢复和交互 TUI。

## 默认会话与思考等级恢复

第二十三批起，不指定会话路径时，每次 CLI 启动创建独立 JSONL，存放在用户 `.tau/sessions/--工作目录编码--/` 下；`--continue` 才会继续当前工作目录最近有消息的会话。共享 `--session-dir` 也会按文件头的 cwd 过滤，空白新会话不会遮住已有对话。旧固定路径文件不迁移、不覆盖，可通过 `--session` 显式打开；`TAU_CODING_AGENT_SESSION_FILE` 与 `TAU_CODING_AGENT_TREE_SESSION_FILE` 仍表示显式文件配置。

JSONL 使用 `thinking_level_change` 条目记录思考等级，平面 JSON 保存 `thinkingLevel`。恢复优先级为显式参数（包括模型范围等级）、会话记录、全局设置、`medium` 默认值，最后按模型能力限制。`off` 是独立有效值，缺少该字段的旧会话才回退到设置。RPC 和交互命令修改等级后立即同步树会话；SDK 的 `Save()` 同步等级和历史，保留其平面副本接口。

2026-10-04 第二十三批实际验证：**2 次 DeepSeek Responses 请求、2 个 CLI 进程通过**。首次明确关闭思考并保存随机标记；全局设置随后改为 `high`，第二次启动不传 `--thinking`、provider 或 model，恢复后仍关闭思考并正确返回标记。JSONL 仅有一条 `off` 变更记录，无思考内容，临时文件不含密钥。另完成 **15 次回环 Responses 请求**，检查实际请求中的 reasoning.effort、默认新建与继续的会话 ID 和历史隔离。交互 TUI 的真实终端验收、按模型独立保存思考偏好等仍未完成。

## 扩展会话状态恢复

第二十四批的 `session-metadata.example.js` 在每次 agent_start 中读取当前分支的计数，调用 `pi.appendEntry` 写入 JSON 状态，再保存名称和条目标签。自定义条目不进入模型上下文；`pi.sendMessage` 属于另一条消息链路。

在已设置 `TAU_SMOKE_API_KEY` 和 `TAU_MODELS_FILE` 的上述 `try` 内运行：

```powershell
dotnet build src/Tau.CodingAgent --verbosity quiet
$smokeCli = (Resolve-Path src/Tau.CodingAgent/bin/Debug/net10.0/Tau.CodingAgent.dll).Path
$smokeExtension = (Resolve-Path tools/Tau.ResponsesSmoke/session-metadata.example.js).Path
$smokeSessionDirectory = Join-Path ([IO.Path]::GetTempPath()) ('tau-extension-check-' + [guid]::NewGuid().ToString('N'))
$smokeSession = Join-Path $smokeSessionDirectory 'extension.jsonl'
$smokeArguments = @('--offline', '--no-tools', '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-themes', '--no-context-files', '--thinking', 'off', '--system-prompt', 'You are a test client.', '--extension', $smokeExtension, '--session', $smokeSession)
'' | dotnet $smokeCli @smokeArguments --provider deepseek-responses --model deepseek-flash --print 'Reply with exactly TAU_EXTENSION_FIRST_OK.'
'' | dotnet $smokeCli @smokeArguments --mode json 'Reply with exactly TAU_EXTENSION_SECOND_OK.'
Get-Content $smokeSession | ConvertFrom-Json | Where-Object { $_.type -in @('custom', 'session_info', 'label') } | ConvertTo-Json -Depth 10
Write-Host "Session file: $smokeSession"
```

会话应包含两个 `customType: tau-session-smoke` 条目，data.turn 分别为 1 和 2；最终名称为 `Extension session 2`，两条状态分别标记为 `extension-turn-1` 和 `extension-turn-2`。第二次启动从会话恢复 provider/model。使用 `--no-session` 时只保留进程内状态；旧平面 `.json` 不支持自定义条目及标签，应使用 `.jsonl`。

2026-10-04 最终一轮 **2 次 DeepSeek Responses 请求、2 个 CLI 进程通过**，元数据恢复及临时文件无密钥检查通过。此前一次单请求探测未满足精确正文断言，本批共发起 3 次在线请求。另用回环 Responses 服务检查两次实际请求，确认 `TAU_EXTENSION_STATE_ONLY` 和自定义类型均未发送给模型。22 项 Node 回归覆盖分支、重载、并发、UI 回调重入、切换会话和写入失败；该批不代表全部扩展 API 已对齐。
