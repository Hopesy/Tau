param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-tools-to-bin-migration-" + [Guid]::NewGuid().ToString('N'))
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
        [switch]$Apply
    )

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-coding-agent-tools-to-bin.ps1'
    if ($Apply) {
        $output = & $scriptPath -AgentDirectory $AgentDirectory -Apply -Json 2>&1
    }
    else {
        $output = & $scriptPath -AgentDirectory $AgentDirectory -Json 2>&1
    }
    $outputText = ($output -join [Environment]::NewLine)

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-coding-agent-tools-to-bin.ps1 did not return valid JSON. Output: $outputText"
    }
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Get-BinaryResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Summary,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $matches = @($Summary.binaries | Where-Object { $_.name -eq $Name })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one migration result for $Name, actual $($matches.Count)."
    }

    return $matches[0]
}

try {
    $agentDir = Join-Path $tempRoot '.tau'
    $toolsDir = Join-Path $agentDir 'tools'
    $binDir = Join-Path $agentDir 'bin'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null

    Write-Utf8NoBomText -Path (Join-Path $toolsDir 'fd') -Text 'old fd'
    Write-Utf8NoBomText -Path (Join-Path $toolsDir 'rg') -Text 'old rg'
    Write-Utf8NoBomText -Path (Join-Path $toolsDir 'fd.exe') -Text 'old fd exe'
    Write-Utf8NoBomText -Path (Join-Path $toolsDir 'custom-tool') -Text 'custom remains'
    Write-Utf8NoBomText -Path (Join-Path $binDir 'rg') -Text 'new rg'
    New-Item -ItemType Directory -Force -Path (Join-Path $toolsDir 'rg.exe') | Out-Null

    $dryRun = Invoke-Migration -AgentDirectory $agentDir
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run tools-to-bin migration to succeed.'
    Add-Assertion -Name 'dry-run managed binary count' -Passed ([int]$dryRun.scan.managedBinaryCount -eq 4) -Detail "Expected 4 managed binary names, actual $($dryRun.scan.managedBinaryCount)."
    Add-Assertion -Name 'dry-run migratable count' -Passed ([int]$dryRun.scan.migratable -eq 3) -Detail "Expected 3 migratable managed binaries, actual $($dryRun.scan.migratable)."
    Add-Assertion -Name 'dry-run would move count' -Passed ([int]$dryRun.scan.wouldMove -eq 2) -Detail "Expected 2 would-move results, actual $($dryRun.scan.wouldMove)."
    Add-Assertion -Name 'dry-run would remove duplicate count' -Passed ([int]$dryRun.scan.wouldRemoveOld -eq 1) -Detail "Expected 1 would-remove-old result, actual $($dryRun.scan.wouldRemoveOld)."
    Add-Assertion -Name 'dry-run skipped count' -Passed ([int]$dryRun.scan.skipped -eq 1) -Detail "Expected 1 skipped result, actual $($dryRun.scan.skipped)."
    Add-Assertion -Name 'dry-run failed count' -Passed ([int]$dryRun.scan.failed -eq 0) -Detail "Expected 0 failed results, actual $($dryRun.scan.failed)."

    $dryFd = Get-BinaryResult -Summary $dryRun -Name 'fd'
    $dryRg = Get-BinaryResult -Summary $dryRun -Name 'rg'
    $dryFdExe = Get-BinaryResult -Summary $dryRun -Name 'fd.exe'
    $dryRgExe = Get-BinaryResult -Summary $dryRun -Name 'rg.exe'
    Add-Assertion -Name 'dry-run marks fd would-move' -Passed ($dryFd.action -eq 'would-move' -and $dryFd.sourceKind -eq 'file') -Detail 'Expected fd to be marked would-move.'
    Add-Assertion -Name 'dry-run marks rg duplicate removal' -Passed ($dryRg.action -eq 'would-remove-old' -and $dryRg.reason -eq 'target-exists') -Detail 'Expected rg duplicate to be marked for old source removal.'
    Add-Assertion -Name 'dry-run marks fd.exe would-move' -Passed ($dryFdExe.action -eq 'would-move') -Detail 'Expected fd.exe to be marked would-move.'
    Add-Assertion -Name 'dry-run skips directory source' -Passed ($dryRgExe.action -eq 'skipped' -and $dryRgExe.reason -eq 'source-not-file') -Detail 'Expected managed binary directory source to be skipped.'
    Add-Assertion -Name 'dry-run does not move source' -Passed (
        (Test-Path -LiteralPath (Join-Path $toolsDir 'fd') -PathType Leaf) -and
        (-not (Test-Path -LiteralPath (Join-Path $binDir 'fd')))
    ) -Detail 'Dry-run unexpectedly moved fd.'
    Add-Assertion -Name 'remaining gaps preserve custom tools scope' -Passed ((@($dryRun.remainingGaps) -join "`n") -match 'custom tool migration') -Detail 'Expected remaining gaps to keep custom tool migration open.'

    $applied = Invoke-Migration -AgentDirectory $agentDir -Apply
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply tools-to-bin migration to succeed.'
    Add-Assertion -Name 'apply moved count' -Passed ([int]$applied.scan.moved -eq 2) -Detail "Expected apply to move 2 binaries, actual $($applied.scan.moved)."
    Add-Assertion -Name 'apply removed duplicate count' -Passed ([int]$applied.scan.removedOld -eq 1) -Detail "Expected apply to remove 1 old duplicate, actual $($applied.scan.removedOld)."
    Add-Assertion -Name 'apply skipped count' -Passed ([int]$applied.scan.skipped -eq 1) -Detail "Expected apply to skip 1 item, actual $($applied.scan.skipped)."
    Add-Assertion -Name 'apply failed count' -Passed ([int]$applied.scan.failed -eq 0) -Detail "Expected apply to have 0 failures, actual $($applied.scan.failed)."

    Add-Assertion -Name 'apply moves fd to bin' -Passed (
        (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'fd'))) -and
        ((Get-Content -LiteralPath (Join-Path $binDir 'fd') -Raw) -eq 'old fd')
    ) -Detail 'Expected fd to be moved from tools to bin with content preserved.'
    Add-Assertion -Name 'apply preserves existing bin duplicate and removes old source' -Passed (
        (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'rg'))) -and
        ((Get-Content -LiteralPath (Join-Path $binDir 'rg') -Raw) -eq 'new rg')
    ) -Detail 'Expected existing bin rg to remain while old tools rg is removed.'
    Add-Assertion -Name 'apply moves fd.exe to bin' -Passed (
        (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'fd.exe'))) -and
        ((Get-Content -LiteralPath (Join-Path $binDir 'fd.exe') -Raw) -eq 'old fd exe')
    ) -Detail 'Expected fd.exe to be moved from tools to bin.'
    Add-Assertion -Name 'apply preserves custom tool' -Passed (Test-Path -LiteralPath (Join-Path $toolsDir 'custom-tool') -PathType Leaf) -Detail 'Expected custom tool file to remain untouched.'
    Add-Assertion -Name 'apply preserves directory source' -Passed (Test-Path -LiteralPath (Join-Path $toolsDir 'rg.exe') -PathType Container) -Detail 'Expected directory named rg.exe to remain untouched.'

    $idempotent = Invoke-Migration -AgentDirectory $agentDir -Apply
    Add-Assertion -Name 'idempotent apply' -Passed (
        [int]$idempotent.scan.moved -eq 0 -and
        [int]$idempotent.scan.removedOld -eq 0 -and
        [int]$idempotent.scan.skipped -eq 4 -and
        [int]$idempotent.scan.failed -eq 0
    ) -Detail 'Expected second apply to migrate nothing and skip all managed binary names.'

    $missingAgent = Join-Path $tempRoot 'missing-agent'
    $missingScan = Invoke-Migration -AgentDirectory $missingAgent
    Add-Assertion -Name 'missing agent directory skip' -Passed (
        [int]$missingScan.scan.migratable -eq 0 -and
        [int]$missingScan.scan.skipped -eq 4 -and
        ($missingScan.binaries | Select-Object -First 1).reason -eq 'tools-directory-missing'
    ) -Detail 'Expected missing tools directory to skip all managed binary names.'

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
        Write-Host 'Tau CodingAgent tools-to-bin migration smoke passed'
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
        Write-Host 'Tau CodingAgent tools-to-bin migration smoke failed'
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
