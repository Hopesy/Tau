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

function Convert-ToDisplayPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($repoRoot)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $rootPrefix = $rootFull.TrimEnd($separator) + $separator

    if ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $rootUri = [System.Uri]::new($rootPrefix)
        $pathUri = [System.Uri]::new($fullPath)
        return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', $separator)
    }

    return $fullPath
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

function Get-VersionSource {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw 'Directory.Build.props not found; release preparation requires a repo-owned version source.'
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $properties = @()

    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $nodes = @($props.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
                continue
            }

            $value = $node.InnerText.Trim()
            if (-not (Test-SemVer -Value $value)) {
                throw "Release version source $propertyName must use x.y.z format. Actual: $value"
            }

            $properties += [ordered]@{
                path = Convert-ToDisplayPath -Path $propsPath
                property = $propertyName
                value = $value
            }
        }
    }

    if ($properties.Count -eq 0) {
        throw 'No repo-owned release version source was found. Define Version, VersionPrefix or PackageVersion before preparing releases.'
    }

    if ($properties.Count -gt 1) {
        $sources = @($properties | ForEach-Object { "$($_.property)=$($_.value)" })
        throw "Multiple release version sources found: $($sources -join ', '). Keep one repo-owned version source before preparing releases."
    }

    return $properties[0]
}

function Resolve-NextVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Target,
        [Parameter(Mandatory = $true)]
        [string]$CurrentVersion
    )

    if ($bumpTypes -contains $Target) {
        return Get-BumpedVersion -Version $CurrentVersion -Bump $Target
    }

    if (Test-SemVer -Value $Target) {
        if ((Compare-SemVer -A $Target -B $CurrentVersion) -le 0) {
            throw "Explicit version $Target must be greater than current version $CurrentVersion."
        }

        return $Target
    }

    throw "Usage: powershell -File .\scripts\prepare-release.ps1 <major|minor|patch|x.y.z> [-Apply] [-Json]"
}

function Invoke-ReleaseHelper {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Release helper failed: $ScriptPath $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine)
}

function ConvertFrom-HelperJson {
    param(
        [AllowNull()]
        [string]$JsonText
    )

    if ([string]::IsNullOrWhiteSpace($JsonText)) {
        return $null
    }

    return $JsonText | ConvertFrom-Json
}

$isBumpTarget = $bumpTypes -contains $ReleaseTarget
$isExplicitVersionTarget = Test-SemVer -Value $ReleaseTarget
if (-not $isBumpTarget -and -not $isExplicitVersionTarget) {
    throw "Usage: powershell -File .\scripts\prepare-release.ps1 <major|minor|patch|x.y.z> [-Apply] [-Json]"
}

$normalizedRuntimes = @($Runtimes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($normalizedRuntimes.Count -eq 0) {
    throw 'At least one runtime identifier is required.'
}

if (-not [string]::IsNullOrWhiteSpace($Date)) {
    if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "Date must use yyyy-MM-dd format. Actual: $Date"
    }

    [void][datetime]::ParseExact($Date, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
}

Add-Check -Name 'release-target' -Status 'passed' -Detail "Release target '$ReleaseTarget' accepted."

$versionSource = Get-VersionSource
$nextVersion = Resolve-NextVersion -Target $ReleaseTarget -CurrentVersion $versionSource.value
$releaseTag = "v$nextVersion"
Add-Check -Name 'version-source' -Status 'passed' -Detail "Current version $($versionSource.value) from $($versionSource.path) $($versionSource.property)."
Add-Check -Name 'next-version' -Status 'passed' -Detail "Prepared next version: $nextVersion."

$gitStatus = Get-GitStatus
if (-not $gitStatus.available) {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Could not read git status: $($gitStatus.error)"
}
elseif ($gitStatus.clean) {
    Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean.'
}
elseif ($Apply -or -not $AllowDirty) {
    Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Release preparation apply requires a clean worktree; dry-run can pass -AllowDirty."
}
else {
    Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s), allowed for dry-run because -AllowDirty was supplied."
}

$releaseNotesPath = Join-Path $repoRoot 'docs/releases/feature-release-notes.md'
if (Test-Path -LiteralPath $releaseNotesPath) {
    Add-Check -Name 'release-notes' -Status 'passed' -Detail 'Tau release notes source exists at docs/releases/feature-release-notes.md.'
}
else {
    Add-Check -Name 'release-notes' -Status 'blocked' -Detail 'Tau release notes source is missing at docs/releases/feature-release-notes.md.'
}

$requiredScripts = @(
    'scripts/update-release-version.ps1',
    'scripts/update-release-notes.ps1',
    'scripts/verify-no-env.ps1',
    'scripts/build-release-matrix.ps1',
    'scripts/package-release-matrix.ps1'
)
$missingScripts = @($requiredScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_)) })
if ($missingScripts.Count -eq 0) {
    Add-Check -Name 'release-scripts' -Status 'passed' -Detail 'Release preparation, validation and matrix scripts are present.'
}
else {
    Add-Check -Name 'release-scripts' -Status 'blocked' -Detail "Missing release script(s): $($missingScripts -join ', ')"
}

$plannedCommands = @(
    [ordered]@{
        name = 'version-update'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-release-version.ps1 $ReleaseTarget -Apply"
        purpose = 'Apply the repo-owned MSBuild version source update.'
    },
    [ordered]@{
        name = 'release-notes-update'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-release-notes.ps1 $nextVersion -Apply"
        purpose = 'Apply the Tau release notes row for the prepared release.'
    },
    [ordered]@{
        name = 'diff-check'
        command = 'git diff --check'
        purpose = 'Validate patch hygiene after local release preparation writes.'
    },
    [ordered]@{
        name = 'no-env-validation'
        command = 'powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore -RunSmoke'
        purpose = 'Run the current Tau project gate under provider/auth-isolated child-process environment.'
    },
    [ordered]@{
        name = 'release-matrix-build'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-matrix.ps1 -Configuration $Configuration -Runtimes $($normalizedRuntimes -join ',')"
        purpose = 'Build and package the configured RID matrix.'
    }
)

$remainingGaps = @(
    'This preparation script does not commit, tag, publish or push.',
    'Release execution still needs an explicit commit/tag/publish/push flow after validation and external e2e decisions.',
    'Real non-host runner executable smoke and external provider/Slack/Docker/SSH/HF/GPU/vLLM release e2e remain open.',
    'Exact Unix release wrapper/auth-backup parity and upstream examples/Photon/interactive asset payload parity remain open.'
)

$helperResults = [ordered]@{
    versionUpdate = $null
    releaseNotesUpdate = $null
    versionUpdatePreview = $null
    releaseNotesUpdatePreview = $null
}

$versionPreviewArgs = @($ReleaseTarget, '-Json')
$notesOptionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Date)) {
    $notesOptionArgs += @('-Date', $Date)
}
if (-not [string]::IsNullOrWhiteSpace($FeatureDomain)) {
    $notesOptionArgs += @('-FeatureDomain', $FeatureDomain)
}
if (-not [string]::IsNullOrWhiteSpace($UserValue)) {
    $notesOptionArgs += @('-UserValue', $UserValue)
}
if (-not [string]::IsNullOrWhiteSpace($ChangeSummary)) {
    $notesOptionArgs += @('-ChangeSummary', $ChangeSummary)
}
$notesPreviewArgs = @($nextVersion, '-Json') + $notesOptionArgs

if ($script:hardPreflightFailure) {
    $result = [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $false
        releaseTarget = $ReleaseTarget
        currentVersion = $versionSource.value
        nextVersion = $nextVersion
        releaseTag = $releaseTag
        configuration = $Configuration
        runtimes = $normalizedRuntimes
        checks = $script:checks
        plannedCommands = $plannedCommands
        helperResults = $helperResults
        remainingGaps = $remainingGaps
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release preparation blocked'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
    }

    exit 1
}

if ($Apply) {
    $helperResults.versionUpdatePreview = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-version.ps1') -Arguments $versionPreviewArgs)
    $helperResults.releaseNotesUpdatePreview = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-notes.ps1') -Arguments $notesPreviewArgs)

    $versionArgs = @($ReleaseTarget, '-Apply', '-Json')
    $helperResults.versionUpdate = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-version.ps1') -Arguments $versionArgs)

    $notesArgs = @($nextVersion, '-Apply', '-Json') + $notesOptionArgs
    $helperResults.releaseNotesUpdate = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-notes.ps1') -Arguments $notesArgs)
}
else {
    $helperResults.versionUpdate = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-version.ps1') -Arguments $versionPreviewArgs)
    $helperResults.releaseNotesUpdate = ConvertFrom-HelperJson -JsonText (Invoke-ReleaseHelper -ScriptPath (Join-Path $repoRoot 'scripts/update-release-notes.ps1') -Arguments $notesPreviewArgs)
}

$changedFiles = @(
    'Directory.Build.props',
    'docs/releases/feature-release-notes.md'
)

$result = [ordered]@{
    schemaVersion = 1
    dryRun = -not $Apply.IsPresent
    applied = $Apply.IsPresent
    releaseTarget = $ReleaseTarget
    currentVersion = $versionSource.value
    nextVersion = $nextVersion
    releaseTag = $releaseTag
    configuration = $Configuration
    runtimes = $normalizedRuntimes
    changedFiles = $changedFiles
    workingTree = [ordered]@{
        cleanBefore = $gitStatus.clean
        allowDirty = $AllowDirty.IsPresent
        statusCount = @($gitStatus.entries).Count
        status = $gitStatus.entries
    }
    checks = $script:checks
    plannedCommands = $plannedCommands
    helperResults = $helperResults
    remainingGaps = $remainingGaps
}

if ($Json) {
    $result | ConvertTo-Json -Depth 12
}
else {
    Write-Host 'Tau release preparation'
    Write-Host "  target: $ReleaseTarget"
    Write-Host "  current version: $($versionSource.value)"
    Write-Host "  next version: $nextVersion"
    Write-Host "  release tag: $releaseTag"
    Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
    Write-Host ''

    Write-Host 'Checks:'
    foreach ($check in $script:checks) {
        Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
    }
    Write-Host ''

    Write-Host 'Changed files when applied:'
    foreach ($file in $changedFiles) {
        Write-Host "  - $file"
    }
    Write-Host ''

    Write-Host 'Next validation commands:'
    foreach ($command in ($plannedCommands | Where-Object { $_.name -notin @('version-update', 'release-notes-update') })) {
        Write-Host "  - $($command.command)"
    }
    Write-Host ''

    Write-Host 'Remaining gaps:'
    foreach ($gap in $remainingGaps) {
        Write-Host "  - $gap"
    }
}
