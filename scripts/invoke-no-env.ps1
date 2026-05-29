param(
    [string]$FilePath = '',
    [string[]]$Arguments = @(),
    [string]$ArgumentListJson = '',
    [string]$ArgumentListBase64 = '',
    [string]$WorkingDirectory = '',
    [string]$TauStateRoot = '',
    [string]$InputFile = '',
    [int]$TimeoutSeconds = 0,
    [string[]]$Keep = @(),
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function ConvertFrom-ArgumentListJson {
    param(
        [string]$Json,
        [string]$Base64
    )

    if (-not [string]::IsNullOrWhiteSpace($Base64)) {
        $bytes = [System.Convert]::FromBase64String($Base64)
        $Json = [System.Text.Encoding]::UTF8.GetString($bytes)
    }

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return $null
    }

    $items = $Json | ConvertFrom-Json
    if ($null -eq $items) {
        return @()
    }

    if ($items -isnot [System.Array]) {
        throw 'ArgumentListJson must be a JSON array of strings.'
    }

    $result = @()
    foreach ($item in $items) {
        if ($null -eq $item) {
            $result += ''
        }
        else {
            $result += [string]$item
        }
    }

    return $result
}

function ConvertTo-ProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function ConvertTo-CmdArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"&<>|^]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Get-SensitiveEnvironmentNames {
    return @(
        'AI_GATEWAY_API_KEY',
        'ANTHROPIC_API_KEY',
        'ANTHROPIC_BASE_URL',
        'ANTHROPIC_OAUTH_TOKEN',
        'AWS_ACCESS_KEY_ID',
        'AWS_BEARER_TOKEN_BEDROCK',
        'AWS_CONFIG_FILE',
        'AWS_CONTAINER_CREDENTIALS_FULL_URI',
        'AWS_CONTAINER_CREDENTIALS_RELATIVE_URI',
        'AWS_DEFAULT_REGION',
        'AWS_ENDPOINT_URL_SSO_OIDC',
        'AWS_PROFILE',
        'AWS_REGION',
        'AWS_ROLE_ARN',
        'AWS_SECRET_ACCESS_KEY',
        'AWS_SESSION_TOKEN',
        'AWS_SHARED_CREDENTIALS_FILE',
        'AWS_WEB_IDENTITY_TOKEN_FILE',
        'AZURE_OPENAI_API_KEY',
        'AZURE_OPENAI_API_VERSION',
        'AZURE_OPENAI_BASE_URL',
        'AZURE_OPENAI_DEPLOYMENT_NAME_MAP',
        'AZURE_OPENAI_RESOURCE_NAME',
        'BEDROCK_EXTENSIVE_MODEL_TEST',
        'CEREBRAS_API_KEY',
        'COPILOT_GITHUB_TOKEN',
        'GCLOUD_PROJECT',
        'GEMINI_API_KEY',
        'GH_TOKEN',
        'GITHUB_TOKEN',
        'GOOGLE_API_KEY',
        'GOOGLE_APPLICATION_CREDENTIALS',
        'GOOGLE_CLOUD_LOCATION',
        'GOOGLE_CLOUD_PROJECT',
        'GOOGLE_GENAI_USE_VERTEXAI',
        'GOOGLE_VERTEX_LOCATION',
        'GOOGLE_VERTEX_PROJECT',
        'GROQ_API_KEY',
        'HF_TOKEN',
        'HUGGINGFACE_HUB_TOKEN',
        'KIMI_API_KEY',
        'MINIMAX_API_KEY',
        'MINIMAX_CN_API_KEY',
        'MISTRAL_API_KEY',
        'OPENCODE_API_KEY',
        'OPENAI_API_BASE',
        'OPENAI_API_KEY',
        'OPENAI_BASE_URL',
        'OPENAI_ORG_ID',
        'OPENAI_ORGANIZATION',
        'OPENAI_PROJECT',
        'OPENROUTER_API_KEY',
        'PI_API_KEY',
        'SLACK_APP_TOKEN',
        'SLACK_BOT_TOKEN',
        'TAU_AUTH_FILE',
        'TAU_CODING_AGENT_CHANGELOG_FILE',
        'TAU_CODING_AGENT_EXTENSION_PATHS',
        'TAU_CODING_AGENT_HISTORY_FILE',
        'TAU_CODING_AGENT_KEYBINDINGS_FILE',
        'TAU_CODING_AGENT_PROMPT_PATHS',
        'TAU_CODING_AGENT_SESSION_FILE',
        'TAU_CODING_AGENT_SETTINGS_FILE',
        'TAU_CODING_AGENT_SKILL_PATHS',
        'TAU_CODING_AGENT_THEME_PATHS',
        'TAU_CODING_AGENT_TREE_SESSION_FILE',
        'TAU_LOG_DISABLED',
        'TAU_LOG_FILE',
        'TAU_MODEL',
        'TAU_MODELS_FILE',
        'TAU_PROVIDER',
        'TAU_SHARE_VIEWER_URL',
        'VERTEX_LOCATION',
        'VERTEX_PROJECT_ID',
        'VERCEL_AI_GATEWAY_API_KEY',
        'XAI_API_KEY',
        'ZAI_API_KEY'
    )
}

function Get-TauStateEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    return [ordered]@{
        TAU_AUTH_FILE = Join-Path $fullRoot 'auth.json'
        TAU_MODELS_FILE = Join-Path $fullRoot 'models.json'
        TAU_LOG_FILE = Join-Path $fullRoot 'tau-runtime-log.jsonl'
        TAU_CODING_AGENT_SESSION_FILE = Join-Path $fullRoot 'coding-agent-session.json'
        TAU_CODING_AGENT_TREE_SESSION_FILE = Join-Path $fullRoot 'coding-agent-session.jsonl'
        TAU_CODING_AGENT_SETTINGS_FILE = Join-Path $fullRoot 'coding-agent-settings.json'
        TAU_CODING_AGENT_HISTORY_FILE = Join-Path $fullRoot 'coding-agent-history'
        TAU_CODING_AGENT_KEYBINDINGS_FILE = Join-Path $fullRoot 'coding-agent-keybindings.json'
    }
}

function Initialize-TauStateRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $envMap = Get-TauStateEnvironment -Root $Root
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetFullPath($Root)) | Out-Null
    [System.IO.File]::WriteAllText($envMap.TAU_AUTH_FILE, '{}' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($envMap.TAU_MODELS_FILE, '{"providers":{}}' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    return $envMap
}

$jsonArguments = ConvertFrom-ArgumentListJson -Json $ArgumentListJson -Base64 $ArgumentListBase64
$processArguments = if ($null -eq $jsonArguments) { @($Arguments) } else { @($jsonArguments) }

if ([string]::IsNullOrWhiteSpace($FilePath) -and -not $DryRun) {
    throw 'No command was provided. Use: invoke-no-env.ps1 -FilePath <command> -Arguments <args>'
}

$keepSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $Keep) {
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        [void]$keepSet.Add($name.Trim())
    }
}

$sensitiveNames = Get-SensitiveEnvironmentNames
$presentRemovedNames = @()

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $false
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false

$workingDir = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    (Get-Location).ProviderPath
}
else {
    [System.IO.Path]::GetFullPath($WorkingDirectory)
}
$psi.WorkingDirectory = $workingDir

foreach ($name in $sensitiveNames) {
    if ($keepSet.Contains($name)) {
        continue
    }

    if ($psi.EnvironmentVariables.ContainsKey($name)) {
        $presentRemovedNames += $name
        $psi.EnvironmentVariables.Remove($name) | Out-Null
    }
}

$envToSet = [ordered]@{
    PI_NO_LOCAL_LLM = '1'
    TAU_NO_LOCAL_LLM = '1'
}

if (-not [string]::IsNullOrWhiteSpace($TauStateRoot)) {
    $tauStateEnvironment = if ($DryRun) {
        Get-TauStateEnvironment -Root $TauStateRoot
    }
    else {
        Initialize-TauStateRoot -Root $TauStateRoot
    }

    foreach ($entry in $tauStateEnvironment.GetEnumerator()) {
        $envToSet[$entry.Key] = $entry.Value
    }
}

foreach ($entry in $envToSet.GetEnumerator()) {
    $psi.EnvironmentVariables[$entry.Key] = [string]$entry.Value
}

if ($DryRun) {
    $removedText = if ($presentRemovedNames.Count -eq 0) { '<none currently set>' } else { ($presentRemovedNames | Sort-Object) -join ', ' }
    $setText = ($envToSet.Keys | Sort-Object) -join ', '
    Write-Host "no-env dry run"
    Write-Host "workingDirectory: $workingDir"
    Write-Host "removed env names: $removedText"
    Write-Host "set env names: $setText"
    if (-not [string]::IsNullOrWhiteSpace($FilePath)) {
        Write-Host "command: $FilePath $($processArguments -join ' ')"
    }
    exit 0
}

$hasInputFile = -not [string]::IsNullOrWhiteSpace($InputFile)
$inputFileFull = ''
if ($hasInputFile) {
    $inputFileFull = [System.IO.Path]::GetFullPath($InputFile)
}

if ($hasInputFile -and -not (Test-Path -LiteralPath $inputFileFull)) {
    throw "Input file not found: $InputFile"
}

if ($hasInputFile) {
    $psi.FileName = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $commandLine = '"' + $FilePath + '"'
    if ($processArguments.Count -gt 0) {
        $commandLine += ' ' + (($processArguments | ForEach-Object { ConvertTo-CmdArgument $_ }) -join ' ')
    }
    $commandLine += ' < "' + $inputFileFull + '"'
    $psi.Arguments = '/d /s /c "' + $commandLine + '"'
}
elseif ([System.IO.Path]::GetExtension($FilePath).Equals('.cmd', [StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetExtension($FilePath).Equals('.bat', [StringComparison]::OrdinalIgnoreCase)) {
    $psi.FileName = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $commandLine = '"' + $FilePath + '"'
    if ($processArguments.Count -gt 0) {
        $commandLine += ' ' + (($processArguments | ForEach-Object { ConvertTo-CmdArgument $_ }) -join ' ')
    }
    $psi.Arguments = '/d /s /c "' + $commandLine + '"'
}
else {
    $psi.FileName = $FilePath
    $psi.Arguments = ($processArguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi
if (-not $process.Start()) {
    throw "Failed to start process: $FilePath"
}

if ($TimeoutSeconds -gt 0) {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "Timed out running $FilePath $($processArguments -join ' ')"
    }
}
else {
    $process.WaitForExit()
}

exit $process.ExitCode
