param(
    [string[]]$Runtimes = @('osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64', 'win-x64'),
    [string]$Configuration = 'Release',
    [string]$OutputRoot = 'artifacts',
    [string]$ArchiveRoot = 'artifacts/releases',
    [switch]$SelfContained,
    [switch]$SkipRestore,
    [switch]$SkipSmoke,
    [switch]$SkipExecutableSmoke,
    [switch]$ForceSmoke,
    [switch]$SkipPackage
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

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

function Get-ArchiveFormatForRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    if ($Runtime.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
        return 'zip'
    }

    return 'tar.gz'
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host "==> $Label"
    & $Action
}

$outputRootFull = Convert-ToFullPath -Path $OutputRoot -BasePath $repoRoot
$archiveRootFull = Convert-ToFullPath -Path $ArchiveRoot -BasePath $repoRoot
$results = @()

foreach ($runtime in $Runtimes) {
    $artifactRoot = Join-Path $outputRootFull "tau-$runtime"
    $format = Get-ArchiveFormatForRuntime -Runtime $runtime
    $extension = if ($format -eq 'zip') { 'zip' } else { 'tar.gz' }
    $archivePath = Join-Path $archiveRootFull "tau-$runtime.$extension"

    if (-not $SkipRestore) {
        Invoke-Step -Label "restore runtime=$runtime" -Action {
            & dotnet restore Tau.slnx -r $runtime --verbosity minimal
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
    }

    $buildParams = @{
        Runtime = $runtime
        Configuration = $Configuration
        OutputRoot = $outputRootFull
        SkipRestore = $true
    }

    if ($SelfContained) {
        $buildParams.SelfContained = $true
    }
    if ($SkipSmoke) {
        $buildParams.SkipSmoke = $true
    }
    if ($ForceSmoke) {
        $buildParams.ForceSmoke = $true
    }

    Invoke-Step -Label "build release artifact runtime=$runtime" -Action {
        & (Join-Path $repoRoot 'scripts/build-release-artifacts.ps1') @buildParams
    }

    if (-not (Test-Path -LiteralPath (Join-Path $artifactRoot 'manifest.json'))) {
        throw "Build did not produce artifact manifest for runtime '$runtime': $(Join-Path $artifactRoot 'manifest.json')"
    }

    if (-not $SkipPackage) {
        $packageParams = @{
            ArtifactRoot = $artifactRoot
            Runtime = $runtime
            ArchiveFormat = $format
            ArchiveRoot = $archiveRootFull
        }

        if ($SkipSmoke) {
            $packageParams.SkipSmoke = $true
        }
        if ($SkipExecutableSmoke) {
            $packageParams.SkipExecutableSmoke = $true
        }
        if ($ForceSmoke) {
            $packageParams.ForceSmoke = $true
        }

        Invoke-Step -Label "package release archive runtime=$runtime format=$format" -Action {
            & (Join-Path $repoRoot 'scripts/package-release-artifacts.ps1') @packageParams
        }

        if (-not (Test-Path -LiteralPath $archivePath)) {
            throw "Expected archive not found: $archivePath"
        }
    }

    $artifactInfo = Get-Item -LiteralPath $artifactRoot
    $archiveInfo = if ((-not $SkipPackage) -and (Test-Path -LiteralPath $archivePath)) {
        Get-Item -LiteralPath $archivePath
    }
    else {
        $null
    }

    $results += [pscustomobject]@{
        runtime = $runtime
        artifactRoot = $artifactInfo.FullName
        archiveFormat = if ($SkipPackage) { '' } else { $format }
        archivePath = if ($archiveInfo) { $archiveInfo.FullName } else { '' }
        archiveSizeBytes = if ($archiveInfo) { $archiveInfo.Length } else { 0 }
    }
}

$results | Format-Table -AutoSize
Write-Host "Tau release artifact matrix built: $($results.Count) runtime(s)"
