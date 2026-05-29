param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ReleaseTarget,
    [string]$PropsPath = 'Directory.Build.props',
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$bumpTypes = @('major', 'minor', 'patch')
$semverPattern = '^\d+\.\d+\.\d+$'

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
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

function Get-VersionSource {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$XmlDocument
    )

    $properties = @()
    foreach ($propertyName in @('Version', 'VersionPrefix', 'PackageVersion')) {
        $nodes = @($XmlDocument.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
                continue
            }

            $value = $node.InnerText.Trim()
            if (-not (Test-SemVer -Value $value)) {
                throw "Release version source $propertyName must use x.y.z format. Actual: $value"
            }

            $properties += [ordered]@{
                property = $propertyName
                value = $value
                node = $node
            }
        }
    }

    if ($properties.Count -eq 0) {
        throw 'No repo-owned release version source was found. Define Version, VersionPrefix or PackageVersion before updating release versions.'
    }

    $uniqueSources = @($properties | ForEach-Object { $_.property } | Sort-Object -Unique)
    if ($properties.Count -gt 1 -or $uniqueSources.Count -gt 1) {
        $sources = @($properties | ForEach-Object { "$($_.property)=$($_.value)" })
        throw "Multiple release version sources found: $($sources -join ', '). Keep one repo-owned version source before updating."
    }

    return $properties[0]
}

$isBumpTarget = $bumpTypes -contains $ReleaseTarget
$isExplicitVersionTarget = Test-SemVer -Value $ReleaseTarget
if (-not $isBumpTarget -and -not $isExplicitVersionTarget) {
    throw "Usage: powershell -File .\scripts\update-release-version.ps1 <major|minor|patch|x.y.z> [-Apply] [-Json]"
}

$propsFullPath = Resolve-FullPath -Path $PropsPath
if (-not (Test-Path -LiteralPath $propsFullPath)) {
    throw "MSBuild props file not found: $propsFullPath"
}

[xml]$propsXml = Get-Content -LiteralPath $propsFullPath -Raw
$versionSource = Get-VersionSource -XmlDocument $propsXml
$currentVersion = $versionSource.value

if ($isBumpTarget) {
    $nextVersion = Get-BumpedVersion -Version $currentVersion -Bump $ReleaseTarget
}
else {
    $nextVersion = $ReleaseTarget
    if ((Compare-SemVer -A $nextVersion -B $currentVersion) -le 0) {
        throw "Explicit version $nextVersion must be greater than current version $currentVersion."
    }
}

if ($Apply) {
    $versionSource.node.InnerText = $nextVersion

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true

    $writer = [System.Xml.XmlWriter]::Create($propsFullPath, $settings)
    try {
        $propsXml.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

$result = [ordered]@{
    schemaVersion = 1
    dryRun = -not $Apply.IsPresent
    applied = $Apply.IsPresent
    releaseTarget = $ReleaseTarget
    currentVersion = $currentVersion
    nextVersion = $nextVersion
    versionSource = [ordered]@{
        path = Convert-ToDisplayPath -Path $propsFullPath
        property = $versionSource.property
    }
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
}
else {
    Write-Host 'Tau release version update'
    Write-Host "  source: $($result.versionSource.path) $($result.versionSource.property)"
    Write-Host "  current version: $currentVersion"
    Write-Host "  next version: $nextVersion"
    Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
}
