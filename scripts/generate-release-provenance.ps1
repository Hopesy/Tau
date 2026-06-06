param(
    [string[]]$ArtifactPath = @(),
    [string[]]$PackagePath = @(),
    [string]$ArtifactRoot = 'artifacts/releases',
    [string]$PackageRoot = 'artifacts/nuget',
    [string]$OutputPath = 'artifacts/provenance/release-provenance.json',
    [switch]$Apply,
    [switch]$AllowDirty,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:checks = @()
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

    if ($Status -eq 'blocked') {
        $script:hardPreflightFailure = $true
    }
}

function Convert-ToFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$BasePath = $repoRoot
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Convert-ToRepoRelativePath {
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

function Invoke-ProcessText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [ordered]@{
        exitCode = $exitCode
        output = ($output -join [Environment]::NewLine)
    }
}

function Get-GitStatus {
    $result = Invoke-ProcessText -FilePath 'git' -Arguments @('status', '--porcelain')
    if ($result.exitCode -ne 0) {
        return [ordered]@{
            available = $false
            clean = $false
            entries = @()
            error = $result.output
        }
    }

    $entries = @($result.output -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return [ordered]@{
        available = $true
        clean = ($entries.Count -eq 0)
        entries = $entries
        error = ''
    }
}

function Get-GitIdentity {
    $commitResult = Invoke-ProcessText -FilePath 'git' -Arguments @('rev-parse', 'HEAD')
    $branchResult = Invoke-ProcessText -FilePath 'git' -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')

    return [ordered]@{
        available = ($commitResult.exitCode -eq 0)
        commit = if ($commitResult.exitCode -eq 0) { $commitResult.output.Trim() } else { '' }
        branch = if ($branchResult.exitCode -eq 0) { $branchResult.output.Trim() } else { '' }
        error = if ($commitResult.exitCode -eq 0) { '' } else { $commitResult.output }
    }
}

function Get-VersionSource {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        return [ordered]@{
            status = 'missing'
            value = ''
            path = ''
            property = ''
        }
    }

    [xml]$propsXml = Get-Content -LiteralPath $propsPath -Raw
    $versionProperties = @()
    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $nodes = @($propsXml.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
                continue
            }

            $value = $node.InnerText.Trim()
            $versionProperties += [ordered]@{
                path = 'Directory.Build.props'
                property = $propertyName
                value = $value
                semver = ($value -match '^\d+\.\d+\.\d+$')
            }
        }
    }

    $semanticVersions = @($versionProperties | Where-Object { $_.semver })
    if ($semanticVersions.Count -eq 0) {
        return [ordered]@{
            status = 'missing'
            value = ''
            path = ''
            property = ''
            detectedProperties = $versionProperties
        }
    }

    $uniqueValues = @($semanticVersions | ForEach-Object { $_.value } | Sort-Object -Unique)
    if ($uniqueValues.Count -ne 1) {
        return [ordered]@{
            status = 'ambiguous'
            value = ''
            path = ''
            property = ''
            detectedProperties = $versionProperties
        }
    }

    $chosen = $semanticVersions[0]
    return [ordered]@{
        status = 'detected'
        value = $chosen.value
        path = $chosen.path
        property = $chosen.property
        detectedProperties = $versionProperties
    }
}

function Resolve-InputFiles {
    param(
        [string[]]$Paths = @()
    )

    $files = @()
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($path)) {
            $files += Get-ChildItem -Path $path -File -ErrorAction SilentlyContinue
            continue
        }

        $fullPath = Convert-ToFullPath -Path $path
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $files += Get-Item -LiteralPath $fullPath
        }
        else {
            $files += [ordered]@{
                FullName = $fullPath
                Missing = $true
            }
        }
    }

    return @($files)
}

function Get-ReleaseArchives {
    if ($ArtifactPath.Count -gt 0) {
        return Resolve-InputFiles -Paths $ArtifactPath
    }

    $root = Convert-ToFullPath -Path $ArtifactRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $root -File |
        Where-Object { $_.Name -match '\.(zip|tar\.gz)$' } |
        Sort-Object Name)
}

function Get-NuGetPackages {
    if ($PackagePath.Count -gt 0) {
        return Resolve-InputFiles -Paths $PackagePath
    }

    $root = Convert-ToFullPath -Path $PackageRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $root -Filter '*.nupkg' -File |
        Where-Object { -not $_.Name.EndsWith('.symbols.nupkg', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object Name)
}

function New-FileEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Kind
    )

    $item = Get-Item -LiteralPath $Path
    $hash = Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256
    $format = if ($item.Name.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
        'tar.gz'
    }
    elseif ($item.Extension.StartsWith('.', [StringComparison]::Ordinal)) {
        $item.Extension.Substring(1)
    }
    else {
        ''
    }

    return [ordered]@{
        kind = $Kind
        path = Convert-ToRepoRelativePath -Path $item.FullName
        fileName = $item.Name
        format = $format
        sizeBytes = [int64]$item.Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

function New-Result {
    param(
        [object[]]$Archives,
        [object[]]$Packages,
        [bool]$Succeeded = $false,
        [bool]$WroteFile = $false
    )

    return [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToUniversalTime().ToString('O')
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        succeeded = $Succeeded
        outputPath = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $OutputPath)
        wroteFile = $WroteFile
        git = [ordered]@{
            available = $gitIdentity.available
            commit = $gitIdentity.commit
            branch = $gitIdentity.branch
            clean = $gitStatus.clean
            allowDirty = $AllowDirty.IsPresent
            statusCount = @($gitStatus.entries).Count
            status = @($gitStatus.entries)
        }
        version = [ordered]@{
            status = $versionSource.status
            value = $versionSource.value
            path = $versionSource.path
            property = $versionSource.property
        }
        inputs = [ordered]@{
            artifactRoot = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $ArtifactRoot)
            packageRoot = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $PackageRoot)
            explicitArtifactPaths = @($ArtifactPath)
            explicitPackagePaths = @($PackagePath)
        }
        archives = @($Archives)
        packages = @($Packages)
        checks = $script:checks
        remainingGaps = @(
            'This provenance manifest records local release archives and NuGet package hashes only; it does not sign packages.',
            'Package signing must be run separately with scripts/sign-release-packages.ps1 and a real code-signing certificate.',
            'Real registry publish, remote release rehearsal, non-host executable smoke and external e2e release smoke remain separate Phase 5 gates.'
        )
    }
}

try {
    $gitStatus = Get-GitStatus
    $gitIdentity = Get-GitIdentity
    $versionSource = Get-VersionSource

    if (-not $gitStatus.available) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'git-status' -Status $status -Detail "Could not read git status: $($gitStatus.error)"
    }
    elseif ($gitStatus.clean) {
        Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean for provenance generation.'
    }
    elseif ($Apply -and -not $AllowDirty) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Provenance apply requires a clean worktree unless -AllowDirty is explicit."
    }
    else {
        Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s); dry-run remains read-only or -AllowDirty was explicit."
    }

    if (-not $gitIdentity.available) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'git-commit' -Status $status -Detail "Could not read git commit: $($gitIdentity.error)"
    }
    else {
        Add-Check -Name 'git-commit' -Status 'passed' -Detail "Git commit recorded: $($gitIdentity.commit)."
    }

    if ($versionSource.status -eq 'detected') {
        Add-Check -Name 'version-source' -Status 'passed' -Detail "Version $($versionSource.value) from $($versionSource.path) $($versionSource.property)."
    }
    else {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'version-source' -Status $status -Detail 'No unique semantic release version source was found in Directory.Build.props.'
    }

    $archiveInputs = Get-ReleaseArchives
    $packageInputs = Get-NuGetPackages
    $missingArchiveInputs = @($archiveInputs | Where-Object { $_.Missing })
    $missingPackageInputs = @($packageInputs | Where-Object { $_.Missing })
    if ($missingArchiveInputs.Count -gt 0 -or $missingPackageInputs.Count -gt 0) {
        Add-Check -Name 'explicit-paths' -Status 'blocked' -Detail "Missing explicit file path(s): $(@($missingArchiveInputs + $missingPackageInputs | ForEach-Object { Convert-ToRepoRelativePath -Path $_.FullName }) -join ', ')"
    }

    $existingArchiveInputs = @($archiveInputs | Where-Object { -not $_.Missing })
    $existingPackageInputs = @($packageInputs | Where-Object { -not $_.Missing })
    if ($existingArchiveInputs.Count -eq 0) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'release-archives' -Status $status -Detail 'No release archives were found for provenance.'
    }
    else {
        Add-Check -Name 'release-archives' -Status 'passed' -Detail "Release archive count: $($existingArchiveInputs.Count)."
    }

    if ($existingPackageInputs.Count -eq 0) {
        Add-Check -Name 'nuget-packages' -Status 'warning' -Detail 'No NuGet packages were found for provenance; this can be valid before package publish is prepared.'
    }
    else {
        Add-Check -Name 'nuget-packages' -Status 'passed' -Detail "NuGet package count: $($existingPackageInputs.Count)."
    }

    $archives = @($existingArchiveInputs | ForEach-Object { New-FileEntry -Path $_.FullName -Kind 'release-archive' })
    $packages = @($existingPackageInputs | ForEach-Object { New-FileEntry -Path $_.FullName -Kind 'nuget-package' })

    if ($script:hardPreflightFailure) {
        $result = New-Result -Archives $archives -Packages $packages -Succeeded $false
        if ($Json) {
            $result | ConvertTo-Json -Depth 12
        }
        else {
            Write-Host 'Tau release provenance blocked'
            foreach ($check in $script:checks) {
                Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
            }
        }
        exit 1
    }

    $wroteFile = $false
    $result = New-Result -Archives $archives -Packages $packages -Succeeded $true
    if ($Apply) {
        $outputFullPath = Convert-ToFullPath -Path $OutputPath
        New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($outputFullPath)) | Out-Null
        $result = New-Result -Archives $archives -Packages $packages -Succeeded $true -WroteFile $true
        $resultJson = $result | ConvertTo-Json -Depth 12
        Set-Content -LiteralPath $outputFullPath -Value $resultJson -Encoding ASCII
        $wroteFile = $true
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release provenance'
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        Write-Host "  archives: $($archives.Count)"
        Write-Host "  packages: $($packages.Count)"
        Write-Host "  output: $($result.outputPath)"
        if ($wroteFile) {
            Write-Host '  wrote: true'
        }
        Write-Host ''
        Write-Host 'Checks:'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
        Write-Host ''
        Write-Host 'Remaining gaps:'
        foreach ($gap in $result.remainingGaps) {
            Write-Host "  - $gap"
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $false
        succeeded = $false
        checks = $script:checks
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release provenance failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
