param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ReleaseTarget,
    [string]$CurrentVersion = '',
    [string]$Configuration = 'Release',
    [string[]]$Runtimes = @('osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64', 'win-x64'),
    [switch]$AllowDirty,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$bumpTypes = @('major', 'minor', 'patch')
$semverPattern = '^\d+\.\d+\.\d+$'
$script:checks = @()
$script:warnings = @()
$script:hardPreflightFailure = $false

function Convert-ToRepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($script:repoRoot)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $rootPrefix = $rootFull.TrimEnd($separator) + $separator

    if ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $rootUri = [System.Uri]::new($rootPrefix)
        $pathUri = [System.Uri]::new($fullPath)
        return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', $separator)
    }

    return $fullPath
}

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
}

function Test-SemVer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return $Value -match $script:semverPattern
}

function Compare-SemVer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$A,
        [Parameter(Mandatory = $true)]
        [string]$B
    )

    $aParts = $A.Split('.') | ForEach-Object { [int]$_ }
    $bParts = $B.Split('.') | ForEach-Object { [int]$_ }

    for ($i = 0; $i -lt 3; $i++) {
        $diff = $aParts[$i] - $bParts[$i]
        if ($diff -ne 0) {
            return $diff
        }
    }

    return 0
}

function Get-BumpedVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [ValidateSet('major', 'minor', 'patch')]
        [string]$Bump
    )

    $parts = $Version.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        'major' { return "$($parts[0] + 1).0.0" }
        'minor' { return "$($parts[0]).$($parts[1] + 1).0" }
        'patch' { return "$($parts[0]).$($parts[1]).$($parts[2] + 1)" }
        default { throw "Unsupported bump type: $Bump" }
    }
}

function Get-VersionProperties {
    $candidatePaths = @()
    foreach ($fileName in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        $path = Join-Path $script:repoRoot $fileName
        if (Test-Path -LiteralPath $path) {
            $candidatePaths += (Get-Item -LiteralPath $path)
        }
    }

    $candidatePaths += Get-ChildItem -LiteralPath $script:repoRoot -Filter '*.csproj' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' }

    $propertyNames = @('Version', 'VersionPrefix', 'PackageVersion', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')
    $properties = @()

    foreach ($path in $candidatePaths) {
        try {
            [xml]$xml = Get-Content -LiteralPath $path.FullName -Raw
        }
        catch {
            $script:warnings += "Could not parse MSBuild XML file $(Convert-ToRepoRelativePath -Path $path.FullName): $($_.Exception.Message)"
            continue
        }

        foreach ($propertyName in $propertyNames) {
            $nodes = $xml.SelectNodes("//*[local-name()='$propertyName']")
            foreach ($node in $nodes) {
                $value = ''
                if ($null -ne $node.InnerText) {
                    $value = $node.InnerText.Trim()
                }
                if ([string]::IsNullOrWhiteSpace($value)) {
                    continue
                }

                $properties += [ordered]@{
                    path = Convert-ToRepoRelativePath -Path $path.FullName
                    property = $propertyName
                    value = $value
                    semver = (Test-SemVer -Value $value)
                }
            }
        }
    }

    return @($properties)
}

function Select-CurrentVersion {
    param(
        [object[]]$VersionProperties
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentVersion)) {
        if (-not (Test-SemVer -Value $CurrentVersion)) {
            throw "CurrentVersion must use x.y.z semver format. Actual: $CurrentVersion"
        }

        return [ordered]@{
            value = $CurrentVersion
            source = 'parameter'
            path = ''
            property = ''
            detectedProperties = $VersionProperties
            status = 'provided'
        }
    }

    $semanticProperties = @($VersionProperties | Where-Object { $_.semver })
    if ($semanticProperties.Count -eq 0) {
        return [ordered]@{
            value = $null
            source = 'none'
            path = ''
            property = ''
            detectedProperties = $VersionProperties
            status = 'missing'
        }
    }

    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $candidates = @($semanticProperties | Where-Object { $_.property -eq $propertyName })
        if ($candidates.Count -eq 0) {
            continue
        }

        $uniqueValues = @($candidates | ForEach-Object { $_.value } | Sort-Object -Unique)
        if ($uniqueValues.Count -eq 1) {
            $chosen = $candidates[0]
            return [ordered]@{
                value = $chosen.value
                source = 'msbuild'
                path = $chosen.path
                property = $chosen.property
                detectedProperties = $VersionProperties
                status = 'detected'
            }
        }

        return [ordered]@{
            value = $null
            source = 'msbuild'
            path = ''
            property = $propertyName
            detectedProperties = $VersionProperties
            status = 'ambiguous'
        }
    }

    $uniqueSemanticValues = @($semanticProperties | ForEach-Object { $_.value } | Sort-Object -Unique)
    if ($uniqueSemanticValues.Count -eq 1) {
        $chosen = $semanticProperties[0]
        return [ordered]@{
            value = $chosen.value
            source = 'msbuild'
            path = $chosen.path
            property = $chosen.property
            detectedProperties = $VersionProperties
            status = 'detected'
        }
    }

    return [ordered]@{
        value = $null
        source = 'msbuild'
        path = ''
        property = ''
        detectedProperties = $VersionProperties
        status = 'ambiguous'
    }
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

$isBumpTarget = $bumpTypes -contains $ReleaseTarget
$isExplicitVersionTarget = Test-SemVer -Value $ReleaseTarget
if (-not $isBumpTarget -and -not $isExplicitVersionTarget) {
    throw "Usage: powershell -File .\scripts\plan-release.ps1 <major|minor|patch|x.y.z> [-CurrentVersion x.y.z] [-AllowDirty] [-Json]"
}

$normalizedRuntimes = @($Runtimes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($normalizedRuntimes.Count -eq 0) {
    throw 'At least one runtime identifier is required.'
}

$versionProperties = Get-VersionProperties
$currentVersionInfo = Select-CurrentVersion -VersionProperties $versionProperties
$nextVersion = $null
$versionPlanStatus = 'ready'

if ($isBumpTarget) {
    if ($currentVersionInfo.value) {
        $nextVersion = Get-BumpedVersion -Version $currentVersionInfo.value -Bump $ReleaseTarget
    }
    else {
        $versionPlanStatus = 'blocked'
    }
}
else {
    $nextVersion = $ReleaseTarget
    if ($currentVersionInfo.value -and (Compare-SemVer -A $nextVersion -B $currentVersionInfo.value) -le 0) {
        $versionPlanStatus = 'blocked'
    }
    elseif (-not $currentVersionInfo.value) {
        $versionPlanStatus = 'comparison-unchecked'
    }
}

Add-Check -Name 'release-target' -Status 'passed' -Detail "Release target '$ReleaseTarget' accepted; this script remains dry-run only."

if ($currentVersionInfo.status -eq 'missing') {
    $status = if ($isBumpTarget) { 'blocked' } else { 'warning' }
    Add-Check -Name 'version-source' -Status $status -Detail 'No explicit Version/VersionPrefix/PackageVersion was found in Tau MSBuild files; use -CurrentVersion or add a repo-owned version source before executing a real release.'
    if ($isBumpTarget) {
        $script:hardPreflightFailure = $true
    }
}
elseif ($currentVersionInfo.status -eq 'ambiguous') {
    Add-Check -Name 'version-source' -Status 'blocked' -Detail 'Multiple semantic version properties were found or the version source is ambiguous; choose one repo-owned version source before executing a real release.'
    $script:hardPreflightFailure = $true
}
else {
    if ($currentVersionInfo.source -eq 'parameter') {
        Add-Check -Name 'version-source' -Status 'passed' -Detail "Current version $($currentVersionInfo.value) from -CurrentVersion."
    }
    else {
        Add-Check -Name 'version-source' -Status 'passed' -Detail "Current version $($currentVersionInfo.value) from $($currentVersionInfo.path) $($currentVersionInfo.property)."
    }
}

if ($versionPlanStatus -eq 'blocked') {
    if ($isExplicitVersionTarget -and $currentVersionInfo.value) {
        Add-Check -Name 'next-version' -Status 'blocked' -Detail "Explicit version $nextVersion must be greater than current version $($currentVersionInfo.value)."
    }
    elseif ($isBumpTarget) {
        Add-Check -Name 'next-version' -Status 'blocked' -Detail "Cannot compute '$ReleaseTarget' bump without a current semantic version."
    }
    $script:hardPreflightFailure = $true
}
elseif ($versionPlanStatus -eq 'comparison-unchecked') {
    Add-Check -Name 'next-version' -Status 'warning' -Detail "Planned explicit version $nextVersion, but comparison against current Tau version was not possible because no repo-owned version source exists."
}
else {
    Add-Check -Name 'next-version' -Status 'passed' -Detail "Planned next version: $nextVersion."
}

$gitStatus = Get-GitStatus
if (-not $gitStatus.available) {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Could not read git status: $($gitStatus.error)"
    $script:hardPreflightFailure = $true
}
elseif ($gitStatus.clean) {
    Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean.'
}
elseif ($AllowDirty) {
    Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s), allowed for planning because -AllowDirty was supplied."
}
else {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Commit or stash first, or pass -AllowDirty for planning-only runs."
    $script:hardPreflightFailure = $true
}

$releaseNotesPath = Join-Path $repoRoot 'docs/releases/feature-release-notes.md'
if (Test-Path -LiteralPath $releaseNotesPath) {
    Add-Check -Name 'release-notes' -Status 'passed' -Detail 'Tau release notes source exists at docs/releases/feature-release-notes.md.'
}
else {
    Add-Check -Name 'release-notes' -Status 'blocked' -Detail 'Tau release notes source is missing at docs/releases/feature-release-notes.md.'
    $script:hardPreflightFailure = $true
}

$requiredScripts = @(
    'scripts/verify-no-env.ps1',
    'scripts/build-release-artifacts.ps1',
    'scripts/build-release-matrix.ps1',
    'scripts/package-release-artifacts.ps1',
    'scripts/package-release-matrix.ps1',
    'scripts/smoke-release-artifacts.ps1'
)
$missingScripts = @($requiredScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_)) })
if ($missingScripts.Count -eq 0) {
    Add-Check -Name 'release-scripts' -Status 'passed' -Detail 'Release build, package, smoke and no-env scripts are present.'
}
else {
    Add-Check -Name 'release-scripts' -Status 'blocked' -Detail "Missing release script(s): $($missingScripts -join ', ')"
    $script:hardPreflightFailure = $true
}

$runtimeArgument = ($normalizedRuntimes -join ',')
$versionToken = if ([string]::IsNullOrWhiteSpace($nextVersion)) { '<next-version>' } else { $nextVersion }
$tagToken = "v$versionToken"

$plannedCommands = @(
    [ordered]@{
        name = 'diff-check'
        command = 'git diff --check'
        purpose = 'Validate whitespace and patch hygiene before release preparation.'
    },
    [ordered]@{
        name = 'no-env-validation'
        command = 'powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore -RunSmoke'
        purpose = 'Run the current Tau project gate under provider/auth-isolated child-process environment.'
    },
    [ordered]@{
        name = 'release-matrix-build'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-matrix.ps1 -Configuration $Configuration -Runtimes $runtimeArgument"
        purpose = 'Build and package the configured RID matrix with existing release artifact scripts.'
    },
    [ordered]@{
        name = 'release-matrix-package-only'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release-matrix.ps1 -Runtimes $runtimeArgument"
        purpose = 'Re-package already-built artifacts when reusing an existing artifacts/tau-<rid> directory.'
    }
)

$upstreamReleaseMapping = @(
    [ordered]@{
        upstreamStep = 'clean worktree check'
        tauPlan = 'plan-release.ps1 reads git status; real release remains blocked unless clean or -AllowDirty is explicitly used for planning.'
        state = 'planned'
    },
    [ordered]@{
        upstreamStep = 'bump or set version'
        tauPlan = 'dry-run computes the requested next version only when a current version is available or an explicit x.y.z target is supplied.'
        state = $versionPlanStatus
    },
    [ordered]@{
        upstreamStep = 'update changelogs'
        tauPlan = 'Tau uses docs/releases/feature-release-notes.md plus docs/histories; this script lists the required mutation but does not edit files.'
        state = 'planned-only'
    },
    [ordered]@{
        upstreamStep = 'commit and tag'
        tauPlan = "Would use release commit and tag $tagToken after validation, but this script never runs git commit or git tag."
        state = 'planned-only'
    },
    [ordered]@{
        upstreamStep = 'publish'
        tauPlan = 'Would publish/upload verified Tau release archives after an explicit future release command; this script never publishes.'
        state = 'planned-only'
    },
    [ordered]@{
        upstreamStep = 'push main and tag'
        tauPlan = 'Would push only from a future explicit release execution script or manual operator action; this script never pushes.'
        state = 'planned-only'
    }
)

$nonExecutedMutations = @(
    "Set or update Tau repo-owned version source to $versionToken.",
    'Update docs/releases/feature-release-notes.md release section and keep docs/histories/YYYY-MM synchronized.',
    "Create release commit, e.g. git commit -m `"Release $tagToken`".",
    "Create tag $tagToken.",
    'Publish/upload verified release archives after external e2e decisions are satisfied.',
    "Push main and $tagToken only after explicit release approval."
)

$remainingGaps = @(
    'No repo-owned Tau version source is currently defined unless -CurrentVersion is supplied or a future Version property is added.',
    'This is a dry-run planner; it does not bump versions, edit release notes, commit, tag, publish or push.',
    'Real non-host runner executable smoke and external provider/Slack/Docker/SSH/HF/GPU/vLLM release e2e remain open.',
    'Exact Unix release wrapper/auth-backup parity and upstream examples/Photon/interactive asset payload parity remain open.'
)

$releasePlan = [ordered]@{
    schemaVersion = 1
    generatedAt = (Get-Date).ToUniversalTime().ToString('O')
    dryRun = $true
    releaseTarget = $ReleaseTarget
    nextVersion = $nextVersion
    configuration = $Configuration
    runtimes = $normalizedRuntimes
    currentVersion = $currentVersionInfo
    workingTree = [ordered]@{
        clean = $gitStatus.clean
        allowDirty = $AllowDirty.IsPresent
        statusCount = @($gitStatus.entries).Count
        status = $gitStatus.entries
    }
    checks = $script:checks
    plannedCommands = $plannedCommands
    upstreamReleaseMapping = $upstreamReleaseMapping
    nonExecutedMutations = $nonExecutedMutations
    remainingGaps = $remainingGaps
}

if ($Json) {
    $releasePlan | ConvertTo-Json -Depth 12
}
else {
    Write-Host 'Tau release plan (dry-run)'
    Write-Host "  target: $ReleaseTarget"
    Write-Host "  current version: $(if ($currentVersionInfo.value) { $currentVersionInfo.value } else { '<not found>' })"
    Write-Host "  next version: $(if ($nextVersion) { $nextVersion } else { '<unresolved>' })"
    Write-Host "  runtimes: $($normalizedRuntimes -join ', ')"
    Write-Host "  working tree: $(if ($gitStatus.clean) { 'clean' } elseif ($AllowDirty) { 'dirty (allowed)' } else { 'dirty (blocked)' })"
    Write-Host ''

    Write-Host 'Checks:'
    foreach ($check in $script:checks) {
        Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
    }
    Write-Host ''

    Write-Host 'Commands to run before an actual release:'
    foreach ($command in $plannedCommands) {
        Write-Host "  - $($command.command)"
    }
    Write-Host ''

    Write-Host 'Release mutations deliberately not executed:'
    foreach ($mutation in $nonExecutedMutations) {
        Write-Host "  - $mutation"
    }
    Write-Host ''

    Write-Host 'Remaining gaps:'
    foreach ($gap in $remainingGaps) {
        Write-Host "  - $gap"
    }
}

if ($script:hardPreflightFailure) {
    exit 1
}
