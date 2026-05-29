param(
    [string]$PropsPath = 'Directory.Build.props',
    [string[]]$ProjectRoots = @('src'),
    [switch]$IncludeTests,
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:semverPattern = '^\d+\.\d+\.\d+$'
$script:versionPropertyNames = @('Version', 'VersionPrefix', 'PackageVersion')

function Resolve-FullPath {
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

function Save-XmlDocument {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$XmlDocument,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true

    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $XmlDocument.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Get-VersionSource {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$XmlDocument,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $properties = @()
    foreach ($propertyName in $script:versionPropertyNames) {
        $nodes = @($XmlDocument.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
                continue
            }

            $value = $node.InnerText.Trim()
            if (-not (Test-SemVer -Value $value)) {
                throw "Release version source $propertyName in $(Convert-ToDisplayPath -Path $Path) must use x.y.z format. Actual: $value"
            }

            $properties += [ordered]@{
                property = $propertyName
                value = $value
                path = Convert-ToDisplayPath -Path $Path
            }
        }
    }

    if ($properties.Count -eq 0) {
        throw "No repo-owned release version source was found in $(Convert-ToDisplayPath -Path $Path). Define Version, VersionPrefix or PackageVersion."
    }

    $uniqueSources = @($properties | ForEach-Object { $_.property } | Sort-Object -Unique)
    if ($properties.Count -gt 1 -or $uniqueSources.Count -gt 1) {
        $sources = @($properties | ForEach-Object { "$($_.property)=$($_.value)" })
        throw "Multiple release version sources found in $(Convert-ToDisplayPath -Path $Path): $($sources -join ', '). Keep one repo-owned version source."
    }

    return $properties[0]
}

function Resolve-ProjectRoots {
    $roots = @($ProjectRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($IncludeTests) {
        $roots += 'tests'
    }

    if ($roots.Count -eq 0) {
        throw 'At least one project root is required.'
    }

    $resolved = @()
    foreach ($root in $roots) {
        $fullPath = Resolve-FullPath -Path $root
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw "Project root not found: $fullPath"
        }

        $resolved += $fullPath
    }

    return @($resolved | Sort-Object -Unique)
}

function Get-ProjectFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Roots
    )

    $files = @()
    foreach ($root in $Roots) {
        $files += @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.csproj' | Where-Object { -not $_.PSIsContainer })
    }

    $uniqueFiles = @($files | ForEach-Object { $_.FullName } | Sort-Object -Unique)
    if ($uniqueFiles.Count -eq 0) {
        throw "No .csproj files found under: $($Roots -join ', ')"
    }

    return $uniqueFiles
}

function Get-ProjectVersionAudit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    [xml]$projectXml = Get-Content -LiteralPath $ProjectPath -Raw
    $versionProperties = @()
    $updated = $false

    foreach ($propertyName in $script:versionPropertyNames) {
        $nodes = @($projectXml.SelectNodes("//*[local-name()='$propertyName']"))
        foreach ($node in $nodes) {
            if ($null -eq $node) {
                continue
            }

            $value = $node.InnerText.Trim()
            $matches = $value -eq $ExpectedVersion
            $entry = [ordered]@{
                property = $propertyName
                value = $value
                expectedVersion = $ExpectedVersion
                inSync = $matches
                updated = $false
            }

            if (-not $matches -and $Apply) {
                $node.InnerText = $ExpectedVersion
                $entry.updated = $true
                $updated = $true
            }

            $versionProperties += $entry
        }
    }

    if ($updated) {
        Save-XmlDocument -XmlDocument $projectXml -Path $ProjectPath
    }

    $projectReferences = @($projectXml.SelectNodes("//*[local-name()='ProjectReference']"))
    return [ordered]@{
        path = Convert-ToDisplayPath -Path $ProjectPath
        explicitVersionProperties = @($versionProperties)
        explicitVersionPropertyCount = @($versionProperties).Count
        outOfSyncVersionPropertyCount = @($versionProperties | Where-Object { -not $_.inSync }).Count
        updatedVersionPropertyCount = @($versionProperties | Where-Object { $_.updated }).Count
        projectReferenceCount = $projectReferences.Count
    }
}

try {
    $propsFullPath = Resolve-FullPath -Path $PropsPath
    if (-not (Test-Path -LiteralPath $propsFullPath -PathType Leaf)) {
        throw "MSBuild props file not found: $propsFullPath"
    }

    [xml]$propsXml = Get-Content -LiteralPath $propsFullPath -Raw
    $versionSource = Get-VersionSource -XmlDocument $propsXml -Path $propsFullPath
    $projectRootsFull = Resolve-ProjectRoots
    $projectFiles = Get-ProjectFiles -Roots $projectRootsFull
    $projects = @()
    foreach ($projectFile in $projectFiles) {
        $projects += Get-ProjectVersionAudit -ProjectPath $projectFile -ExpectedVersion $versionSource.value
    }

    $outOfSyncProjects = @($projects | Where-Object { $_.outOfSyncVersionPropertyCount -gt 0 })
    $explicitVersionProjects = @($projects | Where-Object { $_.explicitVersionPropertyCount -gt 0 })
    $updatedProjects = @($projects | Where-Object { $_.updatedVersionPropertyCount -gt 0 })
    $projectReferenceCount = 0
    foreach ($project in $projects) {
        $projectReferenceCount += $project.projectReferenceCount
    }

    $succeeded = $Apply.IsPresent -or $outOfSyncProjects.Count -eq 0
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $succeeded
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        version = $versionSource.value
        versionSource = [ordered]@{
            path = $versionSource.path
            property = $versionSource.property
        }
        projectRoots = @($projectRootsFull | ForEach-Object { Convert-ToDisplayPath -Path $_ })
        projectCount = $projects.Count
        explicitVersionProjectCount = $explicitVersionProjects.Count
        outOfSyncProjectCount = $outOfSyncProjects.Count
        updatedProjectCount = $updatedProjects.Count
        updatedVersionPropertyCount = @($projects | ForEach-Object { $_.updatedVersionPropertyCount } | Measure-Object -Sum).Sum
        projectReferenceCount = $projectReferenceCount
        projects = @($projects)
        remainingGaps = @(
            'Tau uses one MSBuild version source instead of upstream package-workspace lockstep package.json versions.',
            'NuGet/package publish synchronization remains a separate release automation gap.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau release version sync'
        Write-Host "  source: $($result.versionSource.path) $($result.versionSource.property)=$($result.version)"
        Write-Host "  projects: $($result.projectCount)"
        Write-Host "  explicit project versions: $($result.explicitVersionProjectCount)"
        Write-Host "  out-of-sync projects: $($result.outOfSyncProjectCount)"
        Write-Host "  updated projects: $($result.updatedProjectCount)"
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        if (-not $succeeded) {
            Write-Host '  result: out-of-sync project version properties found; rerun with -Apply to update explicit project versions.'
        }
    }

    if (-not $succeeded) {
        exit 1
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau release version sync failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
