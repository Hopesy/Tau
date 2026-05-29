param(
    [string[]]$BaseDirectory = @((Join-Path $HOME '.tau'), (Join-Path (Get-Location).Path '.tau')),
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path

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

function Get-UniqueFullPaths {
    param([AllowNull()][string[]]$Paths)

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $resolved = @()

    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $fullPath = Resolve-FullPath -Path $path
        if ($seen.Add($fullPath)) {
            $resolved += $fullPath
        }
    }

    return $resolved
}

function Get-SourceKind {
    param([Parameter(Mandatory = $true)]$Item)

    $isDirectory = (($Item.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0)
    $isReparsePoint = (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)

    if ($isDirectory -and $isReparsePoint) {
        return 'directory-reparse-point'
    }

    if ($isDirectory) {
        return 'directory'
    }

    if ($isReparsePoint) {
        return 'file-reparse-point'
    }

    return 'file'
}

function New-DirectoryResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$Action,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Reason,
        [string]$SourceKind = 'none',
        [AllowEmptyString()]
        [string]$ErrorMessage = ''
    )

    $commandsPath = Join-Path $BasePath 'commands'
    $promptsPath = Join-Path $BasePath 'prompts'

    $result = [ordered]@{
        baseDirectory = Convert-ToDisplayPath -Path $BasePath
        commandsPath = Convert-ToDisplayPath -Path $commandsPath
        promptsPath = Convert-ToDisplayPath -Path $promptsPath
        sourceKind = $SourceKind
        action = $Action
        reason = $Reason
    }

    if (-not [string]::IsNullOrWhiteSpace($ErrorMessage)) {
        $result.error = $ErrorMessage
    }

    return $result
}

function Test-CommandsMigrationTarget {
    param([Parameter(Mandatory = $true)][string]$BasePath)

    $commandsPath = Join-Path $BasePath 'commands'
    $promptsPath = Join-Path $BasePath 'prompts'

    if (-not (Test-Path -LiteralPath $BasePath -PathType Container)) {
        return New-DirectoryResult -BasePath $BasePath -Action 'skipped' -Reason 'base-directory-missing'
    }

    if (-not (Test-Path -LiteralPath $commandsPath)) {
        return New-DirectoryResult -BasePath $BasePath -Action 'skipped' -Reason 'no-commands'
    }

    if (Test-Path -LiteralPath $promptsPath) {
        return New-DirectoryResult -BasePath $BasePath -Action 'skipped' -Reason 'prompts-exists'
    }

    try {
        $sourceItem = Get-Item -LiteralPath $commandsPath -Force
    }
    catch {
        return New-DirectoryResult -BasePath $BasePath -Action 'skipped' -Reason 'cannot-read-commands' -ErrorMessage $_.Exception.Message
    }

    $sourceKind = Get-SourceKind -Item $sourceItem
    if (-not (($sourceItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0)) {
        return New-DirectoryResult -BasePath $BasePath -Action 'skipped' -Reason 'source-not-directory' -SourceKind $sourceKind
    }

    return New-DirectoryResult -BasePath $BasePath -Action $(if ($Apply) { 'migrated' } else { 'would-migrate' }) -Reason '' -SourceKind $sourceKind
}

function Get-PropertyCount {
    param(
        [AllowNull()]
        [object[]]$Items,
        [Parameter(Mandatory = $true)]
        [string]$Action
    )

    if ($null -eq $Items -or $Items.Count -eq 0) {
        return 0
    }

    return @($Items | Where-Object { $_.action -eq $Action }).Count
}

try {
    $resolvedBaseDirectories = @(Get-UniqueFullPaths -Paths $BaseDirectory)
    $directoryResults = @()

    foreach ($basePath in $resolvedBaseDirectories) {
        $result = [pscustomobject](Test-CommandsMigrationTarget -BasePath $basePath)

        if ($Apply -and $result.action -eq 'migrated') {
            try {
                Move-Item -LiteralPath (Join-Path $basePath 'commands') -Destination (Join-Path $basePath 'prompts')
            }
            catch {
                $result.action = 'failed'
                $result.reason = 'migration-failed'
                $result | Add-Member -NotePropertyName error -NotePropertyValue $_.Exception.Message -Force
            }
        }

        $directoryResults += $result
    }

    $wouldMigrateCount = Get-PropertyCount -Items $directoryResults -Action 'would-migrate'
    $migratedCount = Get-PropertyCount -Items $directoryResults -Action 'migrated'
    $skippedCount = Get-PropertyCount -Items $directoryResults -Action 'skipped'
    $failedCount = Get-PropertyCount -Items $directoryResults -Action 'failed'

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = ($failedCount -eq 0)
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        baseDirectoryCount = $resolvedBaseDirectories.Count
        scan = [ordered]@{
            migratable = $wouldMigrateCount + $migratedCount
            migrated = $migratedCount
            skipped = $skippedCount
            failed = $failedCount
        }
        directories = @($directoryResults)
        remainingGaps = @(
            'This helper only migrates legacy CodingAgent commands/ directories to prompts/ for configured Tau base directories.',
            'It does not close upstream auth/settings/session/keybindings/tools migrations, tools-to-bin migration, or hooks/tools deprecation warnings.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Scanned $($result.baseDirectoryCount) CodingAgent base directorie(s)."
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to rename migratable commands directories.'
        }
        Write-Host "Migratable: $($result.scan.migratable)"
        Write-Host "Migrated: $($result.scan.migrated)"
        Write-Host "Skipped: $($result.scan.skipped)"
        Write-Host "Failed: $($result.scan.failed)"
        foreach ($directory in $result.directories) {
            if ($directory.action -eq 'skipped') {
                Write-Host "  SKIP $($directory.baseDirectory): $($directory.reason)"
            }
            elseif ($directory.action -eq 'failed') {
                Write-Host "  FAIL $($directory.baseDirectory): $($directory.error)"
            }
            else {
                Write-Host "  $($directory.action.ToUpperInvariant()) $($directory.commandsPath) -> $($directory.promptsPath)"
            }
        }
    }

    if ($failedCount -gt 0) {
        exit 1
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        dryRun = -not $Apply.IsPresent
        applied = $false
        baseDirectoryCount = 0
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent commands migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
