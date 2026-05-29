param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-session-migration-" + [Guid]::NewGuid().ToString('N'))
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
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-coding-agent-sessions.ps1'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "migrate-coding-agent-sessions.ps1 failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-coding-agent-sessions.ps1 did not return valid JSON. Output: $outputText"
    }
}

function Write-Utf8NoBomLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    [System.IO.File]::WriteAllLines($Path, $Lines, [System.Text.UTF8Encoding]::new($false))
}

function New-SessionLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,
        [Parameter(Mandatory = $true)]
        [string]$Cwd,
        [string]$Text = 'hello'
    )

    $escapedCwd = $Cwd.Replace('\', '\\')
    return @(
        "{`"type`":`"session`",`"version`":3,`"id`":`"$Id`",`"timestamp`":`"2026-05-29T01:00:00Z`",`"cwd`":`"$escapedCwd`"}",
        "{`"type`":`"message`",`"id`":`"u1`",`"timestamp`":`"2026-05-29T01:01:00Z`",`"message`":{`"role`":`"user`",`"content`":[{`"type`":`"text`",`"text`":`"$Text`"}]}}"
    )
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $agentRoot = Join-Path $tempRoot 'agent'
    New-Item -ItemType Directory -Force -Path $agentRoot | Out-Null

    $rootSession = Join-Path $agentRoot 'root-session.jsonl'
    $windowsSession = Join-Path $agentRoot 'windows-session.jsonl'
    $badJson = Join-Path $agentRoot 'bad-json.jsonl'
    $noCwd = Join-Path $agentRoot 'no-cwd.jsonl'
    $notSession = Join-Path $agentRoot 'not-session.jsonl'
    $conflict = Join-Path $agentRoot 'conflict.jsonl'
    $nestedDirectory = Join-Path $agentRoot 'nested'
    New-Item -ItemType Directory -Force -Path $nestedDirectory | Out-Null
    $nestedSession = Join-Path $nestedDirectory 'nested.jsonl'

    Write-Utf8NoBomLines -Path $rootSession -Lines (New-SessionLines -Id 'root-session' -Cwd '/home/zhou/tau' -Text 'root session')
    Write-Utf8NoBomLines -Path $windowsSession -Lines (New-SessionLines -Id 'windows-session' -Cwd 'C:\Users\zhouh\Desktop\Tau' -Text 'windows session')
    Write-Utf8NoBomLines -Path $badJson -Lines @('not-json')
    Write-Utf8NoBomLines -Path $noCwd -Lines @(
        '{"type":"session","version":3,"id":"no-cwd","timestamp":"2026-05-29T01:00:00Z"}'
    )
    Write-Utf8NoBomLines -Path $notSession -Lines @(
        '{"type":"message","id":"not-session","cwd":"/tmp/not-session"}'
    )
    Write-Utf8NoBomLines -Path $conflict -Lines (New-SessionLines -Id 'conflict' -Cwd '/tmp/conflict' -Text 'conflict source')
    Write-Utf8NoBomLines -Path $nestedSession -Lines (New-SessionLines -Id 'nested' -Cwd '/tmp/nested' -Text 'nested source')

    $conflictTargetDirectory = Join-Path (Join-Path $agentRoot 'coding-agent-sessions') '--tmp-conflict--'
    New-Item -ItemType Directory -Force -Path $conflictTargetDirectory | Out-Null
    Write-Utf8NoBomLines -Path (Join-Path $conflictTargetDirectory 'conflict.jsonl') -Lines (New-SessionLines -Id 'conflict-target' -Cwd '/tmp/conflict' -Text 'existing target')

    $dryRun = Invoke-Migration -Arguments @('-AgentDirectory', $agentRoot, '-Json')
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run migration to succeed.'
    Add-Assertion -Name 'dry-run root file count' -Passed ([int]$dryRun.scan.sessionFileCount -eq 6) -Detail "Expected 6 direct root JSONL files, actual $($dryRun.scan.sessionFileCount)."
    Add-Assertion -Name 'dry-run migratable count' -Passed ([int]$dryRun.scan.migratable -eq 2) -Detail "Expected 2 migratable sessions, actual $($dryRun.scan.migratable)."
    Add-Assertion -Name 'dry-run migrated count' -Passed ([int]$dryRun.scan.migrated -eq 0) -Detail "Expected dry-run migrated count 0, actual $($dryRun.scan.migrated)."
    Add-Assertion -Name 'dry-run skipped count' -Passed ([int]$dryRun.scan.skipped -eq 4) -Detail "Expected 4 skipped sessions, actual $($dryRun.scan.skipped)."
    Add-Assertion -Name 'dry-run uses Tau target directory' -Passed ($dryRun.targetDirectoryName -eq 'coding-agent-sessions') -Detail "Expected Tau target directory name coding-agent-sessions, actual $($dryRun.targetDirectoryName)."
    Add-Assertion -Name 'dry-run encodes Windows cwd' -Passed ((@($dryRun.files) | Where-Object { $_.fileName -eq 'windows-session.jsonl' }).encodedCwd -eq '--C--Users-zhouh-Desktop-Tau--') -Detail 'Windows cwd was not encoded with upstream slash/colon replacement semantics.'
    Add-Assertion -Name 'dry-run does not move source' -Passed ((Test-Path -LiteralPath $rootSession -PathType Leaf) -and (Test-Path -LiteralPath $windowsSession -PathType Leaf)) -Detail 'Dry-run unexpectedly moved a source session file.'
    Add-Assertion -Name 'dry-run ignores nested jsonl' -Passed (@($dryRun.files | Where-Object { $_.fileName -eq 'nested.jsonl' }).Count -eq 0) -Detail 'Nested JSONL file was scanned, but only direct root files should be scanned.'

    $applied = Invoke-Migration -Arguments @('-AgentDirectory', $agentRoot, '-Apply', '-Json')
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply migration to succeed.'
    Add-Assertion -Name 'apply migrated count' -Passed ([int]$applied.scan.migrated -eq 2) -Detail "Expected apply to move 2 sessions, actual $($applied.scan.migrated)."
    Add-Assertion -Name 'apply skipped count' -Passed ([int]$applied.scan.skipped -eq 4) -Detail "Expected apply to skip 4 sessions, actual $($applied.scan.skipped)."

    $rootTarget = Join-Path (Join-Path (Join-Path $agentRoot 'coding-agent-sessions') '--home-zhou-tau--') 'root-session.jsonl'
    $windowsTarget = Join-Path (Join-Path (Join-Path $agentRoot 'coding-agent-sessions') '--C--Users-zhouh-Desktop-Tau--') 'windows-session.jsonl'
    Add-Assertion -Name 'root target exists' -Passed (Test-Path -LiteralPath $rootTarget -PathType Leaf) -Detail "Root session target was not written: $rootTarget"
    Add-Assertion -Name 'windows target exists' -Passed (Test-Path -LiteralPath $windowsTarget -PathType Leaf) -Detail "Windows session target was not written: $windowsTarget"
    Add-Assertion -Name 'sources removed after apply' -Passed ((-not (Test-Path -LiteralPath $rootSession)) -and (-not (Test-Path -LiteralPath $windowsSession))) -Detail 'Migrated source files still exist after apply.'
    Add-Assertion -Name 'target preserves content' -Passed ((Get-Content -LiteralPath $windowsTarget -Raw) -match 'windows session') -Detail 'Moved session content was not preserved.'
    Add-Assertion -Name 'skipped sources preserved' -Passed ((Test-Path -LiteralPath $badJson) -and (Test-Path -LiteralPath $noCwd) -and (Test-Path -LiteralPath $notSession) -and (Test-Path -LiteralPath $conflict)) -Detail 'Skipped source files should remain in place.'
    Add-Assertion -Name 'nested source preserved' -Passed (Test-Path -LiteralPath $nestedSession -PathType Leaf) -Detail 'Nested session file should not be moved.'

    $idempotent = Invoke-Migration -Arguments @('-AgentDirectory', $agentRoot, '-Apply', '-Json')
    Add-Assertion -Name 'idempotent apply' -Passed ([int]$idempotent.scan.migrated -eq 0 -and [int]$idempotent.scan.sessionFileCount -eq 4) -Detail 'Expected second apply to migrate nothing and only see skipped root files.'

    $emptyAgentRoot = Join-Path $tempRoot 'empty-agent'
    New-Item -ItemType Directory -Force -Path $emptyAgentRoot | Out-Null
    $emptyScan = Invoke-Migration -Arguments @('-AgentDirectory', $emptyAgentRoot, '-Json')
    Add-Assertion -Name 'empty scan uses zero counts' -Passed (
        [int]$emptyScan.scan.sessionFileCount -eq 0 -and
        [int]$emptyScan.scan.migratable -eq 0 -and
        [int]$emptyScan.scan.migrated -eq 0 -and
        [int]$emptyScan.scan.skipped -eq 0
    ) -Detail 'Expected empty CodingAgent agent directory scan counters to be numeric zero values.'

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
        Write-Host 'Tau CodingAgent session migration smoke passed'
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
        Write-Host 'Tau CodingAgent session migration smoke failed'
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
