param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-auth-migration-" + [Guid]::NewGuid().ToString('N'))
$scriptSucceeded = $false

function Add-Assertion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [bool]$Passed,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $script:assertions += [ordered]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }

    if (-not $Passed) {
        throw $Detail
    }
}

function Invoke-Migration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AgentDirectory,
        [string]$AuthPath = '',
        [string]$OAuthPath = '',
        [string]$SettingsPath = '',
        [switch]$Apply
    )

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-coding-agent-auth.ps1'
    $parameters = @{
        AgentDirectory = $AgentDirectory
        Json = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($AuthPath)) {
        $parameters.AuthPath = $AuthPath
    }
    if (-not [string]::IsNullOrWhiteSpace($OAuthPath)) {
        $parameters.OAuthPath = $OAuthPath
    }
    if (-not [string]::IsNullOrWhiteSpace($SettingsPath)) {
        $parameters.SettingsPath = $SettingsPath
    }
    if ($Apply) {
        $parameters.Apply = $true
    }

    $output = & $scriptPath @parameters 2>&1
    $outputText = ($output -join [Environment]::NewLine)

    try {
        $summary = $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-coding-agent-auth.ps1 did not return valid JSON. Output: $outputText"
    }

    return [pscustomobject]@{
        Summary = $summary
        OutputText = $outputText
    }
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

try {
    $agentDir = Join-Path $tempRoot '.tau'
    New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
    $oauthPath = Join-Path $agentDir 'oauth.json'
    $settingsPath = Join-Path $agentDir 'settings.json'
    $authPath = Join-Path $agentDir 'auth.json'

    $oauthJson = @'
{
  "openai-codex": {
    "refresh": "refresh-secret",
    "access": "access-secret",
    "expiresAt": "2030-01-01T00:00:00Z",
    "accountId": "acct-1"
  }
}
'@
    $settingsJson = @'
{
  "defaultProvider": "anthropic",
  "apiKeys": {
    "anthropic": "settings-key-secret",
    "openai-codex": "shadow-key-secret"
  },
  "theme": "dark"
}
'@
    Write-Utf8NoBomText -Path $oauthPath -Text $oauthJson
    Write-Utf8NoBomText -Path $settingsPath -Text $settingsJson

    $dryRunResult = Invoke-Migration -AgentDirectory $agentDir
    $dryRun = $dryRunResult.Summary
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run auth migration to succeed.'
    Add-Assertion -Name 'dry-run provider count' -Passed ([int]$dryRun.scan.providerCount -eq 2) -Detail "Expected two providers to migrate, actual $($dryRun.scan.providerCount)."
    Add-Assertion -Name 'dry-run oauth count' -Passed ([int]$dryRun.scan.oauthProviderCount -eq 1) -Detail "Expected one oauth provider, actual $($dryRun.scan.oauthProviderCount)."
    Add-Assertion -Name 'dry-run api key count' -Passed ([int]$dryRun.scan.apiKeyProviderCount -eq 1) -Detail "Expected one api key provider, actual $($dryRun.scan.apiKeyProviderCount)."
    Add-Assertion -Name 'dry-run oauth wins conflict' -Passed (
        @($dryRun.settings.skippedProviders | Where-Object {
            $_.name -eq 'openai-codex' -and $_.reason -eq 'oauth-wins'
        }).Count -eq 1
    ) -Detail 'Expected settings apiKeys provider to be skipped when OAuth credentials already exist.'
    Add-Assertion -Name 'dry-run does not write auth' -Passed (-not (Test-Path -LiteralPath $authPath)) -Detail 'Dry-run unexpectedly created auth.json.'
    Add-Assertion -Name 'dry-run does not rename oauth' -Passed ((Test-Path -LiteralPath $oauthPath) -and -not (Test-Path -LiteralPath "$oauthPath.migrated")) -Detail 'Dry-run unexpectedly renamed oauth.json.'
    Add-Assertion -Name 'dry-run keeps settings untouched' -Passed ((Get-Content -LiteralPath $settingsPath -Raw) -eq $settingsJson) -Detail 'Dry-run unexpectedly changed settings.json.'
    Add-Assertion -Name 'json output redacts secrets' -Passed (
        $dryRunResult.OutputText -notmatch 'refresh-secret|access-secret|settings-key-secret|shadow-key-secret'
    ) -Detail 'Expected migration JSON output to avoid credential values.'
    Add-Assertion -Name 'remaining gaps keep full auth parity open' -Passed ((@($dryRun.remainingGaps) -join "`n") -match 'real OAuth e2e') -Detail 'Expected remaining gaps to keep full OAuth e2e open.'

    $appliedResult = Invoke-Migration -AgentDirectory $agentDir -Apply
    $applied = $appliedResult.Summary
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply auth migration to succeed.'
    Add-Assertion -Name 'apply writes auth' -Passed ($applied.auth.action -eq 'written' -and (Test-Path -LiteralPath $authPath)) -Detail 'Expected apply to write auth.json.'
    Add-Assertion -Name 'apply renames oauth' -Passed ($applied.oauth.action -eq 'migrated' -and -not (Test-Path -LiteralPath $oauthPath) -and (Test-Path -LiteralPath "$oauthPath.migrated")) -Detail 'Expected apply to rename oauth.json to oauth.json.migrated.'
    Add-Assertion -Name 'apply updates settings' -Passed ($applied.settings.action -eq 'updated') -Detail 'Expected apply to update settings.json.'

    $auth = Read-JsonFile -Path $authPath
    $settings = Read-JsonFile -Path $settingsPath
    Add-Assertion -Name 'auth preserves oauth credential' -Passed (
        $auth.'openai-codex'.type -eq 'oauth' -and
        $auth.'openai-codex'.refresh -eq 'refresh-secret' -and
        $auth.'openai-codex'.access -eq 'access-secret' -and
        $auth.'openai-codex'.accountId -eq 'acct-1'
    ) -Detail 'Expected openai-codex OAuth credential to be written to auth.json.'
    Add-Assertion -Name 'auth writes api key credential' -Passed (
        $auth.anthropic.type -eq 'api_key' -and
        $auth.anthropic.key -eq 'settings-key-secret'
    ) -Detail 'Expected settings api key to be written to auth.json.'
    Add-Assertion -Name 'settings removes apiKeys' -Passed (-not ($settings.PSObject.Properties.Name -contains 'apiKeys')) -Detail 'Expected settings.json apiKeys to be removed.'
    Add-Assertion -Name 'settings preserves other properties' -Passed (
        $settings.defaultProvider -eq 'anthropic' -and
        $settings.theme -eq 'dark'
    ) -Detail 'Expected settings.json non-apiKeys properties to be preserved.'
    Add-Assertion -Name 'apply output redacts secrets' -Passed (
        $appliedResult.OutputText -notmatch 'refresh-secret|access-secret|settings-key-secret|shadow-key-secret'
    ) -Detail 'Expected apply JSON output to avoid credential values.'

    $idempotent = (Invoke-Migration -AgentDirectory $agentDir -Apply).Summary
    Add-Assertion -Name 'idempotent apply skips when auth exists' -Passed (
        $idempotent.auth.action -eq 'skipped' -and
        $idempotent.auth.reason -eq 'auth-exists' -and
        $idempotent.oauth.reason -eq 'auth-exists' -and
        $idempotent.settings.reason -eq 'auth-exists'
    ) -Detail 'Expected second apply to skip all legacy files when auth.json exists.'

    $existingAuthDir = Join-Path $tempRoot 'existing-auth\.tau'
    New-Item -ItemType Directory -Force -Path $existingAuthDir | Out-Null
    $existingAuthPath = Join-Path $existingAuthDir 'auth.json'
    $existingOauthPath = Join-Path $existingAuthDir 'oauth.json'
    $existingSettingsPath = Join-Path $existingAuthDir 'settings.json'
    Write-Utf8NoBomText -Path $existingAuthPath -Text '{ "existing": { "type": "api_key", "key": "keep-existing" } }'
    Write-Utf8NoBomText -Path $existingOauthPath -Text $oauthJson
    Write-Utf8NoBomText -Path $existingSettingsPath -Text $settingsJson
    $existing = (Invoke-Migration -AgentDirectory $existingAuthDir -Apply).Summary
    Add-Assertion -Name 'existing auth skips legacy files' -Passed (
        $existing.auth.reason -eq 'auth-exists' -and
        (Test-Path -LiteralPath $existingOauthPath) -and
        (-not (Test-Path -LiteralPath "$existingOauthPath.migrated")) -and
        ((Get-Content -LiteralPath $existingSettingsPath -Raw) -eq $settingsJson)
    ) -Detail 'Expected existing auth.json to prevent oauth/settings mutation.'
    $existingAuth = Read-JsonFile -Path $existingAuthPath
    Add-Assertion -Name 'existing auth preserved' -Passed ($existingAuth.existing.key -eq 'keep-existing') -Detail 'Expected pre-existing auth.json to remain unchanged.'

    $invalidOauthDir = Join-Path $tempRoot 'invalid-oauth\.tau'
    New-Item -ItemType Directory -Force -Path $invalidOauthDir | Out-Null
    Write-Utf8NoBomText -Path (Join-Path $invalidOauthDir 'oauth.json') -Text '{not json'
    Write-Utf8NoBomText -Path (Join-Path $invalidOauthDir 'settings.json') -Text '{ "apiKeys": { "mistral": "mistral-key-secret" }, "theme": "light" }'
    $invalidOauth = (Invoke-Migration -AgentDirectory $invalidOauthDir -Apply).Summary
    $invalidOauthAuth = Read-JsonFile -Path (Join-Path $invalidOauthDir 'auth.json')
    $invalidOauthSettings = Read-JsonFile -Path (Join-Path $invalidOauthDir 'settings.json')
    Add-Assertion -Name 'invalid oauth does not block settings' -Passed (
        $invalidOauth.oauth.reason -eq 'invalid-json' -and
        $invalidOauthAuth.mistral.type -eq 'api_key' -and
        $invalidOauthAuth.mistral.key -eq 'mistral-key-secret' -and
        -not ($invalidOauthSettings.PSObject.Properties.Name -contains 'apiKeys')
    ) -Detail 'Expected invalid oauth.json to be skipped while settings apiKeys migrate.'

    $invalidSettingsDir = Join-Path $tempRoot 'invalid-settings\.tau'
    New-Item -ItemType Directory -Force -Path $invalidSettingsDir | Out-Null
    Write-Utf8NoBomText -Path (Join-Path $invalidSettingsDir 'oauth.json') -Text '{ "gemini": { "refresh": "gemini-refresh", "access": "gemini-access" } }'
    Write-Utf8NoBomText -Path (Join-Path $invalidSettingsDir 'settings.json') -Text '{not json'
    $invalidSettings = (Invoke-Migration -AgentDirectory $invalidSettingsDir -Apply).Summary
    $invalidSettingsAuth = Read-JsonFile -Path (Join-Path $invalidSettingsDir 'auth.json')
    Add-Assertion -Name 'invalid settings does not block oauth' -Passed (
        $invalidSettings.settings.reason -eq 'invalid-json' -and
        $invalidSettings.oauth.action -eq 'migrated' -and
        $invalidSettingsAuth.gemini.type -eq 'oauth' -and
        $invalidSettingsAuth.gemini.refresh -eq 'gemini-refresh' -and
        (Test-Path -LiteralPath (Join-Path $invalidSettingsDir 'oauth.json.migrated')) -and
        ((Get-Content -LiteralPath (Join-Path $invalidSettingsDir 'settings.json') -Raw) -eq '{not json')
    ) -Detail 'Expected invalid settings.json to be skipped while oauth.json migrates.'

    $missingAgent = Join-Path $tempRoot 'missing-agent'
    $missing = (Invoke-Migration -AgentDirectory $missingAgent).Summary
    Add-Assertion -Name 'missing files skipped' -Passed (
        $missing.succeeded -eq $true -and
        [int]$missing.scan.providerCount -eq 0 -and
        $missing.oauth.reason -eq 'file-missing' -and
        $missing.settings.reason -eq 'file-missing' -and
        $missing.auth.reason -eq 'no-credentials'
    ) -Detail 'Expected missing legacy files to be skipped without failure.'

    $scriptSucceeded = $true
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        tempRoot = $tempRoot
        assertions = $script:assertions
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau CodingAgent auth migration smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  temp root: $tempRoot"
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        tempRoot = $tempRoot
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau CodingAgent auth migration smoke failed'
        Write-Host $_.Exception.Message
        Write-Host "temp root: $tempRoot"
    }

    exit 1
}
finally {
    if ($scriptSucceeded -and -not $KeepTemp) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
