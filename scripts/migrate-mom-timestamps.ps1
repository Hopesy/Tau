param(
    [Parameter(Mandatory = $true)]
    [string]$DataDirectory,
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

function Get-MigratableTimestampInteger {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = ([string]$Value).Trim()
    if ($text -match '^(\d+)(?:\.\d+)?$') {
        return $Matches[1]
    }

    return $null
}

function Test-MillisecondTimestamp {
    param([AllowNull()][object]$Value)

    $text = if ($null -eq $Value) { '' } else { ([string]$Value).Trim() }
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $false
    }

    $leadingInteger = Get-MigratableTimestampInteger -Value $text
    if ([string]::IsNullOrWhiteSpace($leadingInteger)) {
        return $false
    }

    [Int64]$number = 0
    if (-not [Int64]::TryParse($leadingInteger, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return $false
    }

    return $number -gt 1000000000000
}

function Convert-ToSlackTimestamp {
    param([Parameter(Mandatory = $true)][object]$Value)

    $leadingInteger = Get-MigratableTimestampInteger -Value $Value
    [Int64]$milliseconds = 0
    if ([string]::IsNullOrWhiteSpace($leadingInteger) -or
        -not [Int64]::TryParse($leadingInteger, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$milliseconds)) {
        throw "Timestamp is not a millisecond value: $Value"
    }

    $seconds = [Math]::Floor([double]$milliseconds / 1000)
    $micros = ($milliseconds % 1000) * 1000
    return ([Int64]$seconds).ToString([Globalization.CultureInfo]::InvariantCulture) + '.' + $micros.ToString('000000', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-LogFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "DataDirectory not found: $Root"
    }

    $files = @()
    foreach ($directory in @(Get-ChildItem -LiteralPath $Root -Directory | Sort-Object FullName)) {
        $logPath = Join-Path $directory.FullName 'log.jsonl'
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            $files += $logPath
        }
    }

    return @($files)
}

function Get-PropertySum {
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Items = @(),
        [Parameter(Mandatory = $true)]
        [string]$Property
    )

    if ($null -eq $Items -or $Items.Count -eq 0) {
        return 0
    }

    $sum = @($Items | Measure-Object -Property $Property -Sum).Sum
    if ($null -eq $sum) {
        return 0
    }

    return [int]$sum
}

function Invoke-LogMigration {
    param([Parameter(Mandatory = $true)][string]$LogPath)

    $lines = @([System.IO.File]::ReadAllLines($LogPath) | Where-Object { $_ -ne '' })
    $newLines = [System.Collections.Generic.List[string]]::new()
    $migrations = @()
    $malformed = 0

    foreach ($line in $lines) {
        try {
            $entry = $line | ConvertFrom-Json
            $tsProperty = @($entry.PSObject.Properties | Where-Object { $_.Name -eq 'ts' } | Select-Object -First 1)
            if ($tsProperty.Count -gt 0 -and (Test-MillisecondTimestamp -Value $tsProperty[0].Value)) {
                $old = [string]$tsProperty[0].Value
                $new = Convert-ToSlackTimestamp -Value $tsProperty[0].Value
                $entry.ts = $new
                $migrations += [ordered]@{
                    old = $old
                    new = $new
                }
            }

            $newLines.Add(($entry | ConvertTo-Json -Compress -Depth 20)) | Out-Null
        }
        catch {
            $malformed++
            $newLines.Add($line) | Out-Null
        }
    }

    if ($Apply -and $migrations.Count -gt 0) {
        [System.IO.File]::WriteAllText($LogPath, (($newLines.ToArray() -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
    }

    return [ordered]@{
        path = Convert-ToDisplayPath -Path $LogPath
        totalLines = $lines.Count
        migrated = $migrations.Count
        malformed = $malformed
        updated = ($Apply -and $migrations.Count -gt 0)
        migrations = @($migrations)
    }
}

try {
    $dataRoot = Resolve-FullPath -Path $DataDirectory
    $logFiles = @(Get-LogFiles -Root $dataRoot)
    $fileResults = @()
    foreach ($logFile in $logFiles) {
        $fileResults += [pscustomobject](Invoke-LogMigration -LogPath $logFile)
    }

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        dataDirectory = Convert-ToDisplayPath -Path $dataRoot
        scan = [ordered]@{
            logFileCount = $logFiles.Count
            totalLines = Get-PropertySum -Items $fileResults -Property 'totalLines'
            migrated = Get-PropertySum -Items $fileResults -Property 'migrated'
            malformed = Get-PropertySum -Items $fileResults -Property 'malformed'
            updatedFileCount = @($fileResults | Where-Object { $_.updated }).Count
        }
        files = @($fileResults)
        remainingGaps = @(
            'This is a local Mom log timestamp migration helper; it does not prove real Slack runtime smoke or Slack session sync parity.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Scanned $($result.scan.logFileCount) Mom channel log file(s) under $($result.dataDirectory)"
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to rewrite migrated log.jsonl files.'
        }
        Write-Host "Migrated timestamps: $($result.scan.migrated)"
        Write-Host "Malformed lines preserved: $($result.scan.malformed)"
        foreach ($file in $result.files) {
            Write-Host "  $($file.path): $($file.migrated)/$($file.totalLines) migrated"
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        dryRun = -not $Apply.IsPresent
        applied = $false
        dataDirectory = $DataDirectory
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau Mom timestamp migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
