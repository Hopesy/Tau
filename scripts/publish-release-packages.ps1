param(
    [string[]]$ProjectPath = @(
        'src/Tau.Ai/Tau.Ai.csproj',
        'src/Tau.Agent/Tau.Agent.csproj',
        'src/Tau.Tui/Tau.Tui.csproj'
    ),
    [string[]]$PackagePath = @(),
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/nuget',
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [string]$ApiKeyEnv = 'NUGET_API_KEY',
    [string]$DotnetCli = 'dotnet',
    [switch]$Apply,
    [switch]$AllowDirty,
    [switch]$AllowNoApiKey,
    [switch]$SkipPack,
    [switch]$SkipPush,
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$SkipDuplicate,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:checks = @()
$script:commandResults = @()
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

function Join-CommandDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$ApiKeyEnvironmentName = ''
    )

    $displayParts = @($FilePath)
    $redactNext = $false
    foreach ($part in $Arguments) {
        $displayPart = $part
        if ($redactNext) {
            $displayPart = if ([string]::IsNullOrWhiteSpace($ApiKeyEnvironmentName)) { '<redacted>' } else { "<redacted:$ApiKeyEnvironmentName>" }
            $redactNext = $false
        }
        elseif ($part -eq '--api-key' -or $part -eq '-k') {
            $redactNext = $true
        }

        if ($displayPart -match '\s') {
            $displayParts += '"' + ($displayPart -replace '"', '\"') + '"'
        }
        else {
            $displayParts += $displayPart
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

function Protect-ApiKeyText {
    param(
        [AllowNull()]
        [string]$Text,
        [AllowNull()]
        [string]$ApiKey,
        [string]$EnvironmentName = ''
    )

    if ([string]::IsNullOrEmpty($Text) -or [string]::IsNullOrEmpty($ApiKey)) {
        return $Text
    }

    $replacement = if ([string]::IsNullOrWhiteSpace($EnvironmentName)) { '<redacted>' } else { "<redacted:$EnvironmentName>" }
    return $Text.Replace($ApiKey, $replacement)
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

function Invoke-ReleaseStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$ApiKeyEnvironmentName = '',
        [string]$ApiKeyValue = ''
    )

    $display = Join-CommandDisplay -FilePath $FilePath -Arguments $Arguments -ApiKeyEnvironmentName $ApiKeyEnvironmentName
    $startedAt = Get-Date

    if (-not $Json) {
        Write-Host "==> $Name"
        Write-Host "    $display"
    }

    $result = Invoke-ProcessText -FilePath $FilePath -Arguments $Arguments
    $durationMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    $redactedOutput = Protect-ApiKeyText -Text $result.output -ApiKey $ApiKeyValue -EnvironmentName $ApiKeyEnvironmentName

    if (-not $Json) {
        if (-not [string]::IsNullOrWhiteSpace($redactedOutput)) {
            Write-Host $redactedOutput
        }

        if ($result.exitCode -eq 0) {
            Write-Host "==> $Name passed"
        }
        else {
            Write-Host "==> $Name failed with exit code $($result.exitCode)"
        }
        Write-Host ''
    }

    $stepResult = [ordered]@{
        name = $Name
        command = $display
        exitCode = $result.exitCode
        durationMs = $durationMs
        outputPreview = Get-OutputPreview -Output $redactedOutput
        outputLength = $redactedOutput.Length
    }
    $script:commandResults += $stepResult

    if ($result.exitCode -ne 0) {
        throw "Release package publish step '$Name' failed with exit code $($result.exitCode)."
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

function Get-VersionSource {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw 'Directory.Build.props was not found.'
    }

    [xml]$propsXml = Get-Content -LiteralPath $propsPath -Raw
    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $nodes = @($propsXml.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
                continue
            }

            $value = $node.InnerText.Trim()
            if ($value -notmatch '^\d+\.\d+\.\d+$') {
                throw "Release package version source $propertyName must use x.y.z format. Actual: $value"
            }

            return [ordered]@{
                path = 'Directory.Build.props'
                property = $propertyName
                value = $value
            }
        }
    }

    throw 'No repo-owned release version source was found. Define Version, VersionPrefix or PackageVersion.'
}

function Get-ProjectMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $fullPath = Convert-ToFullPath -Path $Path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Project file not found: $Path"
    }

    [xml]$projectXml = Get-Content -LiteralPath $fullPath -Raw
    $packageIdNode = @($projectXml.SelectNodes("//*[local-name()='PackageId']")) | Select-Object -First 1
    $outputTypeNode = @($projectXml.SelectNodes("//*[local-name()='OutputType']")) | Select-Object -First 1
    $isPackableNode = @($projectXml.SelectNodes("//*[local-name()='IsPackable']")) | Select-Object -First 1

    $packageId = if ($packageIdNode -and -not [string]::IsNullOrWhiteSpace($packageIdNode.InnerText)) {
        $packageIdNode.InnerText.Trim()
    }
    else {
        [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
    }

    $outputType = if ($outputTypeNode -and -not [string]::IsNullOrWhiteSpace($outputTypeNode.InnerText)) {
        $outputTypeNode.InnerText.Trim()
    }
    else {
        ''
    }
    $sdk = if ($projectXml.Project -and $projectXml.Project.Sdk) { [string]$projectXml.Project.Sdk } else { '' }
    $isApplication = $outputType.Equals('Exe', [StringComparison]::OrdinalIgnoreCase) -or
        $sdk.StartsWith('Microsoft.NET.Sdk.Web', [StringComparison]::OrdinalIgnoreCase) -or
        $sdk.StartsWith('Microsoft.NET.Sdk.Worker', [StringComparison]::OrdinalIgnoreCase)
    $isPackable = if ($isPackableNode -and -not [string]::IsNullOrWhiteSpace($isPackableNode.InnerText)) {
        -not $isPackableNode.InnerText.Trim().Equals('false', [StringComparison]::OrdinalIgnoreCase)
    }
    else {
        $true
    }

    return [ordered]@{
        path = Convert-ToRepoRelativePath -Path $fullPath
        fullPath = $fullPath
        packageId = $packageId
        outputType = $outputType
        sdk = $sdk
        isApplication = $isApplication
        isPackable = $isPackable
        expectedPackage = Convert-ToRepoRelativePath -Path (Join-Path (Convert-ToFullPath -Path $OutputDirectory) "$packageId.$Version.nupkg")
    }
}

function New-PackArguments {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Project,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $args = @('pack', $Project.fullPath, '--configuration', $Configuration, '--output', (Convert-ToFullPath -Path $OutputDirectory), "-p:PackageVersion=$Version")
    if ($NoRestore) {
        $args += '--no-restore'
    }
    if ($NoBuild) {
        $args += '--no-build'
    }

    return $args
}

function New-PushArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageFullPath,
        [string]$ApiKey = ''
    )

    $args = @('nuget', 'push', $PackageFullPath, '--source', $Source, '--no-symbols')
    if ($SkipDuplicate) {
        $args += '--skip-duplicate'
    }
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $args += @('--api-key', $ApiKey)
    }

    return $args
}

function New-PackageEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$SourceKind
    )

    $fullPath = Convert-ToFullPath -Path $Path
    return [ordered]@{
        path = Convert-ToRepoRelativePath -Path $fullPath
        fullPath = $fullPath
        source = $SourceKind
        exists = (Test-Path -LiteralPath $fullPath -PathType Leaf)
    }
}

function New-Result {
    param(
        [object[]]$Projects,
        [object[]]$Packages,
        [object[]]$PlannedCommands,
        [bool]$Succeeded = $false
    )

    return [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        succeeded = $Succeeded
        version = $versionSource.value
        versionSource = [ordered]@{
            path = $versionSource.path
            property = $versionSource.property
        }
        configuration = $Configuration
        outputDirectory = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $OutputDirectory)
        source = $Source
        apiKey = [ordered]@{
            env = $ApiKeyEnv
            present = -not [string]::IsNullOrWhiteSpace($apiKeyValue)
            value = '<redacted>'
        }
        options = [ordered]@{
            allowDirty = $AllowDirty.IsPresent
            allowNoApiKey = $AllowNoApiKey.IsPresent
            skipPack = $SkipPack.IsPresent -or $PackagePath.Count -gt 0
            skipPush = $SkipPush.IsPresent
            noRestore = $NoRestore.IsPresent
            noBuild = $NoBuild.IsPresent
            skipDuplicate = $SkipDuplicate.IsPresent
        }
        projects = @($Projects | ForEach-Object {
            [ordered]@{
                path = $_.path
                packageId = $_.packageId
                outputType = $_.outputType
                sdk = $_.sdk
                isApplication = $_.isApplication
                isPackable = $_.isPackable
                expectedPackage = $_.expectedPackage
            }
        })
        packages = @($Packages | ForEach-Object {
            [ordered]@{
                path = $_.path
                source = $_.source
                exists = $_.exists
            }
        })
        checks = $script:checks
        plannedCommands = @($PlannedCommands)
        commandResults = $script:commandResults
        remainingGaps = @(
            'Real NuGet/package registry publish remains unverified until publish-release-packages.ps1 is run with -Apply against the intended source.',
            'Tau applications are still delivered by release archives by default; NuGet publish defaults only cover public library packages.',
            'Symbol package publishing and package signing/provenance remain open release hardening work.'
        )
    }
}

try {
    $versionSource = Get-VersionSource
    $apiKeyValue = if ([string]::IsNullOrWhiteSpace($ApiKeyEnv)) { '' } else { [Environment]::GetEnvironmentVariable($ApiKeyEnv) }
    $packEnabled = -not $SkipPack.IsPresent -and $PackagePath.Count -eq 0
    $pushEnabled = -not $SkipPush.IsPresent

    $projectMetadata = @()
    if ($packEnabled) {
        foreach ($project in $ProjectPath) {
            $projectMetadata += Get-ProjectMetadata -Path $project -Version $versionSource.value
        }
    }

    if ($packEnabled -and $projectMetadata.Count -eq 0) {
        Add-Check -Name 'package-projects' -Status 'blocked' -Detail 'No project paths were supplied for package publish.'
    }
    elseif ($packEnabled) {
        $notPackable = @($projectMetadata | Where-Object { -not $_.isPackable })
        if ($notPackable.Count -gt 0) {
            Add-Check -Name 'package-projects' -Status 'blocked' -Detail "Project(s) are marked IsPackable=false: $(@($notPackable | ForEach-Object { $_.path }) -join ', ')"
        }
        else {
            Add-Check -Name 'package-projects' -Status 'passed' -Detail "Package project count: $($projectMetadata.Count)."
        }

        $applicationProjects = @($projectMetadata | Where-Object { $_.isApplication })
        if ($applicationProjects.Count -gt 0) {
            Add-Check -Name 'application-packages' -Status 'warning' -Detail "Application project(s) are explicitly included; Tau default release delivery for apps is archive-based: $(@($applicationProjects | ForEach-Object { $_.path }) -join ', ')"
        }
    }
    else {
        Add-Check -Name 'package-projects' -Status 'warning' -Detail 'Project packing is skipped because -SkipPack or -PackagePath was supplied.'
    }

    $gitStatus = Get-GitStatus
    if (-not $gitStatus.available) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'clean-worktree' -Status $status -Detail "Could not read git status: $($gitStatus.error)"
    }
    elseif ($gitStatus.clean) {
        Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean before package publish.'
    }
    elseif ($Apply -and -not $AllowDirty) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Package publish requires a clean worktree unless -AllowDirty is explicit."
    }
    else {
        Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s); dry-run remains read-only or -AllowDirty was explicit."
    }

    if ([string]::IsNullOrWhiteSpace($Source) -and $pushEnabled) {
        Add-Check -Name 'package-source' -Status 'blocked' -Detail 'A package registry source is required when push is enabled.'
    }
    elseif ($pushEnabled) {
        Add-Check -Name 'package-source' -Status 'passed' -Detail "Package registry source configured: $Source"
    }
    else {
        Add-Check -Name 'package-source' -Status 'warning' -Detail 'Package push is skipped by explicit option.'
    }

    if ($pushEnabled -and [string]::IsNullOrWhiteSpace($apiKeyValue) -and -not $AllowNoApiKey) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'api-key' -Status $status -Detail "Package push expects an API key in environment variable '$ApiKeyEnv'."
    }
    elseif ($pushEnabled -and [string]::IsNullOrWhiteSpace($apiKeyValue) -and $AllowNoApiKey) {
        Add-Check -Name 'api-key' -Status 'warning' -Detail 'Package push will run without an explicit API key because -AllowNoApiKey was supplied.'
    }
    elseif ($pushEnabled) {
        Add-Check -Name 'api-key' -Status 'passed' -Detail "Package API key is present in environment variable '$ApiKeyEnv'."
    }
    else {
        Add-Check -Name 'api-key' -Status 'warning' -Detail 'Package API key is not required because package push is skipped.'
    }

    $packageEntries = @()
    if ($PackagePath.Count -gt 0) {
        foreach ($path in $PackagePath) {
            $packageEntries += New-PackageEntry -Path $path -SourceKind 'explicit'
        }
    }
    elseif ($packEnabled) {
        foreach ($project in $projectMetadata) {
            $packageEntries += New-PackageEntry -Path $project.expectedPackage -SourceKind 'project'
        }
    }
    else {
        $outputFull = Convert-ToFullPath -Path $OutputDirectory
        if (Test-Path -LiteralPath $outputFull -PathType Container) {
            foreach ($package in Get-ChildItem -LiteralPath $outputFull -Filter '*.nupkg' -File | Where-Object { -not $_.Name.EndsWith('.symbols.nupkg', [StringComparison]::OrdinalIgnoreCase) } | Sort-Object Name) {
                $packageEntries += New-PackageEntry -Path $package.FullName -SourceKind 'output-directory'
            }
        }
    }

    if ($pushEnabled -and -not $packEnabled) {
        $missingPackages = @($packageEntries | Where-Object { -not $_.exists })
        if ($packageEntries.Count -eq 0) {
            Add-Check -Name 'packages' -Status 'blocked' -Detail 'No package files were found or supplied for push.'
        }
        elseif ($missingPackages.Count -gt 0) {
            Add-Check -Name 'packages' -Status 'blocked' -Detail "Package path(s) do not exist: $(@($missingPackages | ForEach-Object { $_.path }) -join ', ')"
        }
        else {
            Add-Check -Name 'packages' -Status 'passed' -Detail "Package file count: $($packageEntries.Count)."
        }
    }
    elseif ($pushEnabled) {
        Add-Check -Name 'packages' -Status 'passed' -Detail "Package file(s) will be produced from $($projectMetadata.Count) project(s)."
    }
    else {
        Add-Check -Name 'packages' -Status 'warning' -Detail 'Package push is skipped, so package file discovery is informational.'
    }

    $plannedCommands = @()
    if ($packEnabled) {
        foreach ($project in $projectMetadata) {
            $packArgs = New-PackArguments -Project $project -Version $versionSource.value
            $plannedCommands += [ordered]@{
                name = "pack-$($project.packageId)"
                command = Join-CommandDisplay -FilePath $DotnetCli -Arguments $packArgs
                executedWhenApply = $true
            }
        }
    }

    if ($pushEnabled) {
        foreach ($package in $packageEntries) {
            $pushApiKey = ''
            if (-not [string]::IsNullOrWhiteSpace($apiKeyValue)) {
                $pushApiKey = $apiKeyValue
            }
            $pushArgs = New-PushArguments -PackageFullPath $package.fullPath -ApiKey $pushApiKey
            $plannedCommands += [ordered]@{
                name = "push-$([System.IO.Path]::GetFileNameWithoutExtension($package.fullPath))"
                command = Join-CommandDisplay -FilePath $DotnetCli -Arguments $pushArgs -ApiKeyEnvironmentName $ApiKeyEnv
                executedWhenApply = $true
            }
        }
    }

    if ($script:hardPreflightFailure) {
        $result = New-Result -Projects $projectMetadata -Packages $packageEntries -PlannedCommands $plannedCommands -Succeeded $false
        if ($Json) {
            $result | ConvertTo-Json -Depth 12
        }
        else {
            Write-Host 'Tau release package publish blocked'
            foreach ($check in $script:checks) {
                Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
            }
        }
        exit 1
    }

    if ($Apply) {
        if ($packEnabled) {
            New-Item -ItemType Directory -Force -Path (Convert-ToFullPath -Path $OutputDirectory) | Out-Null
            foreach ($project in $projectMetadata) {
                Invoke-ReleaseStep -Name "pack-$($project.packageId)" -FilePath $DotnetCli -Arguments (New-PackArguments -Project $project -Version $versionSource.value)
            }

            $packageEntries = @()
            foreach ($project in $projectMetadata) {
                $packageEntries += New-PackageEntry -Path $project.expectedPackage -SourceKind 'project'
            }
        }

        $missingAfterPack = @($packageEntries | Where-Object { -not (Test-Path -LiteralPath $_.fullPath -PathType Leaf) })
        if ($pushEnabled -and $missingAfterPack.Count -gt 0) {
            throw "Expected package(s) were not found after pack: $(@($missingAfterPack | ForEach-Object { $_.path }) -join ', ')"
        }

        if ($pushEnabled) {
            foreach ($package in $packageEntries) {
                Invoke-ReleaseStep -Name "push-$([System.IO.Path]::GetFileNameWithoutExtension($package.fullPath))" -FilePath $DotnetCli -Arguments (New-PushArguments -PackageFullPath $package.fullPath -ApiKey $apiKeyValue) -ApiKeyEnvironmentName $ApiKeyEnv -ApiKeyValue $apiKeyValue
            }
        }
    }

    $result = New-Result -Projects $projectMetadata -Packages $packageEntries -PlannedCommands $plannedCommands -Succeeded $true
    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release package publish'
        Write-Host "  version: $($versionSource.value)"
        Write-Host "  source: $Source"
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        Write-Host ''
        Write-Host 'Checks:'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
        Write-Host ''
        Write-Host 'Package steps:'
        foreach ($command in $plannedCommands) {
            Write-Host "  - $($command.command)"
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
        commandResults = $script:commandResults
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release package publish failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
