param(
    [string[]]$Runtimes = @('osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64', 'win-x64'),
    [string]$OutputRoot = 'artifacts',
    [string]$ArchiveRoot = 'artifacts/releases',
    [int]$TimeoutSeconds = 25,
    [switch]$SkipSmoke,
    [switch]$SkipExecutableSmoke,
    [switch]$ForceSmoke
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

$outputRootFull = Convert-ToFullPath -Path $OutputRoot -BasePath $repoRoot
$archiveRootFull = Convert-ToFullPath -Path $ArchiveRoot -BasePath $repoRoot
$results = @()

foreach ($runtime in $Runtimes) {
    $artifactRoot = Join-Path $outputRootFull "tau-$runtime"
    $format = Get-ArchiveFormatForRuntime -Runtime $runtime
    $extension = if ($format -eq 'zip') { 'zip' } else { 'tar.gz' }
    $archivePath = Join-Path $archiveRootFull "tau-$runtime.$extension"

    if (-not (Test-Path -LiteralPath (Join-Path $artifactRoot 'manifest.json'))) {
        throw "Artifact manifest not found for runtime '$runtime': $(Join-Path $artifactRoot 'manifest.json')"
    }

    Write-Host "==> package matrix runtime=$runtime format=$format"

    $packageParams = @{
        ArtifactRoot = $artifactRoot
        Runtime = $runtime
        ArchiveFormat = $format
        ArchiveRoot = $archiveRootFull
        TimeoutSeconds = $TimeoutSeconds
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

    & (Join-Path $repoRoot 'scripts/package-release-artifacts.ps1') @packageParams

    if (-not (Test-Path -LiteralPath $archivePath)) {
        throw "Expected archive not found: $archivePath"
    }

    $archiveInfo = Get-Item -LiteralPath $archivePath
    $results += [pscustomobject]@{
        runtime = $runtime
        archiveFormat = $format
        archivePath = $archiveInfo.FullName
        sizeBytes = $archiveInfo.Length
    }
}

$results | Format-Table -AutoSize
Write-Host "Tau release archive matrix built: $($results.Count) archive(s)"
