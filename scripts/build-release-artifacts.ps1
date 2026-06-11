param(
    [string]$Runtime = '',
    [string]$Configuration = 'Release',
    [string]$OutputRoot = 'artifacts',
    [switch]$SelfContained,
    [switch]$SkipRestore,
    [switch]$SkipSmoke,
    [switch]$ForceSmoke
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

function Get-DefaultRuntimeIdentifier {
    $runtimeInfo = [System.Runtime.InteropServices.RuntimeInformation]
    $osPlatform = [System.Runtime.InteropServices.OSPlatform]

    if ($runtimeInfo::IsOSPlatform($osPlatform::Windows)) {
        $os = 'win'
    }
    elseif ($runtimeInfo::IsOSPlatform($osPlatform::OSX)) {
        $os = 'osx'
    }
    elseif ($runtimeInfo::IsOSPlatform($osPlatform::Linux)) {
        $os = 'linux'
    }
    else {
        throw 'Unable to infer runtime identifier for this operating system. Pass -Runtime explicitly.'
    }

    $arch = $runtimeInfo::ProcessArchitecture.ToString().ToLowerInvariant()
    switch ($arch) {
        'x64' { return "$os-x64" }
        'arm64' { return "$os-arm64" }
        'x86' { return "$os-x86" }
        'arm' { return "$os-arm" }
        default { throw "Unable to infer runtime identifier for architecture '$arch'. Pass -Runtime explicitly." }
    }
}

function Test-IsHostRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier
    )

    return $RuntimeIdentifier.Equals((Get-DefaultRuntimeIdentifier), [StringComparison]::OrdinalIgnoreCase)
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

function Convert-ToManifestPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return ($Path -replace '\\', '/')
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseFull = [System.IO.Path]::GetFullPath($BasePath)
    $targetFull = [System.IO.Path]::GetFullPath($TargetPath)

    $baseUriPath = $baseFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $baseUri = [System.Uri]::new($baseUriPath)
    $targetUri = [System.Uri]::new($targetFull)
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [System.Uri]::UnescapeDataString($relativeUri.ToString()).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Get-RepoVersion {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw 'Directory.Build.props not found; release artifacts require a repo-owned version source.'
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $node = $props.SelectSingleNode("//*[local-name()='$propertyName']")
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
            continue
        }

        $value = $node.InnerText.Trim()
        if ($value -notmatch '^\d+\.\d+\.\d+$') {
            throw "Release version source $propertyName in Directory.Build.props must use x.y.z format. Actual: $value"
        }

        return [ordered]@{
            value = $value
            source = 'Directory.Build.props'
            property = $propertyName
        }
    }

    throw 'Directory.Build.props does not define Version, VersionPrefix or PackageVersion.'
}

function Assert-ArtifactPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactRoot,
        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    $artifactFull = [System.IO.Path]::GetFullPath($ArtifactRoot)
    $outputFull = [System.IO.Path]::GetFullPath($OutputRoot)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $outputPrefix = $outputFull.TrimEnd($separator) + $separator
    $leaf = Split-Path -Leaf $artifactFull

    if (-not $leaf.StartsWith('tau-', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean artifact directory because leaf '$leaf' does not start with 'tau-'."
    }

    if (-not $artifactFull.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean artifact directory outside output root. artifact=$artifactFull outputRoot=$outputFull"
    }
}

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,
        [Parameter(Mandatory = $true)]
        [string]$PublishDir
    )

    $args = @(
        'publish',
        $Project,
        '-c',
        $Configuration,
        '-r',
        $Runtime,
        '-o',
        $PublishDir,
        '-p:UseAppHost=true',
        "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
        '--verbosity',
        'minimal'
    )

    if ($SkipRestore) {
        $args += '--no-restore'
    }

    Write-Host "dotnet $($args -join ' ')"
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function New-CommandWrapper {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Alias,
        [Parameter(Mandatory = $true)]
        [string]$Entrypoint,
        [Parameter(Mandatory = $true)]
        [string]$BinDir,
        [Parameter(Mandatory = $true)]
        [bool]$IsWindows
    )

    if ($IsWindows) {
        $wrapperPath = Join-Path $BinDir "$Alias.cmd"
        $content = @"
@echo off
setlocal
set "TAU_AI_CLI_COMMAND_NAME=%~n0"
set "SCRIPT_DIR=%~dp0"
"%SCRIPT_DIR%..\$Entrypoint" %*
exit /b %ERRORLEVEL%
"@
        [System.IO.File]::WriteAllText($wrapperPath, $content.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))
        return $wrapperPath
    }

    $wrapperPath = Join-Path $BinDir $Alias
    $shellEntrypoint = $Entrypoint -replace '\\', '/'
    $content = @"
#!/usr/bin/env sh
set -eu
TAU_COMMAND_NAME=`${0##*/}
export TAU_AI_CLI_COMMAND_NAME="`$TAU_COMMAND_NAME"
SCRIPT_DIR=`$(CDPATH= cd "`$(dirname "`$0")" && pwd)
exec "`$SCRIPT_DIR/../$shellEntrypoint" "`$@"
"@
    [System.IO.File]::WriteAllText($wrapperPath, $content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

    if (-not $IsWindows) {
        $chmod = Get-Command chmod -ErrorAction SilentlyContinue
        if ($chmod) {
            & chmod +x $wrapperPath
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
        else {
            Write-Warning "chmod was not found; Unix wrapper executable bit could not be set for $wrapperPath"
        }
    }

    return $wrapperPath
}

function Get-FileSummary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        fileCount = 1
        sizeBytes = [int64]$item.Length
    }
}

function Get-DirectorySummary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force)
    $size = [int64]0
    foreach ($file in $files) {
        $size += [int64]$file.Length
    }

    return [ordered]@{
        fileCount = $files.Count
        sizeBytes = $size
    }
}

function Add-ReleasePayloadEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [string]$Source = '',
        [string]$Destination = '',
        [Parameter(Mandatory = $true)]
        [string]$Status,
        [string]$Note = '',
        [int]$FileCount = 0,
        [Int64]$SizeBytes = 0
    )

    $sourceManifestPath = if ([string]::IsNullOrWhiteSpace($Source)) {
        ''
    }
    else {
        Convert-ToManifestPath -Path $Source
    }
    $destinationManifestPath = if ([string]::IsNullOrWhiteSpace($Destination)) {
        ''
    }
    else {
        Convert-ToManifestPath -Path $Destination
    }

    $script:manifestPayloads += [ordered]@{
        name = $Name
        source = $sourceManifestPath
        destination = $destinationManifestPath
        status = $Status
        fileCount = $FileCount
        sizeBytes = $SizeBytes
        note = $Note
    }
}

function Copy-ReleaseFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$SourceRelative,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRelative,
        [switch]$Required
    )

    $sourcePath = Join-Path $repoRoot $SourceRelative
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        if ($Required) {
            throw "Required release payload file not found: $SourceRelative"
        }

        Add-ReleasePayloadEntry -Name $Name -Source $SourceRelative -Destination $DestinationRelative -Status 'missing'
        return
    }

    $destinationPath = Join-Path $artifactRoot $DestinationRelative
    $destinationParent = Split-Path -Parent $destinationPath
    if (-not [string]::IsNullOrWhiteSpace($destinationParent)) {
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    }

    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    $summary = Get-FileSummary -Path $sourcePath
    Add-ReleasePayloadEntry `
        -Name $Name `
        -Source $SourceRelative `
        -Destination $DestinationRelative `
        -Status 'included' `
        -FileCount $summary.fileCount `
        -SizeBytes $summary.sizeBytes
}

function Copy-ReleaseDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$SourceRelative,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRelative,
        [switch]$Required
    )

    $sourcePath = Join-Path $repoRoot $SourceRelative
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        if ($Required) {
            throw "Required release payload directory not found: $SourceRelative"
        }

        Add-ReleasePayloadEntry -Name $Name -Source $SourceRelative -Destination $DestinationRelative -Status 'missing'
        return
    }

    $destinationPath = Join-Path $artifactRoot $DestinationRelative
    if (Test-Path -LiteralPath $destinationPath) {
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }

    $destinationParent = Split-Path -Parent $destinationPath
    if (-not [string]::IsNullOrWhiteSpace($destinationParent)) {
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    }

    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Recurse -Force
    $summary = Get-DirectorySummary -Path $sourcePath
    Add-ReleasePayloadEntry `
        -Name $Name `
        -Source $SourceRelative `
        -Destination $DestinationRelative `
        -Status 'included' `
        -FileCount $summary.fileCount `
        -SizeBytes $summary.sizeBytes
}

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = Get-DefaultRuntimeIdentifier
}

$outputRootFull = Convert-ToFullPath -Path $OutputRoot -BasePath $repoRoot
$artifactRoot = Join-Path $outputRootFull "tau-$Runtime"
$binDir = Join-Path $artifactRoot 'bin'
$appsDir = Join-Path $artifactRoot 'apps'
$isWindowsRid = $Runtime.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)
$entrypointExtension = if ($isWindowsRid) { '.exe' } else { '' }
$repoVersion = Get-RepoVersion

Assert-ArtifactPath -ArtifactRoot $artifactRoot -OutputRoot $outputRootFull

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $binDir, $appsDir | Out-Null

$apps = @(
    [ordered]@{
        Name = 'pi'
        UpstreamBin = 'pi'
        Project = 'src/Tau.CodingAgent/Tau.CodingAgent.csproj'
        AppDir = 'apps/pi'
        AssemblyName = 'Tau.CodingAgent'
        Aliases = @('pi')
    },
    [ordered]@{
        Name = 'tau-ai'
        UpstreamBin = 'pi-ai'
        Project = 'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj'
        AppDir = 'apps/tau-ai'
        AssemblyName = 'Tau.Ai.Cli'
        Aliases = @('tau-ai', 'pi-ai')
    },
    [ordered]@{
        Name = 'mom'
        UpstreamBin = 'mom'
        Project = 'src/Tau.Mom/Tau.Mom.csproj'
        AppDir = 'apps/mom'
        AssemblyName = 'Tau.Mom'
        Aliases = @('mom')
    },
    [ordered]@{
        Name = 'pi-pods'
        UpstreamBin = 'pi-pods'
        Project = 'src/Tau.Pods/Tau.Pods.csproj'
        AppDir = 'apps/pi-pods'
        AssemblyName = 'Tau.Pods'
        Aliases = @('pi-pods')
    },
    [ordered]@{
        Name = 'tau-web-ui'
        UpstreamBin = 'web-ui-host'
        Project = 'src/Tau.WebUi/Tau.WebUi.csproj'
        AppDir = 'apps/tau-web-ui'
        AssemblyName = 'Tau.WebUi'
        Aliases = @('tau-web-ui')
    }
)

Write-Host "==> release artifact root: $artifactRoot"
Write-Host "==> runtime: $Runtime configuration: $Configuration selfContained: $($SelfContained.IsPresent)"

$manifestApps = @()
$manifestCommands = @()
$script:manifestPayloads = @()

foreach ($app in $apps) {
    $publishDir = Join-Path $artifactRoot $app.AppDir
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "==> publish $($app.Name)"
    Invoke-DotnetPublish -Project $app.Project -PublishDir $publishDir

    $entrypointRelative = Join-Path $app.AppDir ($app.AssemblyName + $entrypointExtension)
    $entrypointFull = Join-Path $artifactRoot $entrypointRelative
    if (-not (Test-Path -LiteralPath $entrypointFull)) {
        throw "Expected published entrypoint was not found: $entrypointFull"
    }

    $commandEntries = @()
    foreach ($alias in $app.Aliases) {
        $wrapperPath = New-CommandWrapper `
            -Alias $alias `
            -Entrypoint $entrypointRelative `
            -BinDir $binDir `
            -IsWindows $isWindowsRid

        $wrapperRelative = Get-RelativePath -BasePath $artifactRoot -TargetPath $wrapperPath
        $commandEntries += Convert-ToManifestPath -Path $wrapperRelative
        $manifestCommands += [ordered]@{
            name = $alias
            path = Convert-ToManifestPath -Path $wrapperRelative
            app = $app.Name
            upstreamBin = $app.UpstreamBin
        }
    }

    $manifestApps += [ordered]@{
        name = $app.Name
        upstreamBin = $app.UpstreamBin
        project = Convert-ToManifestPath -Path $app.Project
        publishDir = Convert-ToManifestPath -Path $app.AppDir
        entrypoint = Convert-ToManifestPath -Path $entrypointRelative
        aliases = $app.Aliases
        commands = $commandEntries
    }
}

Copy-ReleaseFile -Name 'readme' -SourceRelative 'README.md' -DestinationRelative 'README.md' -Required
Copy-ReleaseFile -Name 'license' -SourceRelative 'LICENSE' -DestinationRelative 'LICENSE' -Required
Copy-ReleaseDirectory -Name 'docs' -SourceRelative 'docs' -DestinationRelative 'docs' -Required
Copy-ReleaseDirectory -Name 'examples' -SourceRelative 'examples' -DestinationRelative 'examples'

$releaseNotesRelative = 'docs/releases/feature-release-notes.md'
$releaseNotesPath = Join-Path $repoRoot $releaseNotesRelative
if (Test-Path -LiteralPath $releaseNotesPath) {
    $releaseNotesSummary = Get-FileSummary -Path $releaseNotesPath
    Add-ReleasePayloadEntry `
        -Name 'changelog' `
        -Source $releaseNotesRelative `
        -Destination $releaseNotesRelative `
        -Status 'tau-native-docs' `
        -FileCount $releaseNotesSummary.fileCount `
        -SizeBytes $releaseNotesSummary.sizeBytes `
        -Note 'Tau currently ships docs/releases/feature-release-notes.md as the release notes source instead of a root CHANGELOG.md.'
}
else {
    Add-ReleasePayloadEntry `
        -Name 'changelog' `
        -Source $releaseNotesRelative `
        -Destination $releaseNotesRelative `
        -Status 'missing' `
        -Note 'Tau has no release notes payload available; upstream ships CHANGELOG.md.'
}

Add-ReleasePayloadEntry `
    -Name 'package-json' `
    -Source 'package.json' `
    -Destination 'manifest.json' `
    -Status 'tau-native-manifest' `
    -FileCount 1 `
    -Note 'Tau is a .NET solution, so release package metadata is generated into manifest.json instead of copying an npm package.json.'

Add-ReleasePayloadEntry `
    -Name 'photon-wasm' `
    -Source 'node_modules/@silvia-odwyer/photon-node/photon_rs_bg.wasm' `
    -Destination 'photon_rs_bg.wasm' `
    -Status 'missing' `
    -Note 'Tau does not yet port the upstream Photon image resize/convert pipeline; image resize and EXIF handling remain parity gaps.'

Add-ReleasePayloadEntry `
    -Name 'theme' `
    -Source 'src/Tau.CodingAgent/Runtime/CodingAgentThemeStore.cs' `
    -Status 'tau-native-inline' `
    -Note 'Tau built-in themes are compiled into CodingAgentThemeStore; project/user/extension themes remain runtime-discovered from .tau paths.'

Add-ReleasePayloadEntry `
    -Name 'export-html' `
    -Source 'src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs' `
    -Status 'tau-native-inline' `
    -Note 'Tau HTML transcript export template, CSS and script are compiled into CodingAgentHtmlSessionExporter instead of copied as external template/vendor files.'

Add-ReleasePayloadEntry `
    -Name 'interactive-assets' `
    -Source 'src/Tau.CodingAgent/assets' `
    -Destination 'assets' `
    -Status 'missing' `
    -Note 'No Tau raster interactive asset directory exists yet; upstream clankolas.png-style asset packaging remains a product/release parity gap.'

Add-ReleasePayloadEntry `
    -Name 'koffi-windows-native' `
    -Source 'node_modules/koffi' `
    -Status 'not-applicable' `
    -Note 'Upstream copies koffi for Bun Windows VT input; Tau release wrappers launch .NET executables and do not depend on koffi.'

$manifest = [ordered]@{
    schemaVersion = 1
    createdAt = (Get-Date).ToUniversalTime().ToString('O')
    version = $repoVersion.value
    versionSource = [ordered]@{
        path = $repoVersion.source
        property = $repoVersion.property
    }
    runtimeIdentifier = $Runtime
    configuration = $Configuration
    selfContained = $SelfContained.IsPresent
    artifactRoot = $artifactRoot
    apps = $manifestApps
    commands = $manifestCommands
    upstreamMapping = [ordered]@{
        pi = 'Tau.CodingAgent release wrapper'
        piAi = 'Tau.Ai.Cli release wrapper alias pi-ai'
        tauAi = 'Tau.Ai.Cli release wrapper'
        mom = 'Tau.Mom release wrapper'
        piPods = 'Tau.Pods release wrapper'
        tauWebUi = 'Tau.WebUi release wrapper'
    }
    releasePayload = $script:manifestPayloads
    includedDocs = @(
        $script:manifestPayloads |
            Where-Object { $_.status -eq 'included' -and $_.destination -like 'docs*' } |
            ForEach-Object { $_.destination }
    )
    remainingGaps = @(
        'Non-host runner executable smoke for Linux and macOS release artifacts',
        'Photon image pipeline, interactive raster assets and external export-html vendor/template files are not copied because current Tau equivalents are compiled inline, represented by docs, or not yet ported',
        'Version bump, changelog release section, commit, tag and publish automation parity with upstream release.mjs',
        'Exact auth backup parity with upstream test.sh and pi-test.sh; Tau currently has PowerShell no-env child-process isolation scripts',
        'Real external provider, Slack, Docker, SSH, HF, GPU and vLLM e2e release smoke'
    )
}

$manifestPath = Join-Path $artifactRoot 'manifest.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

if (-not $SkipSmoke) {
    if ((Test-IsHostRuntime -RuntimeIdentifier $Runtime) -or $ForceSmoke) {
        Write-Host '==> smoke release artifact'
        & (Join-Path $repoRoot 'scripts/smoke-release-artifacts.ps1') -ArtifactRoot $artifactRoot
    }
    else {
        Write-Host "==> skipping executable smoke because runtime '$Runtime' does not match host runtime"
    }
}

Write-Host "Tau release artifact built at $artifactRoot"
