param(
    [string]$ReleaseTarget = 'patch',
    [string[]]$Runtimes = @('win-x64'),
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$script:results = [ordered]@{}

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

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Actual,
        [AllowNull()]
        [object]$Expected
    )

    $passed = [object]::Equals($Actual, $Expected)
    Add-Assertion -Name $Name -Passed $passed -Detail "Expected '$Expected', actual '$Actual'."
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Actual,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed ($Actual -match $Pattern) -Detail "Expected '$Actual' to match '$Pattern'."
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object[]]$Actual,
        [Parameter(Mandatory = $true)]
        [string[]]$Expected
    )

    $actualValues = @($Actual)
    foreach ($expectedValue in $Expected) {
        if ($actualValues -notcontains $expectedValue) {
            Add-Assertion -Name $Name -Passed $false -Detail "Expected list to contain '$expectedValue'. Actual: $($actualValues -join ', ')."
        }
    }

    Add-Assertion -Name $Name -Passed $true -Detail "List contains expected value(s): $($Expected -join ', ')."
}

function Assert-TextContainsAll {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedPatterns
    )

    foreach ($pattern in $ExpectedPatterns) {
        if ($Text -notmatch $pattern) {
            Add-Assertion -Name $Name -Passed $false -Detail "Expected text to match '$pattern'."
        }
    }

    Add-Assertion -Name $Name -Passed $true -Detail "Text contains expected pattern(s): $($ExpectedPatterns -join ', ')."
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
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $fullScriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "$Name did not return valid JSON. Output: $outputText"
    }
}

function Get-Names {
    param(
        [AllowNull()]
        [object[]]$Items
    )

    return @($Items | ForEach-Object { $_.name })
}

try {
    $normalizedRuntimes = @($Runtimes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($normalizedRuntimes.Count -eq 0) {
        throw 'At least one runtime identifier is required.'
    }

    $planArgs = @($ReleaseTarget, '-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-Json')
    $prepareArgs = @($ReleaseTarget, '-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-Json')
    $validateArgs = @('-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-Json')
    $minimalValidateArgs = @('-Runtimes') + $normalizedRuntimes + @('-AllowDirty', '-SkipNoEnv', '-SkipMatrix', '-Json')

    $plan = Invoke-JsonScript -Name 'plan-release' -ScriptPath 'scripts/plan-release.ps1' -Arguments $planArgs
    $prepare = Invoke-JsonScript -Name 'prepare-release' -ScriptPath 'scripts/prepare-release.ps1' -Arguments $prepareArgs
    $validate = Invoke-JsonScript -Name 'validate-release' -ScriptPath 'scripts/validate-release.ps1' -Arguments $validateArgs
    $minimalValidate = Invoke-JsonScript -Name 'validate-release-minimal' -ScriptPath 'scripts/validate-release.ps1' -Arguments $minimalValidateArgs
    $execute = Invoke-JsonScript -Name 'execute-release' -ScriptPath 'scripts/execute-release.ps1' -Arguments $prepareArgs
    $finalize = Invoke-JsonScript -Name 'release-finalize-smoke' -ScriptPath 'scripts/verify-release-finalize.ps1' -Arguments @('-Json')
    $packagePublish = Invoke-JsonScript -Name 'release-package-publish-smoke' -ScriptPath 'scripts/verify-release-package-publish.ps1' -Arguments @('-Json')
    $provenance = Invoke-JsonScript -Name 'release-provenance-smoke' -ScriptPath 'scripts/verify-release-provenance.ps1' -Arguments @('-Json')

    $script:results.plan = [ordered]@{
        nextVersion = $plan.nextVersion
        commandNames = Get-Names -Items @($plan.plannedCommands)
        mutationCount = @($plan.nonExecutedMutations).Count
    }
    $script:results.prepare = [ordered]@{
        nextVersion = $prepare.nextVersion
        commandNames = Get-Names -Items @($prepare.plannedCommands)
        changedFiles = @($prepare.changedFiles)
    }
    $script:results.validate = [ordered]@{
        validationLevel = $validate.validationLevel
        enabledValidationCount = $validate.enabledValidationCount
        skippedValidationCount = $validate.skippedValidationCount
        commandNames = Get-Names -Items @($validate.plannedCommands)
    }
    $script:results.minimalValidate = [ordered]@{
        validationLevel = $minimalValidate.validationLevel
        enabledValidationCount = $minimalValidate.enabledValidationCount
        skippedValidationCount = $minimalValidate.skippedValidationCount
        skippedValidationNames = @($minimalValidate.skippedValidationNames)
    }
    $script:results.execute = [ordered]@{
        nextVersion = $execute.nextVersion
        dryRun = $execute.dryRun
        applied = $execute.applied
        localMutationNames = Get-Names -Items @($execute.localMutationPlan)
    }
    $script:results.finalize = [ordered]@{
        succeeded = $finalize.succeeded
        assertionCount = @($finalize.assertions).Count
        dryRunExitCode = $finalize.results.dryRun.exitCode
        applyExitCode = $finalize.results.apply.exitCode
        githubReleaseExitCode = $finalize.results.githubRelease.exitCode
        dirtyApplyExitCode = $finalize.results.dirtyApply.exitCode
    }
    $script:results.packagePublish = [ordered]@{
        succeeded = $packagePublish.succeeded
        assertionCount = @($packagePublish.assertions).Count
        dryRunExitCode = $packagePublish.results.dryRun.exitCode
        applyExitCode = $packagePublish.results.apply.exitCode
        packageCount = $packagePublish.results.apply.packageCount
        dirtyApplyExitCode = $packagePublish.results.dirtyApply.exitCode
    }
    $script:results.provenance = [ordered]@{
        succeeded = $provenance.succeeded
        assertionCount = @($provenance.assertions).Count
        provenanceDryRunExitCode = $provenance.results.provenanceDryRun.exitCode
        provenanceApplyExitCode = $provenance.results.provenanceApply.exitCode
        signDryRunExitCode = $provenance.results.signDryRun.exitCode
        signApplyExitCode = $provenance.results.signApply.exitCode
        signNoCertApplyExitCode = $provenance.results.signNoCertApply.exitCode
    }

    Assert-Equal -Name 'plan schema version' -Actual $plan.schemaVersion -Expected 1
    Assert-Equal -Name 'plan dry-run' -Actual $plan.dryRun -Expected $true
    Assert-Equal -Name 'plan release target' -Actual $plan.releaseTarget -Expected $ReleaseTarget
    Assert-Matches -Name 'plan next version semver' -Actual $plan.nextVersion -Pattern '^\d+\.\d+\.\d+$'
    Assert-Equal -Name 'plan version source' -Actual $plan.currentVersion.source -Expected 'msbuild'
    Assert-ContainsAll -Name 'plan command contract' -Actual (Get-Names -Items @($plan.plannedCommands)) -Expected @(
        'release-contract-smoke',
        'release-finalize-smoke',
        'release-package-publish-smoke',
        'release-provenance-smoke',
        'session-audit-script-smoke',
        'coding-agent-auth-migration-smoke',
        'coding-agent-session-migration-smoke',
        'coding-agent-commands-migration-smoke',
        'coding-agent-tools-to-bin-migration-smoke',
        'coding-agent-deprecated-extension-dirs-audit-smoke',
        'coding-agent-keybindings-migration-smoke',
        'edit-tool-stats-smoke',
        'mom-timestamp-migration-smoke',
        'coding-agent-startup-profile-smoke',
        'release-version-sync-smoke',
        'local-release-execution',
        'release-finalization',
        'release-package-publish',
        'release-provenance',
        'release-package-signing',
        'release-preparation',
        'release-validation',
        'version-update',
        'release-notes-update',
        'no-env-validation',
        'release-matrix-build'
    )
    Assert-TextContainsAll -Name 'plan mutation boundary' -Text (@($plan.nonExecutedMutations) -join [Environment]::NewLine) -ExpectedPatterns @(
        'commit',
        'tag',
        'Publish|publish',
        'Push|push'
    )

    Assert-Equal -Name 'prepare schema version' -Actual $prepare.schemaVersion -Expected 1
    Assert-Equal -Name 'prepare dry-run' -Actual $prepare.dryRun -Expected $true
    Assert-Equal -Name 'prepare not applied' -Actual $prepare.applied -Expected $false
    Assert-Equal -Name 'prepare next version matches plan' -Actual $prepare.nextVersion -Expected $plan.nextVersion
    Assert-ContainsAll -Name 'prepare changed file contract' -Actual @($prepare.changedFiles) -Expected @(
        'Directory.Build.props',
        'docs/releases/feature-release-notes.md'
    )
    Assert-Equal -Name 'prepare version helper dry-run' -Actual $prepare.helperResults.versionUpdate.dryRun -Expected $true
    Assert-Equal -Name 'prepare notes helper dry-run' -Actual $prepare.helperResults.releaseNotesUpdate.dryRun -Expected $true
    Assert-ContainsAll -Name 'prepare command contract' -Actual (Get-Names -Items @($prepare.plannedCommands)) -Expected @(
        'version-update',
        'release-notes-update',
        'diff-check',
        'no-env-validation',
        'release-matrix-build'
    )
    Assert-TextContainsAll -Name 'prepare remaining gap boundary' -Text (@($prepare.remainingGaps) -join [Environment]::NewLine) -ExpectedPatterns @(
        'commit',
        'tag',
        'publish',
        'push'
    )

    Assert-Equal -Name 'validate schema version' -Actual $validate.schemaVersion -Expected 1
    Assert-Equal -Name 'validate dry-run' -Actual $validate.dryRun -Expected $true
    Assert-Equal -Name 'validate not run' -Actual $validate.ran -Expected $false
    Assert-Equal -Name 'validate full local level' -Actual $validate.validationLevel -Expected 'full-local-dry-run'
    Assert-Equal -Name 'validate enabled count' -Actual $validate.enabledValidationCount -Expected 3
    Assert-Equal -Name 'validate skipped count' -Actual $validate.skippedValidationCount -Expected 0
    Assert-ContainsAll -Name 'validate command contract' -Actual (Get-Names -Items @($validate.plannedCommands)) -Expected @(
        'diff-check',
        'no-env-validation',
        'release-matrix-build'
    )
    $disabledCommands = @(@($validate.plannedCommands) | Where-Object { -not $_.enabled })
    Assert-Equal -Name 'validate default has no disabled commands' -Actual $disabledCommands.Count -Expected 0

    Assert-Equal -Name 'minimal validate dry-run' -Actual $minimalValidate.dryRun -Expected $true
    Assert-Equal -Name 'minimal validate level' -Actual $minimalValidate.validationLevel -Expected 'minimal-diff-only-dry-run'
    Assert-Equal -Name 'minimal validate enabled count' -Actual $minimalValidate.enabledValidationCount -Expected 1
    Assert-Equal -Name 'minimal validate skipped count' -Actual $minimalValidate.skippedValidationCount -Expected 2
    Assert-ContainsAll -Name 'minimal validate skipped names' -Actual @($minimalValidate.skippedValidationNames) -Expected @(
        'no-env-validation',
        'release-matrix-build'
    )
    $coverageWarning = @($minimalValidate.checks | Where-Object { $_.name -eq 'validation-coverage' -and $_.status -eq 'warning' })
    Assert-Equal -Name 'minimal validate coverage warning' -Actual $coverageWarning.Count -Expected 1

    Assert-Equal -Name 'execute schema version' -Actual $execute.schemaVersion -Expected 1
    Assert-Equal -Name 'execute dry-run' -Actual $execute.dryRun -Expected $true
    Assert-Equal -Name 'execute not applied' -Actual $execute.applied -Expected $false
    Assert-Equal -Name 'execute next version matches plan' -Actual $execute.nextVersion -Expected $plan.nextVersion
    Assert-ContainsAll -Name 'execute local mutation contract' -Actual (Get-Names -Items @($execute.localMutationPlan)) -Expected @(
        'release-contract-smoke',
        'release-preparation-apply',
        'release-validation-run',
        'release-stage',
        'release-commit',
        'release-tag'
    )
    Assert-TextContainsAll -Name 'execute non-executed boundary' -Text (@($execute.nonExecutedMutations) -join [Environment]::NewLine) -ExpectedPatterns @(
        'Publish|publish',
        'Push|push',
        'external'
    )

    Assert-Equal -Name 'release finalize smoke succeeded' -Actual $finalize.succeeded -Expected $true
    Assert-Equal -Name 'release finalize dry-run exit code' -Actual $finalize.results.dryRun.exitCode -Expected 0
    Assert-Equal -Name 'release finalize apply exit code' -Actual $finalize.results.apply.exitCode -Expected 0
    Assert-Equal -Name 'release finalize github release exit code' -Actual $finalize.results.githubRelease.exitCode -Expected 0
    Assert-Equal -Name 'release finalize dirty apply blocked' -Actual ($finalize.results.dirtyApply.exitCode -ne 0) -Expected $true

    Assert-Equal -Name 'release package publish smoke succeeded' -Actual $packagePublish.succeeded -Expected $true
    Assert-Equal -Name 'release package publish dry-run exit code' -Actual $packagePublish.results.dryRun.exitCode -Expected 0
    Assert-Equal -Name 'release package publish apply exit code' -Actual $packagePublish.results.apply.exitCode -Expected 0
    Assert-Equal -Name 'release package publish default package count' -Actual $packagePublish.results.apply.packageCount -Expected 3
    Assert-Equal -Name 'release package publish dirty apply blocked' -Actual ($packagePublish.results.dirtyApply.exitCode -ne 0) -Expected $true

    Assert-Equal -Name 'release provenance smoke succeeded' -Actual $provenance.succeeded -Expected $true
    Assert-Equal -Name 'release provenance dry-run exit code' -Actual $provenance.results.provenanceDryRun.exitCode -Expected 0
    Assert-Equal -Name 'release provenance apply exit code' -Actual $provenance.results.provenanceApply.exitCode -Expected 0
    Assert-Equal -Name 'release provenance dirty apply blocked' -Actual ($provenance.results.provenanceDirtyApply.exitCode -ne 0) -Expected $true
    Assert-Equal -Name 'release package signing dry-run exit code' -Actual $provenance.results.signDryRun.exitCode -Expected 0
    Assert-Equal -Name 'release package signing apply exit code' -Actual $provenance.results.signApply.exitCode -Expected 0
    Assert-Equal -Name 'release package signing no certificate apply blocked' -Actual ($provenance.results.signNoCertApply.exitCode -ne 0) -Expected $true

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        releaseTarget = $ReleaseTarget
        runtimes = $normalizedRuntimes
        results = $script:results
        assertions = $script:assertions
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release contract smoke passed'
        Write-Host "  target: $ReleaseTarget"
        Write-Host "  next version: $($plan.nextVersion)"
        Write-Host "  runtimes: $($normalizedRuntimes -join ', ')"
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  validation level: $($validate.validationLevel)"
        Write-Host "  minimal validation level: $($minimalValidate.validationLevel)"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        releaseTarget = $ReleaseTarget
        runtimes = @($Runtimes)
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release contract smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
