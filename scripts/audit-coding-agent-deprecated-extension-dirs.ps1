param(
    [string[]]$BaseDirectory = @(),
    [string[]]$Label = @(),
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path
$migrationGuideUrl = 'https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/CHANGELOG.md#extensions-migration'
$extensionsDocUrl = 'https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/extensions.md'
$managedToolNames = @('fd', 'rg', 'fd.exe', 'rg.exe')

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$BasePath = $invocationDirectory
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Convert-ToDisplayPath {
    param([Parameter(Mandatory = $true)][string]$Path)

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

function Get-AuditTargets {
    $targets = @()

    if ($BaseDirectory.Count -eq 0) {
        $targets += [ordered]@{
            label = 'Global'
            path = Resolve-FullPath -Path (Join-Path $HOME '.tau')
        }
        $targets += [ordered]@{
            label = 'Project'
            path = Resolve-FullPath -Path (Join-Path (Get-Location).Path '.tau')
        }
        return $targets
    }

    for ($i = 0; $i -lt $BaseDirectory.Count; $i++) {
        $path = $BaseDirectory[$i]
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $targetLabel = if ($i -lt $Label.Count -and -not [string]::IsNullOrWhiteSpace($Label[$i])) {
            $Label[$i]
        }
        else {
            "Directory $($i + 1)"
        }

        $targets += [ordered]@{
            label = $targetLabel
            path = Resolve-FullPath -Path $path
        }
    }

    return $targets
}

function New-DirectoryAudit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $hooksPath = Join-Path $BasePath 'hooks'
    $toolsPath = Join-Path $BasePath 'tools'
    $warnings = @()
    $customTools = @()
    $ignoredManaged = @()
    $ignoredHidden = @()
    $toolsReadError = ''
    $toolsExists = Test-Path -LiteralPath $toolsPath
    $toolsIsDirectory = Test-Path -LiteralPath $toolsPath -PathType Container
    $toolsReadable = $false

    if (Test-Path -LiteralPath $hooksPath) {
        $warnings += "$Label hooks/ directory found. Hooks have been renamed to extensions."
    }

    if ($toolsExists -and $toolsIsDirectory) {
        try {
            $entries = @(Get-ChildItem -LiteralPath $toolsPath -Force -Name -ErrorAction Stop | ForEach-Object { [string]$_ })
            $toolsReadable = $true

            foreach ($entry in $entries) {
                $lower = $entry.ToLowerInvariant()
                if ($managedToolNames -contains $lower) {
                    $ignoredManaged += $entry
                }
                elseif ($entry.StartsWith('.', [StringComparison]::Ordinal)) {
                    $ignoredHidden += $entry
                }
                else {
                    $customTools += $entry
                }
            }

            if ($customTools.Count -gt 0) {
                $warnings += "$Label tools/ directory contains custom tools. Custom tools have been merged into extensions."
            }
        }
        catch {
            $toolsReadError = $_.Exception.Message
        }
    }
    elseif ($toolsExists) {
        $toolsReadError = 'tools-path-not-directory'
    }

    return [ordered]@{
        label = $Label
        baseDirectory = Convert-ToDisplayPath -Path $BasePath
        baseExists = (Test-Path -LiteralPath $BasePath -PathType Container)
        hooksPath = Convert-ToDisplayPath -Path $hooksPath
        hooksExists = (Test-Path -LiteralPath $hooksPath)
        toolsPath = Convert-ToDisplayPath -Path $toolsPath
        toolsExists = $toolsExists
        toolsIsDirectory = $toolsIsDirectory
        toolsReadable = $toolsReadable
        toolsReadError = $toolsReadError
        customToolCount = $customTools.Count
        customTools = @($customTools)
        ignoredManagedBinaryCount = $ignoredManaged.Count
        ignoredManagedBinaries = @($ignoredManaged)
        ignoredHiddenEntryCount = $ignoredHidden.Count
        ignoredHiddenEntries = @($ignoredHidden)
        warningCount = $warnings.Count
        warnings = @($warnings)
    }
}

try {
    $targets = @(Get-AuditTargets)
    $directoryAudits = @()

    foreach ($target in $targets) {
        $directoryAudits += [pscustomobject](New-DirectoryAudit -BasePath $target.path -Label $target.label)
    }

    $allWarnings = @($directoryAudits | ForEach-Object { $_.warnings } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $hookWarningCount = @($allWarnings | Where-Object { $_ -match ' hooks/ directory found' }).Count
    $customToolsWarningCount = @($allWarnings | Where-Object { $_ -match ' tools/ directory contains custom tools' }).Count
    $toolsReadErrorCount = @($directoryAudits | Where-Object { -not [string]::IsNullOrWhiteSpace($_.toolsReadError) }).Count

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        baseDirectoryCount = $directoryAudits.Count
        scan = [ordered]@{
            warningCount = $allWarnings.Count
            hookWarnings = $hookWarningCount
            customToolsWarnings = $customToolsWarningCount
            directoriesWithWarnings = @($directoryAudits | Where-Object { $_.warningCount -gt 0 }).Count
            toolsReadErrors = $toolsReadErrorCount
        }
        warnings = @($allWarnings)
        directories = @($directoryAudits)
        migrationGuideUrl = $migrationGuideUrl
        extensionsDocUrl = $extensionsDocUrl
        remainingGaps = @(
            'This helper only audits deprecated CodingAgent hooks/ directories and custom entries in tools/ directories.',
            'It does not migrate hooks or custom tools to extensions/ and does not close auth/settings/session/keybindings migrations.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Scanned $($result.baseDirectoryCount) CodingAgent extension base directorie(s)."
        Write-Host 'Audit only; no files were moved or deleted.'
        Write-Host "Warnings: $($result.scan.warningCount)"
        if ($result.scan.warningCount -eq 0) {
            Write-Host 'No deprecated extension directories or custom tools were found.'
        }
        else {
            foreach ($warning in $result.warnings) {
                Write-Host "Warning: $warning"
            }
            Write-Host ''
            Write-Host 'Move your extensions to the extensions/ directory.'
            Write-Host "Migration guide: $migrationGuideUrl"
            Write-Host "Documentation: $extensionsDocUrl"
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        baseDirectoryCount = 0
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent deprecated extension directory audit failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
