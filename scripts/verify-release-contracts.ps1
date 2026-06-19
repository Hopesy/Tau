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

function Invoke-JsonScriptAllowFailure {
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

    try {
        $json = $outputText | ConvertFrom-Json
    }
    catch {
        throw "$Name did not return valid JSON. Output: $outputText"
    }

    return [ordered]@{
        exitCode = $exitCode
        json = $json
        raw = $outputText
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
    $aiCliToolInstall = Invoke-JsonScript -Name 'ai-cli-tool-install-smoke' -ScriptPath 'scripts/verify-ai-cli-tool-install.ps1' -Arguments @('-SkipRestore', '-Json')
    $agentPackageConsumer = Invoke-JsonScript -Name 'agent-package-consumer-smoke' -ScriptPath 'scripts/verify-agent-package-consumer.ps1' -Arguments @('-SkipRestore', '-Json')
    $agentProxyServerE2e = Invoke-JsonScript -Name 'agent-proxy-server-e2e-smoke' -ScriptPath 'scripts/verify-agent-proxy-server-e2e.ps1' -Arguments @('-SkipRestore', '-Json')
    $aiProviderOauthMatrix = Invoke-JsonScript -Name 'ai-provider-oauth-matrix-smoke' -ScriptPath 'scripts/verify-ai-provider-e2e-matrix.ps1' -Arguments @('-Isolated', '-Json')
    $aiProviderOauthMatrixRequired = Invoke-JsonScriptAllowFailure -Name 'ai-provider-oauth-matrix-required-guard' -ScriptPath 'scripts/verify-ai-provider-e2e-matrix.ps1' -Arguments @('-RunConfigured', '-RequireConfigured', '-Isolated', '-Json')
    $aiAgentExportShape = Invoke-JsonScript -Name 'ai-agent-export-shape-smoke' -ScriptPath 'scripts/verify-ai-agent-export-shape.ps1' -Arguments @('-Json')
    $provenance = Invoke-JsonScript -Name 'release-provenance-smoke' -ScriptPath 'scripts/verify-release-provenance.ps1' -Arguments @('-Json')
    $aiTestImage = Invoke-JsonScript -Name 'ai-test-image-smoke' -ScriptPath 'scripts/verify-ai-test-image.ps1' -Arguments @('-Json')

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
        toolPackageCount = $packagePublish.results.apply.toolPackageCount
        dirtyApplyExitCode = $packagePublish.results.dirtyApply.exitCode
    }
    $script:results.aiCliToolInstall = [ordered]@{
        succeeded = $aiCliToolInstall.succeeded
        assertionCount = @($aiCliToolInstall.assertions).Count
        version = $aiCliToolInstall.version
    }
    $script:results.agentPackageConsumer = [ordered]@{
        succeeded = $agentPackageConsumer.succeeded
        assertionCount = @($agentPackageConsumer.assertions).Count
        aiRestoreExitCode = $agentPackageConsumer.results.aiConsumer.restoreExitCode
        aiBuildExitCode = $agentPackageConsumer.results.aiConsumer.buildExitCode
        aiRunExitCode = $agentPackageConsumer.results.aiConsumer.runExitCode
        agentRestoreExitCode = $agentPackageConsumer.results.agentConsumer.restoreExitCode
        agentBuildExitCode = $agentPackageConsumer.results.agentConsumer.buildExitCode
        agentRunExitCode = $agentPackageConsumer.results.agentConsumer.runExitCode
    }
    $script:results.agentProxyServerE2e = [ordered]@{
        succeeded = $agentProxyServerE2e.succeeded
        assertionCount = @($agentProxyServerE2e.assertions).Count
        exitCode = $agentProxyServerE2e.exitCode
        filter = $agentProxyServerE2e.filter
    }
    $script:results.aiProviderOauthMatrix = [ordered]@{
        succeeded = $aiProviderOauthMatrix.succeeded
        assertionCount = @($aiProviderOauthMatrix.assertions).Count
        mode = $aiProviderOauthMatrix.mode
        isolated = $aiProviderOauthMatrix.isolated
        requireConfigured = $aiProviderOauthMatrix.requireConfigured
        realE2eSatisfied = $aiProviderOauthMatrix.realE2eSatisfied
        completionStatus = $aiProviderOauthMatrix.completionStatus
        providerCount = $aiProviderOauthMatrix.providerCount
        configuredProviderCount = $aiProviderOauthMatrix.results.summary.configuredProviderCount
        openProviderCount = $aiProviderOauthMatrix.results.summary.openProviderCount
    }
    $script:results.aiProviderOauthMatrixRequired = [ordered]@{
        succeeded = $aiProviderOauthMatrixRequired.json.succeeded
        assertionCount = @($aiProviderOauthMatrixRequired.json.assertions).Count
        exitCode = $aiProviderOauthMatrixRequired.exitCode
        mode = $aiProviderOauthMatrixRequired.json.mode
        isolated = $aiProviderOauthMatrixRequired.json.isolated
        runConfigured = $aiProviderOauthMatrixRequired.json.runConfigured
        requireConfigured = $aiProviderOauthMatrixRequired.json.requireConfigured
        configuredProviderCount = $aiProviderOauthMatrixRequired.json.results.summary.configuredProviderCount
        attemptedProviderCount = $aiProviderOauthMatrixRequired.json.results.summary.attemptedProviderCount
        completionStatus = $aiProviderOauthMatrixRequired.json.results.summary.completionStatus
        gateFailure = $aiProviderOauthMatrixRequired.json.results.summary.gateFailure
    }
    $script:results.aiAgentExportShape = [ordered]@{
        succeeded = $aiAgentExportShape.succeeded
        assertionCount = @($aiAgentExportShape.assertions).Count
        aiPackageExportCount = $aiAgentExportShape.results.aiPackageExportCount
        aiIndexExportCount = $aiAgentExportShape.results.aiIndexExportCount
        agentIndexExportCount = $aiAgentExportShape.results.agentIndexExportCount
        evidenceFileCount = $aiAgentExportShape.results.evidenceFileCount
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
    $script:results.aiTestImage = [ordered]@{
        succeeded = $aiTestImage.succeeded
        assertionCount = @($aiTestImage.assertions).Count
        sha256 = $aiTestImage.sha256
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
        'ai-cli-tool-install-smoke',
        'agent-package-consumer-smoke',
        'agent-proxy-server-e2e-smoke',
        'ai-provider-oauth-matrix-smoke',
        'ai-agent-export-shape-smoke',
        'release-provenance-smoke',
        'ai-test-image-smoke',
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
    Assert-Equal -Name 'release package publish default package count' -Actual $packagePublish.results.apply.packageCount -Expected 5
    Assert-Equal -Name 'release package publish default tool package count' -Actual $packagePublish.results.apply.toolPackageCount -Expected 2
    Assert-Equal -Name 'release package publish dirty apply blocked' -Actual ($packagePublish.results.dirtyApply.exitCode -ne 0) -Expected $true

    Assert-Equal -Name 'ai cli tool install smoke succeeded' -Actual $aiCliToolInstall.succeeded -Expected $true
    Assert-Matches -Name 'ai cli tool install version' -Actual $aiCliToolInstall.version -Pattern '^\d+\.\d+\.\d+$'

    Assert-Equal -Name 'agent package consumer smoke succeeded' -Actual $agentPackageConsumer.succeeded -Expected $true
    Assert-Equal -Name 'ai package consumer restore exit code' -Actual $agentPackageConsumer.results.aiConsumer.restoreExitCode -Expected 0
    Assert-Equal -Name 'ai package consumer build exit code' -Actual $agentPackageConsumer.results.aiConsumer.buildExitCode -Expected 0
    Assert-Equal -Name 'ai package consumer run exit code' -Actual $agentPackageConsumer.results.aiConsumer.runExitCode -Expected 0
    Assert-Matches -Name 'ai package consumer output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'assistant=ai package complete'
    Assert-Equal -Name 'agent package consumer restore exit code' -Actual $agentPackageConsumer.results.agentConsumer.restoreExitCode -Expected 0
    Assert-Equal -Name 'agent package consumer build exit code' -Actual $agentPackageConsumer.results.agentConsumer.buildExitCode -Expected 0
    Assert-Equal -Name 'agent package consumer run exit code' -Actual $agentPackageConsumer.results.agentConsumer.runExitCode -Expected 0
    Assert-Matches -Name 'agent package consumer output' -Actual $agentPackageConsumer.results.agentConsumer.output -Pattern 'assistant=package consumer complete'
    Assert-Matches -Name 'agent package consumer prepared tool output' -Actual $agentPackageConsumer.results.agentConsumer.output -Pattern 'prepared package consumer'
    Assert-Matches -Name 'ai package consumer configured status output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredStatus=models\.json:True'
    Assert-Matches -Name 'ai package consumer configured assistant output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAssistant=configured package consumer complete'
    Assert-Matches -Name 'ai package consumer configured api output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredApi=consumer-config-api'
    Assert-Matches -Name 'ai package consumer configured host output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredHost=consumer\.example\.test'
    Assert-Matches -Name 'ai package consumer configured auth output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAuth=Bearer consumer-dynamic-key'
    Assert-Matches -Name 'ai package consumer configured provider header output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredProviderHeader=explicit-provider-header'
    Assert-Matches -Name 'ai package consumer configured model header output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredModelHeader=model-header-value'
    Assert-Matches -Name 'ai package consumer configured explicit header output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredExplicitHeader=explicit-header'
    Assert-Matches -Name 'ai package consumer configured path output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredPath=/v1/chat/completions'
    Assert-Matches -Name 'ai package consumer configured options temperature output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsTemperature=0\.3'
    Assert-Matches -Name 'ai package consumer configured options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsMaxTokens=111'
    Assert-Matches -Name 'ai package consumer configured options top-p output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsTopP=0\.6'
    Assert-Matches -Name 'ai package consumer configured options transport output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsTransport=WebSocket'
    Assert-Matches -Name 'ai package consumer configured options cache output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsCacheRetention=Long'
    Assert-Matches -Name 'ai package consumer configured options session output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsSessionId=model-session'
    Assert-Matches -Name 'ai package consumer configured options retry output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsMaxRetryDelayMs=2500'
    Assert-Matches -Name 'ai package consumer configured options reasoning output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsReasoning=High'
    Assert-Matches -Name 'ai package consumer configured options thinking output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsThinkingHigh=400'
    Assert-Matches -Name 'ai package consumer configured options metadata shared output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsMetadataShared=model'
    Assert-Matches -Name 'ai package consumer configured options metadata model output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOptionsMetadataModelOnly=7'
    Assert-Matches -Name 'ai package consumer configured responses options type output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredResponsesOptionsType=OpenAiResponsesOptions'
    Assert-Matches -Name 'ai package consumer configured responses options service tier output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredResponsesOptionsServiceTier=flex'
    Assert-Matches -Name 'ai package consumer configured responses options reasoning effort output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredResponsesOptionsReasoningEffort=low'
    Assert-Matches -Name 'ai package consumer configured responses options reasoning summary output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredResponsesOptionsReasoningSummary=concise'
    Assert-Matches -Name 'ai package consumer configured responses options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredResponsesOptionsMaxTokens=111'
    Assert-Matches -Name 'ai package consumer configured openai options type output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsType=OpenAiOptions'
    Assert-Matches -Name 'ai package consumer configured openai options tool choice output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsToolChoice=function:consumer_openai_tool'
    Assert-Matches -Name 'ai package consumer configured openai options tool choice kind output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsToolChoiceKind=function'
    Assert-Matches -Name 'ai package consumer configured openai options tool choice function output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsToolChoiceFunction=consumer_openai_tool'
    Assert-Matches -Name 'ai package consumer configured openai options reasoning effort output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsReasoningEffort=high'
    Assert-Matches -Name 'ai package consumer configured openai options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredOpenAiOptionsMaxTokens=222'
    Assert-Matches -Name 'ai package consumer configured mistral options type output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsType=MistralOptions'
    Assert-Matches -Name 'ai package consumer configured mistral options tool choice output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsToolChoice=function:consumer_tool'
    Assert-Matches -Name 'ai package consumer configured mistral options tool choice kind output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsToolChoiceKind=function'
    Assert-Matches -Name 'ai package consumer configured mistral options tool choice function output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsToolChoiceFunction=consumer_tool'
    Assert-Matches -Name 'ai package consumer configured mistral options prompt mode output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsPromptMode=reasoning'
    Assert-Matches -Name 'ai package consumer configured mistral options reasoning effort output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsReasoningEffort=high'
    Assert-Matches -Name 'ai package consumer configured mistral options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredMistralOptionsMaxTokens=321'
    Assert-Matches -Name 'ai package consumer configured anthropic options type output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsType=AnthropicOptions'
    Assert-Matches -Name 'ai package consumer configured anthropic options thinking enabled output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsThinkingEnabled=True'
    Assert-Matches -Name 'ai package consumer configured anthropic options thinking budget output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsThinkingBudgetTokens=2345'
    Assert-Matches -Name 'ai package consumer configured anthropic options effort output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsEffort=high'
    Assert-Matches -Name 'ai package consumer configured anthropic options thinking display output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsThinkingDisplay=omitted'
    Assert-Matches -Name 'ai package consumer configured anthropic options interleaved thinking output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsInterleavedThinking=True'
    Assert-Matches -Name 'ai package consumer configured anthropic options tool choice output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsToolChoice=tool:read_file'
    Assert-Matches -Name 'ai package consumer configured anthropic options tool choice kind output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsToolChoiceKind=tool'
    Assert-Matches -Name 'ai package consumer configured anthropic options tool choice name output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsToolChoiceName=read_file'
    Assert-Matches -Name 'ai package consumer configured anthropic options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredAnthropicOptionsMaxTokens=654'
    Assert-Matches -Name 'ai package consumer configured bedrock options type output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsType=BedrockOptions'
    Assert-Matches -Name 'ai package consumer configured bedrock options region output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsRegion=us-west-2'
    Assert-Matches -Name 'ai package consumer configured bedrock options profile output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsProfile=consumer-profile'
    Assert-Matches -Name 'ai package consumer configured bedrock options bearer token output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsBearerToken=consumer-bedrock-token'
    Assert-Matches -Name 'ai package consumer configured bedrock options tool choice output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsToolChoice=tool:read_file'
    Assert-Matches -Name 'ai package consumer configured bedrock options reasoning output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsReasoning=High'
    Assert-Matches -Name 'ai package consumer configured bedrock options thinking budget output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsThinkingBudgetTokens=3456'
    Assert-Matches -Name 'ai package consumer configured bedrock options thinking display output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsThinkingDisplay=omitted'
    Assert-Matches -Name 'ai package consumer configured bedrock options interleaved thinking output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsInterleavedThinking=True'
    Assert-Matches -Name 'ai package consumer configured bedrock options request metadata output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsRequestMetadataApp=consumer'
    Assert-Matches -Name 'ai package consumer configured bedrock options max tokens output' -Actual $agentPackageConsumer.results.aiConsumer.output -Pattern 'configuredBedrockOptionsMaxTokens=876'

    Assert-Equal -Name 'agent proxy server e2e smoke succeeded' -Actual $agentProxyServerE2e.succeeded -Expected $true
    Assert-Equal -Name 'agent proxy server e2e exit code' -Actual $agentProxyServerE2e.exitCode -Expected 0
    Assert-Matches -Name 'agent proxy server e2e filter' -Actual $agentProxyServerE2e.filter -Pattern 'ProxyStreamProviderTests'

    Assert-Equal -Name 'ai provider oauth matrix smoke succeeded' -Actual $aiProviderOauthMatrix.succeeded -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix mode' -Actual $aiProviderOauthMatrix.mode -Expected 'inspect'
    Assert-Equal -Name 'ai provider oauth matrix isolated' -Actual $aiProviderOauthMatrix.isolated -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix require configured default' -Actual $aiProviderOauthMatrix.requireConfigured -Expected $false
    Assert-Equal -Name 'ai provider oauth matrix real e2e default' -Actual $aiProviderOauthMatrix.realE2eSatisfied -Expected $false
    Assert-Equal -Name 'ai provider oauth matrix completion default' -Actual $aiProviderOauthMatrix.completionStatus -Expected 'inspect-only'
    Assert-Equal -Name 'ai provider oauth matrix provider count' -Actual $aiProviderOauthMatrix.providerCount -Expected 11
    Assert-Equal -Name 'ai provider oauth matrix configured count' -Actual $aiProviderOauthMatrix.results.summary.configuredProviderCount -Expected 0
    Assert-Equal -Name 'ai provider oauth matrix attempted count' -Actual $aiProviderOauthMatrix.results.summary.attemptedProviderCount -Expected 0
    Assert-Equal -Name 'ai provider oauth matrix succeeded count' -Actual $aiProviderOauthMatrix.results.summary.succeededProviderCount -Expected 0
    Assert-Equal -Name 'ai provider oauth matrix raw succeeded flag' -Actual $aiProviderOauthMatrix.results.summary.succeeded -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix open providers minimum' -Actual ($aiProviderOauthMatrix.results.summary.openProviderCount -ge 1) -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix required guard blocks empty credentials' -Actual ($aiProviderOauthMatrixRequired.exitCode -ne 0) -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix required guard succeeded flag' -Actual $aiProviderOauthMatrixRequired.json.succeeded -Expected $false
    Assert-Equal -Name 'ai provider oauth matrix required guard mode' -Actual $aiProviderOauthMatrixRequired.json.mode -Expected 'run-configured'
    Assert-Equal -Name 'ai provider oauth matrix required guard isolated' -Actual $aiProviderOauthMatrixRequired.json.isolated -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix required guard switch' -Actual $aiProviderOauthMatrixRequired.json.requireConfigured -Expected $true
    Assert-Equal -Name 'ai provider oauth matrix required guard configured count' -Actual $aiProviderOauthMatrixRequired.json.results.summary.configuredProviderCount -Expected 0
    Assert-Equal -Name 'ai provider oauth matrix required guard attempted count' -Actual $aiProviderOauthMatrixRequired.json.results.summary.attemptedProviderCount -Expected 0
    Assert-Equal -Name 'ai provider oauth matrix required guard completion status' -Actual $aiProviderOauthMatrixRequired.json.results.summary.completionStatus -Expected 'no-configured-providers'
    Assert-Matches -Name 'ai provider oauth matrix required guard failure text' -Actual $aiProviderOauthMatrixRequired.json.results.summary.gateFailure -Pattern 'No configured providers'

    Assert-Equal -Name 'ai agent export shape smoke succeeded' -Actual $aiAgentExportShape.succeeded -Expected $true
    Assert-Equal -Name 'ai agent export shape AI package export count' -Actual $aiAgentExportShape.results.aiPackageExportCount -Expected 12
    Assert-Equal -Name 'ai agent export shape AI index export count' -Actual $aiAgentExportShape.results.aiIndexExportCount -Expected 13
    Assert-Equal -Name 'ai agent export shape Agent index export count' -Actual $aiAgentExportShape.results.agentIndexExportCount -Expected 4

    Assert-Equal -Name 'release provenance smoke succeeded' -Actual $provenance.succeeded -Expected $true
    Assert-Equal -Name 'release provenance dry-run exit code' -Actual $provenance.results.provenanceDryRun.exitCode -Expected 0
    Assert-Equal -Name 'release provenance apply exit code' -Actual $provenance.results.provenanceApply.exitCode -Expected 0
    Assert-Equal -Name 'release provenance dirty apply blocked' -Actual ($provenance.results.provenanceDirtyApply.exitCode -ne 0) -Expected $true
    Assert-Equal -Name 'release package signing dry-run exit code' -Actual $provenance.results.signDryRun.exitCode -Expected 0
    Assert-Equal -Name 'release package signing apply exit code' -Actual $provenance.results.signApply.exitCode -Expected 0
    Assert-Equal -Name 'release package signing no certificate apply blocked' -Actual ($provenance.results.signNoCertApply.exitCode -ne 0) -Expected $true
    Assert-Equal -Name 'ai test image smoke succeeded' -Actual $aiTestImage.succeeded -Expected $true
    Assert-Matches -Name 'ai test image sha256 shape' -Actual $aiTestImage.sha256 -Pattern '^[0-9a-f]{64}$'

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
