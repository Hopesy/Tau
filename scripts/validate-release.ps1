param(
    [string]$Configuration = 'Release',
    [string[]]$Runtimes = @('osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64', 'win-x64'),
    [string]$OutputRoot = 'artifacts',
    [string]$ArchiveRoot = 'artifacts/releases',
    [switch]$Run,
    [switch]$AllowDirty,
    [switch]$SkipRestore,
    [switch]$SkipNoEnv,
    [switch]$SkipMatrix,
    [switch]$SkipSmoke,
    [switch]$SkipExecutableSmoke,
    [switch]$ForceSmoke,
    [switch]$SkipPackage,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:checks = @()
$script:warnings = @()
$script:hardPreflightFailure = $false
$script:commandResults = @()

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('passed', 'warning', 'blocked')]
        [string]$Status,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $script:checks += [ordered]@{
        name = $Name
        status = $Status
        detail = $Detail
    }

    if ($Status -eq 'warning') {
        $script:warnings += $Detail
    }
    elseif ($Status -eq 'blocked') {
        $script:hardPreflightFailure = $true
    }
}

function Convert-ToFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-GitStatus {
    try {
        $output = & git status --porcelain 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            return [ordered]@{
                available = $false
                clean = $false
                entries = @()
                error = ($output -join [Environment]::NewLine)
            }
        }

        $entries = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        return [ordered]@{
            available = $true
            clean = ($entries.Count -eq 0)
            entries = $entries
            error = ''
        }
    }
    catch {
        return [ordered]@{
            available = $false
            clean = $false
            entries = @()
            error = $_.Exception.Message
        }
    }
}

function Join-CommandDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $parts = @($FilePath) + $Arguments
    $displayParts = @()
    foreach ($part in $parts) {
        if ($part -match '\s') {
            $displayParts += '"' + ($part -replace '"', '\"') + '"'
        }
        else {
            $displayParts += $part
        }
    }

    return ($displayParts -join ' ')
}

function Get-OutputPreview {
    param(
        [AllowNull()]
        [string]$Output,
        [int]$MaxLength = 8000
    )

    if ([string]::IsNullOrEmpty($Output)) {
        return ''
    }

    if ($Output.Length -le $MaxLength) {
        return $Output
    }

    return $Output.Substring(0, $MaxLength) + "`n... <truncated $($Output.Length - $MaxLength) chars>"
}

function Invoke-ValidationStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $display = Join-CommandDisplay -FilePath $FilePath -Arguments $Arguments
    $startedAt = Get-Date

    if (-not $Json) {
        Write-Host "==> $Name"
        Write-Host "    $display"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $outputText = ($output -join [Environment]::NewLine)
    $durationMs = [int]((Get-Date) - $startedAt).TotalMilliseconds

    if (-not $Json) {
        if (-not [string]::IsNullOrWhiteSpace($outputText)) {
            Write-Host $outputText
        }
        if ($exitCode -eq 0) {
            Write-Host "==> $Name passed"
        }
        else {
            Write-Host "==> $Name failed with exit code $exitCode"
        }
        Write-Host ''
    }

    $result = [ordered]@{
        name = $Name
        command = $display
        exitCode = $exitCode
        durationMs = $durationMs
        outputPreview = Get-OutputPreview -Output $outputText
        outputLength = $outputText.Length
    }
    $script:commandResults += $result

    if ($exitCode -ne 0) {
        throw "Release validation step '$Name' failed with exit code $exitCode."
    }
}

$normalizedRuntimes = @($Runtimes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($normalizedRuntimes.Count -eq 0) {
    throw 'At least one runtime identifier is required.'
}

$outputRootFull = Convert-ToFullPath -Path $OutputRoot -BasePath $repoRoot
$archiveRootFull = Convert-ToFullPath -Path $ArchiveRoot -BasePath $repoRoot
$runtimeArgument = ($normalizedRuntimes -join ',')

Add-Check -Name 'runtimes' -Status 'passed' -Detail "Release validation target runtime(s): $runtimeArgument."

$gitStatus = Get-GitStatus
if (-not $gitStatus.available) {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Could not read git status: $($gitStatus.error)"
}
elseif ($gitStatus.clean) {
    Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean.'
}
elseif ($Run -and -not $AllowDirty) {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Release validation run requires a clean worktree; pass -AllowDirty only for local WIP validation."
}
else {
    $mode = if ($Run) { 'run' } else { 'dry-run' }
    Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s), allowed for $mode because -AllowDirty was supplied or no commands will execute."
}

$requiredScripts = @(
    'scripts/verify-no-env.ps1',
    'scripts/build-release-matrix.ps1',
    'scripts/package-release-matrix.ps1',
    'scripts/package-release-artifacts.ps1',
    'scripts/smoke-release-artifacts.ps1'
)
$missingScripts = @($requiredScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_)) })
if ($missingScripts.Count -eq 0) {
    Add-Check -Name 'release-validation-scripts' -Status 'passed' -Detail 'No-env, release matrix, package and smoke scripts are present.'
}
else {
    Add-Check -Name 'release-validation-scripts' -Status 'blocked' -Detail "Missing release validation script(s): $($missingScripts -join ', ')"
}

if ($SkipNoEnv) {
    Add-Check -Name 'no-env-validation' -Status 'warning' -Detail 'No-env validation is skipped for this run.'
}
else {
    Add-Check -Name 'no-env-validation' -Status 'passed' -Detail 'No-env validation is scheduled.'
}

if ($SkipMatrix) {
    Add-Check -Name 'release-matrix' -Status 'warning' -Detail 'Release matrix build/package validation is skipped for this run.'
}
else {
    Add-Check -Name 'release-matrix' -Status 'passed' -Detail 'Release matrix build/package validation is scheduled.'
}

$noEnvArguments = @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $repoRoot 'scripts/verify-no-env.ps1')
)
if ($SkipRestore) {
    $noEnvArguments += '-SkipRestore'
}
if (-not $SkipSmoke) {
    $noEnvArguments += '-RunSmoke'
}

$matrixArguments = @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $repoRoot 'scripts/build-release-matrix.ps1'),
    '-Configuration',
    $Configuration,
    '-Runtimes'
) + $normalizedRuntimes + @(
    '-OutputRoot',
    $outputRootFull,
    '-ArchiveRoot',
    $archiveRootFull
)
if ($SkipRestore) {
    $matrixArguments += '-SkipRestore'
}
if ($SkipSmoke) {
    $matrixArguments += '-SkipSmoke'
}
if ($SkipExecutableSmoke) {
    $matrixArguments += '-SkipExecutableSmoke'
}
if ($ForceSmoke) {
    $matrixArguments += '-ForceSmoke'
}
if ($SkipPackage) {
    $matrixArguments += '-SkipPackage'
}

$plannedCommands = @(
    [ordered]@{
        name = 'diff-check'
        enabled = $true
        command = 'git diff --check'
        purpose = 'Validate whitespace and patch hygiene before release validation.'
    },
    [ordered]@{
        name = 'no-env-validation'
        enabled = -not $SkipNoEnv.IsPresent
        command = Join-CommandDisplay -FilePath 'powershell' -Arguments $noEnvArguments
        purpose = 'Run the project gate under provider/auth-isolated child-process environment.'
    },
    [ordered]@{
        name = 'release-matrix-build'
        enabled = -not $SkipMatrix.IsPresent
        command = Join-CommandDisplay -FilePath 'powershell' -Arguments $matrixArguments
        purpose = 'Build and package the configured RID release matrix with artifact smoke.'
    }
)

$enabledValidationCommands = @($plannedCommands | Where-Object { $_.enabled })
$skippedValidationCommands = @($plannedCommands | Where-Object { -not $_.enabled })
$validationBaseLevel = 'full-local'
if ($SkipNoEnv -and $SkipMatrix) {
    $validationBaseLevel = 'minimal-diff-only'
}
elseif ($SkipNoEnv -or $SkipMatrix) {
    $validationBaseLevel = 'partial-local'
}
$validationLevel = if ($Run) { $validationBaseLevel } else { "$validationBaseLevel-dry-run" }
$enabledValidationNames = @($enabledValidationCommands | ForEach-Object { $_.name })
$skippedValidationNames = @($skippedValidationCommands | ForEach-Object { $_.name })

if ($SkipNoEnv -and $SkipMatrix) {
    Add-Check -Name 'validation-coverage' -Status 'warning' -Detail 'Only git diff --check is enabled; no-env and release matrix validation are both skipped.'
}
elseif ($SkipNoEnv -or $SkipMatrix) {
    Add-Check -Name 'validation-coverage' -Status 'warning' -Detail "Partial local validation is enabled; skipped step(s): $($skippedValidationNames -join ', ')."
}
else {
    Add-Check -Name 'validation-coverage' -Status 'passed' -Detail 'Full local release validation plan is enabled: diff-check, no-env validation and release matrix build/package.'
}

$remainingGaps = @(
    'This validation script does not run prepare-release.ps1 -Apply, commit, tag, publish or push.',
    'Real non-host runner executable smoke and external provider/Slack/Docker/SSH/HF/GPU/vLLM release e2e remain open.',
    'Exact Unix release wrapper/auth-backup parity and upstream examples/Photon/interactive asset payload parity remain open.'
)

if ($Run -and $script:hardPreflightFailure) {
    $result = [ordered]@{
        schemaVersion = 1
        dryRun = $false
        ran = $false
        configuration = $Configuration
        runtimes = $normalizedRuntimes
        outputRoot = $outputRootFull
        archiveRoot = $archiveRootFull
        workingTree = [ordered]@{
            clean = $gitStatus.clean
            allowDirty = $AllowDirty.IsPresent
            statusCount = @($gitStatus.entries).Count
            status = $gitStatus.entries
        }
        checks = $script:checks
        validationLevel = $validationLevel
        enabledValidationCount = $enabledValidationCommands.Count
        skippedValidationCount = $skippedValidationCommands.Count
        enabledValidationNames = $enabledValidationNames
        skippedValidationNames = $skippedValidationNames
        plannedCommands = $plannedCommands
        commandResults = $script:commandResults
        remainingGaps = $remainingGaps
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release validation blocked'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
    }

    exit 1
}

$runFailed = $false
if ($Run) {
    try {
        Invoke-ValidationStep -Name 'diff-check' -FilePath 'git' -Arguments @('diff', '--check')

        if (-not $SkipNoEnv) {
            Invoke-ValidationStep -Name 'no-env-validation' -FilePath 'powershell' -Arguments $noEnvArguments
        }

        if (-not $SkipMatrix) {
            Invoke-ValidationStep -Name 'release-matrix-build' -FilePath 'powershell' -Arguments $matrixArguments
        }
    }
    catch {
        $runFailed = $true
        if (-not $Json) {
            Write-Host $_.Exception.Message
        }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    dryRun = -not $Run.IsPresent
    ran = $Run.IsPresent
    succeeded = (-not $runFailed) -and (-not ($Run -and $script:hardPreflightFailure))
    configuration = $Configuration
    runtimes = $normalizedRuntimes
    outputRoot = $outputRootFull
    archiveRoot = $archiveRootFull
    options = [ordered]@{
        allowDirty = $AllowDirty.IsPresent
        skipRestore = $SkipRestore.IsPresent
        skipNoEnv = $SkipNoEnv.IsPresent
        skipMatrix = $SkipMatrix.IsPresent
        skipSmoke = $SkipSmoke.IsPresent
        skipExecutableSmoke = $SkipExecutableSmoke.IsPresent
        forceSmoke = $ForceSmoke.IsPresent
        skipPackage = $SkipPackage.IsPresent
    }
    workingTree = [ordered]@{
        clean = $gitStatus.clean
        allowDirty = $AllowDirty.IsPresent
        statusCount = @($gitStatus.entries).Count
        status = $gitStatus.entries
    }
    checks = $script:checks
    validationLevel = $validationLevel
    enabledValidationCount = $enabledValidationCommands.Count
    skippedValidationCount = $skippedValidationCommands.Count
    enabledValidationNames = $enabledValidationNames
    skippedValidationNames = $skippedValidationNames
    plannedCommands = $plannedCommands
    commandResults = $script:commandResults
    remainingGaps = $remainingGaps
}

if ($Json) {
    $result | ConvertTo-Json -Depth 12
}
else {
    Write-Host 'Tau release validation'
    Write-Host "  mode: $(if ($Run) { 'run' } else { 'dry-run' })"
    Write-Host "  configuration: $Configuration"
    Write-Host "  runtimes: $($normalizedRuntimes -join ', ')"
    Write-Host "  validation level: $validationLevel"
    Write-Host "  enabled validations: $($enabledValidationNames -join ', ')"
    if ($skippedValidationNames.Count -gt 0) {
        Write-Host "  skipped validations: $($skippedValidationNames -join ', ')"
    }
    Write-Host "  output root: $outputRootFull"
    Write-Host "  archive root: $archiveRootFull"
    Write-Host ''

    Write-Host 'Checks:'
    foreach ($check in $script:checks) {
        Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
    }
    Write-Host ''

    Write-Host 'Validation commands:'
    foreach ($command in $plannedCommands) {
        $state = if ($command.enabled) { 'enabled' } else { 'skipped' }
        Write-Host "  - [$state] $($command.command)"
    }
    Write-Host ''

    Write-Host 'Remaining gaps:'
    foreach ($gap in $remainingGaps) {
        Write-Host "  - $gap"
    }
}

if ($runFailed) {
    exit 1
}
