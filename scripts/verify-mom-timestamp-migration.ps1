param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-mom-timestamp-migration-" + [Guid]::NewGuid().ToString('N'))
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

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-mom-timestamps.ps1'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "migrate-mom-timestamps.ps1 failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-mom-timestamps.ps1 did not return valid JSON. Output: $outputText"
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

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $dataRoot = Join-Path $tempRoot 'mom-data'
    $channelOne = Join-Path $dataRoot 'C123OPS'
    $channelTwo = Join-Path $dataRoot 'C456DEV'
    New-Item -ItemType Directory -Force -Path $channelOne, $channelTwo, (Join-Path $dataRoot 'NOLOG') | Out-Null

    $logOne = Join-Path $channelOne 'log.jsonl'
    $logTwo = Join-Path $channelTwo 'log.jsonl'
    Write-Utf8NoBomLines -Path $logOne -Lines @(
        '{"date":"2026-05-29T10:00:00Z","ts":"1764279320398","user":"U1","text":"millis","attachments":[],"isBot":false}',
        '{"date":"2026-05-29T10:01:00Z","ts":"1764279530.533489","user":"U2","text":"slack","attachments":[],"isBot":false}',
        '{"date":"2026-05-29T10:02:00Z","ts":1764279320123,"user":"U3","text":"numeric millis","attachments":[],"isBot":false}',
        '{"date":"2026-05-29T10:03:00Z","ts":"1764279320398-bot","user":"bot","text":"bot suffix","attachments":[],"isBot":true}',
        'not-json'
    )
    Write-Utf8NoBomLines -Path $logTwo -Lines @(
        '{"date":"2026-05-29T11:00:00Z","ts":"1764279600.000001","user":"U4","text":"already slack","attachments":[],"isBot":false}'
    )

    $dryRun = Invoke-Migration -Arguments @('-DataDirectory', $dataRoot, '-Json')
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run migration to succeed.'
    Add-Assertion -Name 'dry-run file count' -Passed ([int]$dryRun.scan.logFileCount -eq 2) -Detail "Expected 2 log files, actual $($dryRun.scan.logFileCount)."
    Add-Assertion -Name 'dry-run migrated count' -Passed ([int]$dryRun.scan.migrated -eq 2) -Detail "Expected 2 dry-run migrations, actual $($dryRun.scan.migrated)."
    Add-Assertion -Name 'dry-run malformed count' -Passed ([int]$dryRun.scan.malformed -eq 1) -Detail "Expected 1 malformed line, actual $($dryRun.scan.malformed)."
    Add-Assertion -Name 'dry-run does not write' -Passed ((Get-Content -LiteralPath $logOne -Raw) -match '1764279320398') -Detail 'Dry-run unexpectedly rewrote log.jsonl.'

    $applied = Invoke-Migration -Arguments @('-DataDirectory', $dataRoot, '-Apply', '-Json')
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply migration to succeed.'
    Add-Assertion -Name 'apply migrated count' -Passed ([int]$applied.scan.migrated -eq 2 -and [int]$applied.scan.updatedFileCount -eq 1) -Detail 'Expected apply to migrate 2 timestamps in one file.'

    $updatedText = Get-Content -LiteralPath $logOne -Raw
    Add-Assertion -Name 'string millis converted' -Passed ($updatedText -match '"ts":"1764279320.398000"') -Detail 'String millisecond timestamp was not converted to Slack format.'
    Add-Assertion -Name 'numeric millis converted' -Passed ($updatedText -match '"ts":"1764279320.123000"') -Detail 'Numeric millisecond timestamp was not converted to Slack format.'
    Add-Assertion -Name 'slack timestamp preserved' -Passed ($updatedText -match '"ts":"1764279530.533489"') -Detail 'Existing Slack timestamp changed unexpectedly.'
    Add-Assertion -Name 'bot suffix preserved' -Passed ($updatedText -match '"ts":"1764279320398-bot"') -Detail 'Bot suffix timestamp changed unexpectedly.'
    Add-Assertion -Name 'malformed line preserved' -Passed ($updatedText -match 'not-json') -Detail 'Malformed line was not preserved.'

    $idempotent = Invoke-Migration -Arguments @('-DataDirectory', $dataRoot, '-Apply', '-Json')
    Add-Assertion -Name 'idempotent apply' -Passed ([int]$idempotent.scan.migrated -eq 0 -and [int]$idempotent.scan.updatedFileCount -eq 0) -Detail 'Expected second apply to be idempotent.'

    $emptyDataRoot = Join-Path $tempRoot 'empty-mom-data'
    New-Item -ItemType Directory -Force -Path (Join-Path $emptyDataRoot 'NOLOG') | Out-Null
    $emptyScan = Invoke-Migration -Arguments @('-DataDirectory', $emptyDataRoot, '-Json')
    Add-Assertion -Name 'empty scan uses zero counts' -Passed (
        [int]$emptyScan.scan.logFileCount -eq 0 -and
        [int]$emptyScan.scan.totalLines -eq 0 -and
        [int]$emptyScan.scan.migrated -eq 0 -and
        [int]$emptyScan.scan.malformed -eq 0
    ) -Detail 'Expected empty Mom data directory scan counters to be numeric zero values.'

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
        Write-Host 'Tau Mom timestamp migration smoke passed'
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
        Write-Host 'Tau Mom timestamp migration smoke failed'
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
