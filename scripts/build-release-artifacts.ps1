param(
    [string]$Runtime = '',
    [string]$Configuration = 'Release',
    [string]$OutputRoot = 'artifacts',
    [switch]$SelfContained,
    [switch]$SkipRestore,
    [switch]$SkipSmoke
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
SCRIPT_DIR=`$(CDPATH= cd -- "`$(dirname -- "`$0")" && pwd)
exec "`$SCRIPT_DIR/../$shellEntrypoint" "`$@"
"@
    [System.IO.File]::WriteAllText($wrapperPath, $content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

    if (-not $IsWindows) {
        & chmod +x $wrapperPath
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    return $wrapperPath
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

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $artifactRoot 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $artifactRoot 'LICENSE') -Force

$releaseNotes = Join-Path $repoRoot 'docs/releases/feature-release-notes.md'
if (Test-Path -LiteralPath $releaseNotes) {
    $artifactReleaseNotesDir = Join-Path $artifactRoot 'docs/releases'
    New-Item -ItemType Directory -Force -Path $artifactReleaseNotesDir | Out-Null
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $artifactReleaseNotesDir 'feature-release-notes.md') -Force
}

$manifest = [ordered]@{
    schemaVersion = 1
    createdAt = (Get-Date).ToUniversalTime().ToString('O')
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
    includedDocs = @(
        'README.md',
        'LICENSE',
        'docs/releases/feature-release-notes.md'
    )
    remainingGaps = @(
        'Cross-platform archive matrix parity with upstream build-binaries.sh',
        'Version bump, changelog release section, commit, tag and publish automation parity with upstream release.mjs',
        'Exact auth backup parity with upstream test.sh and pi-test.sh; Tau currently has PowerShell no-env child-process isolation scripts',
        'Real external provider, Slack, Docker, SSH, HF, GPU and vLLM e2e release smoke'
    )
}

$manifestPath = Join-Path $artifactRoot 'manifest.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

if (-not $SkipSmoke) {
    Write-Host '==> smoke release artifact'
    & (Join-Path $repoRoot 'scripts/smoke-release-artifacts.ps1') -ArtifactRoot $artifactRoot
}

Write-Host "Tau release artifact built at $artifactRoot"
