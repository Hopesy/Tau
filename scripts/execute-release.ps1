param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ReleaseTarget,
    [string]$Configuration = 'Release',
    [string[]]$Runtimes = @('osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64', 'win-x64'),
    [string]$Date = '',
    [string]$FeatureDomain = 'Release',
    [string]$UserValue = '',
    [string]$ChangeSummary = '',
    [switch]$Apply,
    [switch]$AllowDirty,
    [switch]$SkipValidation,
    [switch]$SkipNoEnv,
    [switch]$SkipMatrix,
    [switch]$SkipSmoke,
    [switch]$SkipExecutableSmoke,
    [switch]$ForceSmoke,
    [switch]$SkipPackage,
    [switch]$SkipRestore,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:checks = @()
$script:commandResults = @()
$script:hardPreflightFailure = $false
$script:lastInternalError = $null

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

    if ($Status -eq 'blocked') {
        $script:hardPreflightFailure = $true
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

function Get-StatusPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Entry
    )

    if ($Entry.Length -le 3) {
        return $Entry.Trim()
    }

    $path = $Entry.Substring(3)
    $renameMarker = ' -> '
    $renameIndex = $path.LastIndexOf($renameMarker, [StringComparison]::Ordinal)
    if ($renameIndex -ge 0) {
        $path = $path.Substring($renameIndex + $renameMarker.Length)
    }

    return ($path.Trim('"') -replace '/', '\')
}

function Test-GitTagExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TagName
    )

    $output = & git rev-parse -q --verify "refs/tags/$TagName" 2>$null
    return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($output -join ''))
}

function Invoke-JsonScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $fullScriptPath = Join-Path $repoRoot $ScriptPath
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $fullScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    catch {
        $script:lastInternalError = [ordered]@{
            name = $Name
            scriptPath = $fullScriptPath
            arguments = $Arguments
            exception = $_.Exception.Message
        }
        throw "$Name failed to start. Script: $fullScriptPath. Error: $($_.Exception.Message)"
    }

    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        $script:lastInternalError = [ordered]@{
            name = $Name
            scriptPath = $fullScriptPath
            arguments = $Arguments
            exitCode = $exitCode
            output = $outputText
        }
        throw "$Name failed with exit code $exitCode. Script: $fullScriptPath. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        $script:lastInternalError = [ordered]@{
            name = $Name
            scriptPath = $fullScriptPath
            arguments = $Arguments
            exitCode = $exitCode
            output = $outputText
            exception = $_.Exception.Message
        }
        throw "$Name did not return valid JSON. Script: $fullScriptPath. Output: $outputText"
    }
}

function Invoke-ReleaseStep {
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
        throw "Release execution step '$Name' failed with exit code $exitCode."
    }

    return $outputText
}

function New-BaseResult {
    param(
        [AllowNull()]
        [object]$Plan,
        [AllowNull()]
        [object]$Prepare,
        [AllowNull()]
        [object]$Validate,
        [AllowNull()]
        [object[]]$LocalMutationPlan,
        [AllowNull()]
        [string[]]$NonExecutedMutations
    )

    $nextVersion = if ($Plan) { $Plan.nextVersion } else { $null }
    $releaseTag = if ($nextVersion) { "v$nextVersion" } else { '' }

    return [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $false
        succeeded = $false
        releaseTarget = $ReleaseTarget
        nextVersion = $nextVersion
        releaseTag = $releaseTag
        configuration = $Configuration
        runtimes = $normalizedRuntimes
        options = [ordered]@{
            skipValidation = $SkipValidation.IsPresent
            allowDirty = $AllowDirty.IsPresent
            skipRestore = $SkipRestore.IsPresent
            skipNoEnv = $SkipNoEnv.IsPresent
            skipMatrix = $SkipMatrix.IsPresent
            skipSmoke = $SkipSmoke.IsPresent
            skipExecutableSmoke = $SkipExecutableSmoke.IsPresent
            forceSmoke = $ForceSmoke.IsPresent
            skipPackage = $SkipPackage.IsPresent
        }
        plan = if ($Plan) {
            [ordered]@{
                nextVersion = $Plan.nextVersion
                currentVersion = $Plan.currentVersion
                plannedCommandNames = @($Plan.plannedCommands | ForEach-Object { $_.name })
            }
        } else { $null }
        preparation = if ($Prepare) {
            [ordered]@{
                dryRun = $Prepare.dryRun
                applied = $Prepare.applied
                changedFiles = @($Prepare.changedFiles)
                nextVersion = $Prepare.nextVersion
            }
        } else { $null }
        validation = if ($Validate) {
            [ordered]@{
                dryRun = $Validate.dryRun
                ran = $Validate.ran
                validationLevel = $Validate.validationLevel
                enabledValidationNames = @($Validate.enabledValidationNames)
                skippedValidationNames = @($Validate.skippedValidationNames)
            }
        } else { $null }
        checks = $script:checks
        localMutationPlan = @($LocalMutationPlan)
        commandResults = $script:commandResults
        nonExecutedMutations = @($NonExecutedMutations)
    }
}

$normalizedRuntimes = @($Runtimes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($normalizedRuntimes.Count -eq 0) {
    throw 'At least one runtime identifier is required.'
}

$planArgs = @($ReleaseTarget, '-Configuration', $Configuration, '-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-Json')
$prepareArgs = @($ReleaseTarget, '-Configuration', $Configuration, '-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-Json')
if (-not [string]::IsNullOrWhiteSpace($Date)) {
    $prepareArgs += @('-Date', $Date)
}
if (-not [string]::IsNullOrWhiteSpace($FeatureDomain)) {
    $prepareArgs += @('-FeatureDomain', $FeatureDomain)
}
if (-not [string]::IsNullOrWhiteSpace($UserValue)) {
    $prepareArgs += @('-UserValue', $UserValue)
}
if (-not [string]::IsNullOrWhiteSpace($ChangeSummary)) {
    $prepareArgs += @('-ChangeSummary', $ChangeSummary)
}

$validateArgs = @('-Configuration', $Configuration, '-Runtimes') + $normalizedRuntimes + @('-Json')
if ($SkipRestore) {
    $validateArgs += '-SkipRestore'
}
if ($SkipNoEnv) {
    $validateArgs += '-SkipNoEnv'
}
if ($SkipMatrix) {
    $validateArgs += '-SkipMatrix'
}
if ($SkipSmoke) {
    $validateArgs += '-SkipSmoke'
}
if ($SkipExecutableSmoke) {
    $validateArgs += '-SkipExecutableSmoke'
}
if ($ForceSmoke) {
    $validateArgs += '-ForceSmoke'
}
if ($SkipPackage) {
    $validateArgs += '-SkipPackage'
}

$expectedChangedFiles = @(
    'Directory.Build.props',
    'docs\releases\feature-release-notes.md'
)

$nonExecutedMutations = @(
    'Publish/upload release archives remains deliberately non-executed by this local script.',
    'Push main and the release tag remains deliberately non-executed by this local script.',
    'Adding an upstream-style fresh [Unreleased] changelog commit remains unmapped because Tau release notes use a dated table rather than package CHANGELOG.md sections.',
    'Real external provider/Slack/Docker/SSH/HF/GPU/vLLM release e2e remains open.'
)

try {
    $plan = Invoke-JsonScript -Name 'plan-release' -ScriptPath 'scripts/plan-release.ps1' -Arguments $planArgs
    $prepare = Invoke-JsonScript -Name 'prepare-release' -ScriptPath 'scripts/prepare-release.ps1' -Arguments $prepareArgs
    $validate = Invoke-JsonScript -Name 'validate-release' -ScriptPath 'scripts/validate-release.ps1' -Arguments $validateArgs

    $releaseTag = "v$($plan.nextVersion)"
    $gitStatus = Get-GitStatus
    if (-not $gitStatus.available) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Could not read git status: $($gitStatus.error)"
    }
    elseif ($gitStatus.clean) {
        Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean before release execution.'
    }
    elseif ($Apply) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Local release execution requires a clean worktree before -Apply."
    }
    else {
        $detail = if ($AllowDirty) {
            "Working tree has $($gitStatus.entries.Count) uncommitted item(s), allowed for dry-run because -AllowDirty was supplied."
        }
        else {
            "Working tree has $($gitStatus.entries.Count) uncommitted item(s); dry-run remains read-only."
        }
        Add-Check -Name 'clean-worktree' -Status 'warning' -Detail $detail
    }

    if (Test-GitTagExists -TagName $releaseTag) {
        Add-Check -Name 'release-tag' -Status 'blocked' -Detail "Release tag $releaseTag already exists."
    }
    else {
        Add-Check -Name 'release-tag' -Status 'passed' -Detail "Release tag $releaseTag is available."
    }

    if ($SkipValidation) {
        Add-Check -Name 'release-validation' -Status 'warning' -Detail 'Release validation is skipped by explicit option; use only for local rehearsal.'
    }
    else {
        Add-Check -Name 'release-validation' -Status 'passed' -Detail "Release validation is planned with level $($validate.validationLevel)."
    }

    $runtimeArgument = ($normalizedRuntimes -join ',')
    $localMutationPlan = @(
        [ordered]@{
            name = 'release-contract-smoke'
            command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -ReleaseTarget $ReleaseTarget -Runtimes $runtimeArgument"
            executedWhenApply = $true
        },
        [ordered]@{
            name = 'release-preparation-apply'
            command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-release.ps1 $ReleaseTarget -Apply"
            executedWhenApply = $true
        },
        [ordered]@{
            name = 'release-validation-run'
            command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Run -AllowDirty -Runtimes $runtimeArgument"
            executedWhenApply = -not $SkipValidation.IsPresent
        },
        [ordered]@{
            name = 'release-stage'
            command = 'git add -- Directory.Build.props docs/releases/feature-release-notes.md'
            executedWhenApply = $true
        },
        [ordered]@{
            name = 'release-commit'
            command = "git commit -m `"Release $releaseTag`""
            executedWhenApply = $true
        },
        [ordered]@{
            name = 'release-tag'
            command = "git tag $releaseTag"
            executedWhenApply = $true
        }
    )

    if ($script:hardPreflightFailure) {
        $result = New-BaseResult -Plan $plan -Prepare $prepare -Validate $validate -LocalMutationPlan $localMutationPlan -NonExecutedMutations $nonExecutedMutations
        if ($Json) {
            $result | ConvertTo-Json -Depth 12
        }
        else {
            Write-Host 'Tau local release execution blocked'
            foreach ($check in $script:checks) {
                Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
            }
        }
        exit 1
    }

    if ($Apply) {
        $contractArgs = @('-ReleaseTarget', $ReleaseTarget, '-Runtimes') + $normalizedRuntimes
        $contractCommandArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/verify-release-contracts.ps1')) + $contractArgs
        Invoke-ReleaseStep -Name 'release-contract-smoke' -FilePath 'powershell' -Arguments $contractCommandArgs | Out-Null

        $prepareApplyArgs = @($ReleaseTarget, '-Configuration', $Configuration, '-Runtimes') + $normalizedRuntimes + @('-Apply', '-Json')
        if (-not [string]::IsNullOrWhiteSpace($Date)) {
            $prepareApplyArgs += @('-Date', $Date)
        }
        if (-not [string]::IsNullOrWhiteSpace($FeatureDomain)) {
            $prepareApplyArgs += @('-FeatureDomain', $FeatureDomain)
        }
        if (-not [string]::IsNullOrWhiteSpace($UserValue)) {
            $prepareApplyArgs += @('-UserValue', $UserValue)
        }
        if (-not [string]::IsNullOrWhiteSpace($ChangeSummary)) {
            $prepareApplyArgs += @('-ChangeSummary', $ChangeSummary)
        }
        $prepareCommandArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/prepare-release.ps1')) + $prepareApplyArgs
        $prepareOutput = Invoke-ReleaseStep -Name 'release-preparation-apply' -FilePath 'powershell' -Arguments $prepareCommandArgs
        $prepare = $prepareOutput | ConvertFrom-Json

        if (-not $SkipValidation) {
            $validateRunArgs = @('-Configuration', $Configuration, '-Runtimes') + $normalizedRuntimes + @('-Run', '-AllowDirty', '-Json')
            if ($SkipRestore) {
                $validateRunArgs += '-SkipRestore'
            }
            if ($SkipNoEnv) {
                $validateRunArgs += '-SkipNoEnv'
            }
            if ($SkipMatrix) {
                $validateRunArgs += '-SkipMatrix'
            }
            if ($SkipSmoke) {
                $validateRunArgs += '-SkipSmoke'
            }
            if ($SkipExecutableSmoke) {
                $validateRunArgs += '-SkipExecutableSmoke'
            }
            if ($ForceSmoke) {
                $validateRunArgs += '-ForceSmoke'
            }
            if ($SkipPackage) {
                $validateRunArgs += '-SkipPackage'
            }
            $validateCommandArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/validate-release.ps1')) + $validateRunArgs
            $validateOutput = Invoke-ReleaseStep -Name 'release-validation-run' -FilePath 'powershell' -Arguments $validateCommandArgs
            $validate = $validateOutput | ConvertFrom-Json
        }

        $postPrepareStatus = Get-GitStatus
        $changedPaths = @($postPrepareStatus.entries | ForEach-Object { Get-StatusPath -Entry $_ })
        $unexpectedPaths = @($changedPaths | Where-Object { $expectedChangedFiles -notcontains $_ })
        if ($unexpectedPaths.Count -gt 0) {
            throw "Release preparation produced unexpected git changes: $($unexpectedPaths -join ', ')"
        }
        if ($changedPaths.Count -eq 0) {
            throw 'Release preparation produced no git changes to commit.'
        }

        Invoke-ReleaseStep -Name 'release-stage' -FilePath 'git' -Arguments @('add', '--', 'Directory.Build.props', 'docs/releases/feature-release-notes.md') | Out-Null
        Invoke-ReleaseStep -Name 'release-commit' -FilePath 'git' -Arguments @('commit', '-m', "Release $releaseTag") | Out-Null
        Invoke-ReleaseStep -Name 'release-tag' -FilePath 'git' -Arguments @('tag', $releaseTag) | Out-Null
    }

    $result = New-BaseResult -Plan $plan -Prepare $prepare -Validate $validate -LocalMutationPlan $localMutationPlan -NonExecutedMutations $nonExecutedMutations
    $result['applied'] = $Apply.IsPresent
    $result['succeeded'] = $true

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau local release execution'
        Write-Host "  target: $ReleaseTarget"
        Write-Host "  next version: $($plan.nextVersion)"
        Write-Host "  tag: $releaseTag"
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        Write-Host ''
        Write-Host 'Checks:'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
        Write-Host ''
        Write-Host 'Local release steps:'
        foreach ($step in $localMutationPlan) {
            $state = if ($step.executedWhenApply) { 'apply' } else { 'skipped' }
            Write-Host "  - [$state] $($step.command)"
        }
        Write-Host ''
        Write-Host 'Still not executed:'
        foreach ($mutation in $nonExecutedMutations) {
            Write-Host "  - $mutation"
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $false
        succeeded = $false
        releaseTarget = $ReleaseTarget
        runtimes = $normalizedRuntimes
        checks = $script:checks
        commandResults = $script:commandResults
        internalError = $script:lastInternalError
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau local release execution failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
