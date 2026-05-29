param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-commands-migration-" + [Guid]::NewGuid().ToString('N'))
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
        [string[]]$BaseDirectories,
        [switch]$Apply
    )

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-coding-agent-commands.ps1'
    if ($Apply) {
        $output = & $scriptPath -BaseDirectory $BaseDirectories -Apply -Json 2>&1
    }
    else {
        $output = & $scriptPath -BaseDirectory $BaseDirectories -Json 2>&1
    }
    $outputText = ($output -join [Environment]::NewLine)

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-coding-agent-commands.ps1 did not return valid JSON. Output: $outputText"
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

function Get-DirectoryResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Summary,
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory
    )

    $fullPath = [System.IO.Path]::GetFullPath($BaseDirectory)
    $matches = @($Summary.directories | Where-Object { $_.baseDirectory -eq $fullPath })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one migration result for $fullPath, actual $($matches.Count)."
    }

    return $matches[0]
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

    $migratableBase = Join-Path $tempRoot 'migratable\.tau'
    $targetExistsBase = Join-Path $tempRoot 'target-exists\.tau'
    $noCommandsBase = Join-Path $tempRoot 'no-commands\.tau'
    $fileSourceBase = Join-Path $tempRoot 'file-source\.tau'
    $missingBase = Join-Path $tempRoot 'missing\.tau'

    New-Item -ItemType Directory -Force -Path (Join-Path $migratableBase 'commands') | Out-Null
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $migratableBase 'commands') 'deploy.md') -Text '# Deploy prompt'

    New-Item -ItemType Directory -Force -Path (Join-Path $targetExistsBase 'commands') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $targetExistsBase 'prompts') | Out-Null
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $targetExistsBase 'commands') 'legacy.md') -Text '# Legacy command'
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $targetExistsBase 'prompts') 'existing.md') -Text '# Existing prompt'

    New-Item -ItemType Directory -Force -Path $noCommandsBase | Out-Null

    New-Item -ItemType Directory -Force -Path $fileSourceBase | Out-Null
    Write-Utf8NoBomText -Path (Join-Path $fileSourceBase 'commands') -Text 'not a directory'

    $baseDirectories = @($migratableBase, $targetExistsBase, $noCommandsBase, $fileSourceBase, $missingBase)

    $dryRun = Invoke-Migration -BaseDirectories $baseDirectories
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run migration to succeed.'
    Add-Assertion -Name 'dry-run base count' -Passed ([int]$dryRun.baseDirectoryCount -eq 5) -Detail "Expected 5 base directories, actual $($dryRun.baseDirectoryCount)."
    Add-Assertion -Name 'dry-run migratable count' -Passed ([int]$dryRun.scan.migratable -eq 1) -Detail "Expected 1 migratable commands directory, actual $($dryRun.scan.migratable)."
    Add-Assertion -Name 'dry-run migrated count' -Passed ([int]$dryRun.scan.migrated -eq 0) -Detail "Expected dry-run migrated count 0, actual $($dryRun.scan.migrated)."
    Add-Assertion -Name 'dry-run skipped count' -Passed ([int]$dryRun.scan.skipped -eq 4) -Detail "Expected 4 skipped base directories, actual $($dryRun.scan.skipped)."
    Add-Assertion -Name 'dry-run failed count' -Passed ([int]$dryRun.scan.failed -eq 0) -Detail "Expected 0 failed migrations, actual $($dryRun.scan.failed)."

    $dryMigratable = Get-DirectoryResult -Summary $dryRun -BaseDirectory $migratableBase
    $dryTargetExists = Get-DirectoryResult -Summary $dryRun -BaseDirectory $targetExistsBase
    $dryNoCommands = Get-DirectoryResult -Summary $dryRun -BaseDirectory $noCommandsBase
    $dryFileSource = Get-DirectoryResult -Summary $dryRun -BaseDirectory $fileSourceBase
    $dryMissing = Get-DirectoryResult -Summary $dryRun -BaseDirectory $missingBase

    Add-Assertion -Name 'dry-run marks migratable directory' -Passed ($dryMigratable.action -eq 'would-migrate' -and $dryMigratable.sourceKind -eq 'directory') -Detail 'Expected migratable commands directory to be marked would-migrate.'
    Add-Assertion -Name 'dry-run target-exists skip' -Passed ($dryTargetExists.action -eq 'skipped' -and $dryTargetExists.reason -eq 'prompts-exists') -Detail 'Expected existing prompts directory to block migration.'
    Add-Assertion -Name 'dry-run no-commands skip' -Passed ($dryNoCommands.action -eq 'skipped' -and $dryNoCommands.reason -eq 'no-commands') -Detail 'Expected base directory without commands to be skipped.'
    Add-Assertion -Name 'dry-run file-source skip' -Passed ($dryFileSource.action -eq 'skipped' -and $dryFileSource.reason -eq 'source-not-directory') -Detail 'Expected file named commands to be skipped.'
    Add-Assertion -Name 'dry-run missing-base skip' -Passed ($dryMissing.action -eq 'skipped' -and $dryMissing.reason -eq 'base-directory-missing') -Detail 'Expected missing base directory to be skipped.'
    Add-Assertion -Name 'dry-run does not move source' -Passed (
        (Test-Path -LiteralPath (Join-Path $migratableBase 'commands') -PathType Container) -and
        (-not (Test-Path -LiteralPath (Join-Path $migratableBase 'prompts')))
    ) -Detail 'Dry-run unexpectedly renamed commands to prompts.'
    Add-Assertion -Name 'remaining gaps preserve broader migration scope' -Passed ((@($dryRun.remainingGaps) -join "`n") -match 'auth/settings/session/keybindings/tools') -Detail 'Expected remaining gaps to keep broader migrations open.'

    $applied = Invoke-Migration -BaseDirectories $baseDirectories -Apply
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply migration to succeed.'
    Add-Assertion -Name 'apply migrated count' -Passed ([int]$applied.scan.migrated -eq 1) -Detail "Expected apply to migrate 1 directory, actual $($applied.scan.migrated)."
    Add-Assertion -Name 'apply skipped count' -Passed ([int]$applied.scan.skipped -eq 4) -Detail "Expected apply to skip 4 directories, actual $($applied.scan.skipped)."
    Add-Assertion -Name 'apply failed count' -Passed ([int]$applied.scan.failed -eq 0) -Detail "Expected apply to have 0 failed migrations, actual $($applied.scan.failed)."

    $migratedPrompts = Join-Path $migratableBase 'prompts'
    Add-Assertion -Name 'apply renames commands to prompts' -Passed (
        (Test-Path -LiteralPath $migratedPrompts -PathType Container) -and
        (-not (Test-Path -LiteralPath (Join-Path $migratableBase 'commands')))
    ) -Detail 'Expected apply to rename commands directory to prompts.'
    Add-Assertion -Name 'apply preserves prompt file content' -Passed ((Get-Content -LiteralPath (Join-Path $migratedPrompts 'deploy.md') -Raw) -eq '# Deploy prompt') -Detail 'Migrated prompt content was not preserved.'
    Add-Assertion -Name 'target-exists source preserved' -Passed (
        (Test-Path -LiteralPath (Join-Path $targetExistsBase 'commands') -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $targetExistsBase 'prompts') -PathType Container)
    ) -Detail 'Existing prompts case should leave commands and prompts in place.'
    Add-Assertion -Name 'file-source preserved' -Passed (Test-Path -LiteralPath (Join-Path $fileSourceBase 'commands') -PathType Leaf) -Detail 'File named commands should remain untouched.'

    $idempotent = Invoke-Migration -BaseDirectories $baseDirectories -Apply
    Add-Assertion -Name 'idempotent apply' -Passed (
        [int]$idempotent.scan.migrated -eq 0 -and
        [int]$idempotent.scan.skipped -eq 5 -and
        [int]$idempotent.scan.failed -eq 0
    ) -Detail 'Expected second apply to migrate nothing and skip all configured bases.'

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
        Write-Host 'Tau CodingAgent commands migration smoke passed'
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
        Write-Host 'Tau CodingAgent commands migration smoke failed'
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
