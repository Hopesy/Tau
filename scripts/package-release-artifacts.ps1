param(
    [string]$ArtifactRoot = '',
    [string]$OutputRoot = 'artifacts',
    [string]$ArchiveRoot = 'artifacts/releases',
    [string]$Runtime = '',
    [ValidateSet('auto', 'zip', 'tar.gz')]
    [string]$ArchiveFormat = 'auto',
    [int]$TimeoutSeconds = 25,
    [switch]$SkipSmoke,
    [switch]$SkipExecutableSmoke,
    [switch]$ForceSmoke,
    [switch]$KeepExtracted
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

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
        throw 'Unable to infer runtime identifier for this operating system. Pass -Runtime or -ArtifactRoot explicitly.'
    }

    $arch = $runtimeInfo::ProcessArchitecture.ToString().ToLowerInvariant()
    switch ($arch) {
        'x64' { return "$os-x64" }
        'arm64' { return "$os-arm64" }
        'x86' { return "$os-x86" }
        'arm' { return "$os-arm" }
        default { throw "Unable to infer runtime identifier for architecture '$arch'. Pass -Runtime or -ArtifactRoot explicitly." }
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

function Test-IsPathInside {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChildPath,
        [Parameter(Mandatory = $true)]
        [string]$ParentPath
    )

    $childFull = [System.IO.Path]::GetFullPath($ChildPath)
    $parentFull = [System.IO.Path]::GetFullPath($ParentPath)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $parentPrefix = $parentFull.TrimEnd($separator) + $separator

    return $childFull.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-TauArtifactRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $leaf = Split-Path -Leaf ([System.IO.Path]::GetFullPath($Path))
    if (-not $leaf.StartsWith('tau-', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact root leaf must start with 'tau-'. Actual leaf: $leaf"
    }
}

function Assert-SafeTempPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $tempRoot = [System.IO.Path]::GetTempPath()
    if (-not (Test-IsPathInside -ChildPath $Path -ParentPath $tempRoot)) {
        throw "Refusing to remove or extract outside temp root. path=$Path tempRoot=$tempRoot"
    }
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

function Convert-ToZipPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return ($Path -replace '\\', '/')
}

function Get-ArchiveFormatForRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)]
        [string]$RequestedFormat
    )

    if ($RequestedFormat -ne 'auto') {
        return $RequestedFormat
    }

    if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
        return 'zip'
    }

    return 'tar.gz'
}

function Get-ArchiveExtension {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Format
    )

    switch ($Format) {
        'zip' { return 'zip' }
        'tar.gz' { return 'tar.gz' }
        default { throw "Unsupported archive format: $Format" }
    }
}

function Test-IsHostRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier
    )

    return $RuntimeIdentifier.Equals((Get-DefaultRuntimeIdentifier), [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-Tar {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "tar $($Arguments -join ' ')"
    & tar @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed with exit code $LASTEXITCODE"
    }
}

function Add-ZipEntryFromFile {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    $entry = $Archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [System.IO.File]::GetLastWriteTime($FilePath)

    $entryStream = $null
    $fileStream = $null
    try {
        $entryStream = $entry.Open()
        $fileStream = [System.IO.File]::OpenRead($FilePath)
        $fileStream.CopyTo($entryStream)
    }
    finally {
        if ($fileStream) {
            $fileStream.Dispose()
        }
        if ($entryStream) {
            $entryStream.Dispose()
        }
    }
}

function New-ZipArchiveFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$TopLevelDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath
    )

    $zipStream = $null
    $archive = $null
    try {
        $zipStream = [System.IO.File]::Open($ArchivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite)
        $archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

        $files = Get-ChildItem -LiteralPath $SourceRoot -File -Recurse | Sort-Object FullName
        foreach ($file in $files) {
            $relativePath = Get-RelativePath -BasePath $SourceRoot -TargetPath $file.FullName
            $entryName = Convert-ToZipPath -Path (Join-Path $TopLevelDirectory $relativePath)
            Add-ZipEntryFromFile -Archive $archive -FilePath $file.FullName -EntryName $entryName
        }
    }
    finally {
        if ($archive) {
            $archive.Dispose()
        }
        if ($zipStream) {
            $zipStream.Dispose()
        }
    }
}

function New-TarGzArchiveFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$TopLevelDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath
    )

    $sourceParent = Split-Path -Parent $SourceRoot
    Invoke-Tar -Arguments @('-czf', $ArchivePath, '-C', $sourceParent, $TopLevelDirectory)
}

function Expand-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,
        [Parameter(Mandatory = $true)]
        [string]$Format
    )

    if ($Format -eq 'zip') {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $DestinationRoot)
        return
    }

    if ($Format -eq 'tar.gz') {
        Invoke-Tar -Arguments @('-xzf', $ArchivePath, '-C', $DestinationRoot)
        return
    }

    throw "Unsupported archive format: $Format"
}

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = Get-DefaultRuntimeIdentifier
}

$outputRootFull = Convert-ToFullPath -Path $OutputRoot -BasePath $repoRoot
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $outputRootFull "tau-$Runtime"
}

$artifactRootFull = Convert-ToFullPath -Path $ArtifactRoot -BasePath $repoRoot
$archiveRootFull = Convert-ToFullPath -Path $ArchiveRoot -BasePath $repoRoot
$artifactLeaf = Split-Path -Leaf $artifactRootFull

Assert-TauArtifactRoot -Path $artifactRootFull

if (-not (Test-Path -LiteralPath $artifactRootFull)) {
    throw "Artifact root not found: $artifactRootFull"
}

$manifestPath = Join-Path $artifactRootFull 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Artifact manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported artifact manifest schema version: $($manifest.schemaVersion)"
}

if ($manifest.runtimeIdentifier -and $manifest.runtimeIdentifier -ne $Runtime) {
    Write-Host "==> manifest runtime '$($manifest.runtimeIdentifier)' differs from requested runtime '$Runtime'; using manifest runtime for archive name"
    $Runtime = $manifest.runtimeIdentifier
}

$effectiveArchiveFormat = Get-ArchiveFormatForRuntime -RuntimeIdentifier $Runtime -RequestedFormat $ArchiveFormat
$archiveExtension = Get-ArchiveExtension -Format $effectiveArchiveFormat
$isHostRuntime = Test-IsHostRuntime -RuntimeIdentifier $Runtime

New-Item -ItemType Directory -Force -Path $archiveRootFull | Out-Null
$archivePath = Join-Path $archiveRootFull "tau-$Runtime.$archiveExtension"

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Write-Host "==> packaging $artifactRootFull"
Write-Host "==> archive $archivePath"
Write-Host "==> format $effectiveArchiveFormat runtime $Runtime hostRuntime=$isHostRuntime"

if ($effectiveArchiveFormat -eq 'zip') {
    New-ZipArchiveFromDirectory -SourceRoot $artifactRootFull -TopLevelDirectory $artifactLeaf -ArchivePath $archivePath
}
elseif ($effectiveArchiveFormat -eq 'tar.gz') {
    New-TarGzArchiveFromDirectory -SourceRoot $artifactRootFull -TopLevelDirectory $artifactLeaf -ArchivePath $archivePath
}
else {
    throw "Unsupported archive format: $effectiveArchiveFormat"
}

$archiveInfo = Get-Item -LiteralPath $archivePath
if ($archiveInfo.Length -le 0) {
    throw "Archive was created but is empty: $archivePath"
}

if (-not $SkipSmoke) {
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-extract-" + [Guid]::NewGuid().ToString('N'))
    $extractedArtifactRoot = Join-Path $extractRoot $artifactLeaf

    Assert-SafeTempPath -Path $extractRoot
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

    try {
        Write-Host "==> extracting archive smoke root $extractRoot"
        Expand-ReleaseArchive -ArchivePath $archivePath -DestinationRoot $extractRoot -Format $effectiveArchiveFormat

        if (-not (Test-Path -LiteralPath $extractedArtifactRoot)) {
            throw "Extracted artifact root not found: $extractedArtifactRoot"
        }

        if ((-not $SkipExecutableSmoke) -and ($isHostRuntime -or $ForceSmoke)) {
            Write-Host '==> smoke extracted release artifact'
            & (Join-Path $repoRoot 'scripts/smoke-release-artifacts.ps1') -ArtifactRoot $extractedArtifactRoot -TimeoutSeconds $TimeoutSeconds
        }
        else {
            Write-Host "==> extracted archive structure verified; skipping executable smoke"
        }
    }
    finally {
        if ($KeepExtracted) {
            Write-Host "==> keeping extracted archive smoke root: $extractRoot"
        }
        elseif (Test-Path -LiteralPath $extractRoot) {
            Assert-SafeTempPath -Path $extractRoot
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

Write-Host "Tau release archive built at $archivePath"
