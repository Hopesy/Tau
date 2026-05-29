param(
    [Parameter(Mandatory = $true)]
    [Alias('d', 'Dir')]
    [string]$Directory,
    [Parameter(Mandatory = $true)]
    [Alias('n')]
    [int]$Days,
    [string[]]$SessionPath = @(),
    [string]$SessionsDirectory = '',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path
$script:warnings = @()

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

function Add-SessionPath {
    param(
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Paths,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $fullPath -PathType Leaf) -and -not $Paths.Contains($fullPath)) {
        $Paths.Add($fullPath)
    }
}

function Get-SessionFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $paths = [System.Collections.Generic.List[string]]::new()

    foreach ($path in @($SessionPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        Add-SessionPath -Paths $paths -Path (Convert-ToFullPath -Path $path -BasePath $invocationDirectory)
    }

    if ($paths.Count -gt 0) {
        return @($paths | Sort-Object)
    }

    if (-not [string]::IsNullOrWhiteSpace($SessionsDirectory)) {
        $sessionDirectory = Convert-ToFullPath -Path $SessionsDirectory -BasePath $invocationDirectory
        if (-not (Test-Path -LiteralPath $sessionDirectory -PathType Container)) {
            throw "SessionsDirectory not found: $sessionDirectory"
        }

        foreach ($file in @(Get-ChildItem -LiteralPath $sessionDirectory -Filter '*.jsonl' -File | Sort-Object Name)) {
            Add-SessionPath -Paths $paths -Path $file.FullName
        }

        return @($paths | Sort-Object)
    }

    $treeSessionPath = $env:TAU_CODING_AGENT_TREE_SESSION_FILE
    if (-not [string]::IsNullOrWhiteSpace($treeSessionPath)) {
        Add-SessionPath -Paths $paths -Path $treeSessionPath
    }

    $flatSessionPath = $env:TAU_CODING_AGENT_SESSION_FILE
    if (-not [string]::IsNullOrWhiteSpace($flatSessionPath) -and
        [System.IO.Path]::GetExtension($flatSessionPath).Equals('.jsonl', [StringComparison]::OrdinalIgnoreCase)) {
        Add-SessionPath -Paths $paths -Path $flatSessionPath
    }

    Add-SessionPath -Paths $paths -Path (Join-Path $WorkingDirectory '.tau/coding-agent-session.jsonl')

    $defaultSessionsDirectory = Join-Path $WorkingDirectory '.tau/coding-agent-sessions'
    if (Test-Path -LiteralPath $defaultSessionsDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $defaultSessionsDirectory -Filter '*.jsonl' -File | Sort-Object Name)) {
            Add-SessionPath -Paths $paths -Path $file.FullName
        }
    }

    return @($paths | Sort-Object)
}

function Convert-ToDecimal {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return [decimal]0
    }

    return [decimal]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Convert-ToLong {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return [int64]0
    }

    return [int64]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-PropertyValue {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    if ($null -eq $Object) {
        return $null
    }

    $properties = @($Object.PSObject.Properties)
    foreach ($name in $Names) {
        $property = $properties | Where-Object { $_.Name.Equals($name, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function New-CostBucket {
    return [ordered]@{
        total = [decimal]0
        input = [decimal]0
        output = [decimal]0
        cacheRead = [decimal]0
        cacheWrite = [decimal]0
        requests = 0
        assistantMessages = 0
        tokenRecords = 0
        inputTokens = [int64]0
        outputTokens = [int64]0
        cacheReadTokens = [int64]0
        cacheWriteTokens = [int64]0
    }
}

function Add-ToBucket {
    param(
        [AllowEmptyCollection()]
        [hashtable]$Stats,
        [Parameter(Mandatory = $true)]
        [string]$Day,
        [Parameter(Mandatory = $true)]
        [string]$Provider,
        [Parameter(Mandatory = $true)]
        [object]$Usage,
        [AllowNull()]
        [object]$Cost
    )

    if (-not $Stats.ContainsKey($Day)) {
        $Stats[$Day] = @{}
    }
    if (-not $Stats[$Day].ContainsKey($Provider)) {
        $Stats[$Day][$Provider] = New-CostBucket
    }

    $bucket = $Stats[$Day][$Provider]
    $bucket.assistantMessages++

    if ($null -ne $Cost) {
        $bucket.total += Convert-ToDecimal (Get-PropertyValue -Object $Cost -Names @('total'))
        $bucket.input += Convert-ToDecimal (Get-PropertyValue -Object $Cost -Names @('input'))
        $bucket.output += Convert-ToDecimal (Get-PropertyValue -Object $Cost -Names @('output'))
        $bucket.cacheRead += Convert-ToDecimal (Get-PropertyValue -Object $Cost -Names @('cacheRead', 'cache_read'))
        $bucket.cacheWrite += Convert-ToDecimal (Get-PropertyValue -Object $Cost -Names @('cacheWrite', 'cache_write'))
        $bucket.requests++
    }

    $inputTokens = Convert-ToLong (Get-PropertyValue -Object $Usage -Names @('inputTokens', 'input_tokens', 'input'))
    $outputTokens = Convert-ToLong (Get-PropertyValue -Object $Usage -Names @('outputTokens', 'output_tokens', 'output'))
    $cacheReadTokens = Convert-ToLong (Get-PropertyValue -Object $Usage -Names @('cacheReadTokens', 'cache_read_tokens'))
    $cacheWriteTokens = Convert-ToLong (Get-PropertyValue -Object $Usage -Names @('cacheWriteTokens', 'cache_write_tokens'))

    if (($inputTokens + $outputTokens + $cacheReadTokens + $cacheWriteTokens) -gt 0) {
        $bucket.tokenRecords++
        $bucket.inputTokens += $inputTokens
        $bucket.outputTokens += $outputTokens
        $bucket.cacheReadTokens += $cacheReadTokens
        $bucket.cacheWriteTokens += $cacheWriteTokens
    }
}

function Get-EntryTimestamp {
    param(
        [AllowNull()]
        [object]$Entry,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $timestamp = Get-PropertyValue -Object $Entry -Names @('timestamp')
    if ($null -ne $timestamp -and -not [string]::IsNullOrWhiteSpace([string]$timestamp)) {
        try {
            return [DateTimeOffset]::Parse([string]$timestamp, [System.Globalization.CultureInfo]::InvariantCulture).ToLocalTime().DateTime
        }
        catch {
            $script:warnings += "Could not parse timestamp '$timestamp' in $FilePath; using file last-write time."
        }
    }

    return (Get-Item -LiteralPath $FilePath).LastWriteTime
}

function Format-Cost {
    param(
        [Parameter(Mandatory = $true)]
        [decimal]$Value
    )

    return $Value.ToString('0.0000', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Convert-StatsToObjects {
    param(
        [AllowEmptyCollection()]
        [hashtable]$Stats
    )

    $days = @()
    foreach ($day in @($Stats.Keys | Sort-Object)) {
        $providers = @()
        foreach ($provider in @($Stats[$day].Keys | Sort-Object)) {
            $bucket = $Stats[$day][$provider]
            $providers += [ordered]@{
                provider = $provider
                total = $bucket.total
                input = $bucket.input
                output = $bucket.output
                cacheRead = $bucket.cacheRead
                cacheWrite = $bucket.cacheWrite
                requests = $bucket.requests
                assistantMessages = $bucket.assistantMessages
                tokenRecords = $bucket.tokenRecords
                inputTokens = $bucket.inputTokens
                outputTokens = $bucket.outputTokens
                cacheReadTokens = $bucket.cacheReadTokens
                cacheWriteTokens = $bucket.cacheWriteTokens
            }
        }

        $days += [ordered]@{
            day = $day
            providers = @($providers)
        }
    }

    return $days
}

try {
    if ($Days -le 0) {
        throw 'Days must be greater than zero.'
    }

    $workingDirectory = Convert-ToFullPath -Path $Directory -BasePath $invocationDirectory
    $sessionFiles = @(Get-SessionFiles -WorkingDirectory $workingDirectory)
    if ($sessionFiles.Count -eq 0) {
        throw "No Tau JSONL sessions found for $workingDirectory"
    }

    $cutoff = [DateTime]::Today.AddDays(-$Days)
    $stats = @{}
    $providerTotals = @{}
    $processedLines = 0
    $malformedLineCount = 0
    $assistantMessages = 0
    $costRecords = 0
    $tokenRecords = 0

    foreach ($file in $sessionFiles) {
        $lineNumber = 0
        foreach ($line in [System.IO.File]::ReadLines($file)) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $processedLines++
            try {
                $entry = $line | ConvertFrom-Json
            }
            catch {
                $malformedLineCount++
                $script:warnings += "Skipped malformed JSONL line $lineNumber in $file"
                continue
            }

            if (-not ([string]$entry.type).Equals('message', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $message = $entry.message
            if ($null -eq $message -or -not ([string]$message.role).Equals('assistant', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $entryDate = Get-EntryTimestamp -Entry $entry -FilePath $file
            if ($entryDate -lt $cutoff) {
                continue
            }

            $usage = Get-PropertyValue -Object $message -Names @('usage')
            if ($null -eq $usage) {
                $usage = Get-PropertyValue -Object $entry -Names @('usage')
            }
            if ($null -eq $usage) {
                continue
            }

            $assistantMessages++
            $cost = Get-PropertyValue -Object $usage -Names @('cost')
            if ($null -ne $cost) {
                $costRecords++
            }

            $inputTokens = Convert-ToLong (Get-PropertyValue -Object $usage -Names @('inputTokens', 'input_tokens', 'input'))
            $outputTokens = Convert-ToLong (Get-PropertyValue -Object $usage -Names @('outputTokens', 'output_tokens', 'output'))
            $cacheReadTokens = Convert-ToLong (Get-PropertyValue -Object $usage -Names @('cacheReadTokens', 'cache_read_tokens'))
            $cacheWriteTokens = Convert-ToLong (Get-PropertyValue -Object $usage -Names @('cacheWriteTokens', 'cache_write_tokens'))
            if (($inputTokens + $outputTokens + $cacheReadTokens + $cacheWriteTokens) -gt 0) {
                $tokenRecords++
            }

            $provider = [string](Get-PropertyValue -Object $message -Names @('provider'))
            if ([string]::IsNullOrWhiteSpace($provider)) {
                $provider = [string](Get-PropertyValue -Object $entry -Names @('provider'))
            }
            if ([string]::IsNullOrWhiteSpace($provider)) {
                $provider = 'unknown'
            }

            $day = $entryDate.ToString('yyyy-MM-dd')
            Add-ToBucket -Stats $stats -Day $day -Provider $provider -Usage $usage -Cost $cost
        }
    }

    foreach ($day in @($stats.Keys)) {
        foreach ($provider in @($stats[$day].Keys)) {
            if (-not $providerTotals.ContainsKey($provider)) {
                $providerTotals[$provider] = New-CostBucket
            }

            $source = $stats[$day][$provider]
            $target = $providerTotals[$provider]
            $target.total += $source.total
            $target.input += $source.input
            $target.output += $source.output
            $target.cacheRead += $source.cacheRead
            $target.cacheWrite += $source.cacheWrite
            $target.requests += $source.requests
            $target.assistantMessages += $source.assistantMessages
            $target.tokenRecords += $source.tokenRecords
            $target.inputTokens += $source.inputTokens
            $target.outputTokens += $source.outputTokens
            $target.cacheReadTokens += $source.cacheReadTokens
            $target.cacheWriteTokens += $source.cacheWriteTokens
        }
    }

    $grandTotal = [decimal]0
    foreach ($provider in @($providerTotals.Keys)) {
        $grandTotal += $providerTotals[$provider].total
    }

    $providerTotalsObjects = @()
    foreach ($provider in @($providerTotals.Keys | Sort-Object)) {
        $bucket = $providerTotals[$provider]
        $providerTotalsObjects += [ordered]@{
            provider = $provider
            total = $bucket.total
            input = $bucket.input
            output = $bucket.output
            cacheRead = $bucket.cacheRead
            cacheWrite = $bucket.cacheWrite
            requests = $bucket.requests
            assistantMessages = $bucket.assistantMessages
            tokenRecords = $bucket.tokenRecords
            inputTokens = $bucket.inputTokens
            outputTokens = $bucket.outputTokens
            cacheReadTokens = $bucket.cacheReadTokens
            cacheWriteTokens = $bucket.cacheWriteTokens
        }
    }

    if ($costRecords -eq 0) {
        $script:warnings += 'No usage.cost records were found. Current Tau CodingAgent JSONL sessions do not persist assistant usage costs, so this script does not infer or recalculate dollar costs.'
    }

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        directory = $workingDirectory
        days = $Days
        cutoffDate = $cutoff.ToString('yyyy-MM-dd')
        sessionFileCount = $sessionFiles.Count
        processedLineCount = $processedLines
        malformedLineCount = $malformedLineCount
        assistantMessagesWithUsage = $assistantMessages
        costRecords = $costRecords
        tokenRecords = $tokenRecords
        grandTotal = $grandTotal
        daysBreakdown = @(Convert-StatsToObjects -Stats $stats)
        providerTotals = @($providerTotalsObjects)
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host ''
        Write-Host "Cost breakdown for: $workingDirectory"
        Write-Host "Period: last $Days days (since $($cutoff.ToString('yyyy-MM-dd')))"
        Write-Host ('=' * 80)

        foreach ($day in @($stats.Keys | Sort-Object)) {
            Write-Host ''
            Write-Host $day
            Write-Host ('-' * 40)
            $dayTotal = [decimal]0
            foreach ($provider in @($stats[$day].Keys | Sort-Object)) {
                $bucket = $stats[$day][$provider]
                $dayTotal += $bucket.total
                if ($bucket.requests -gt 0) {
                    Write-Host ('  {0,-15} ${1,8}  ({2} cost reqs, in: ${3}, out: ${4}, cache: ${5})' -f $provider, (Format-Cost $bucket.total), $bucket.requests, (Format-Cost $bucket.input), (Format-Cost $bucket.output), (Format-Cost ($bucket.cacheRead + $bucket.cacheWrite)))
                }
                elseif ($bucket.tokenRecords -gt 0) {
                    Write-Host ('  {0,-15} no cost  ({1} token records, tokens in/out/cache: {2}/{3}/{4})' -f $provider, $bucket.tokenRecords, $bucket.inputTokens, $bucket.outputTokens, ($bucket.cacheReadTokens + $bucket.cacheWriteTokens))
                }
            }
            Write-Host ('  {0,-15} ${1,8}' -f 'Day total:', (Format-Cost $dayTotal))
        }

        Write-Host ''
        Write-Host ('=' * 80)
        Write-Host 'TOTALS BY PROVIDER'
        Write-Host ('-' * 40)
        foreach ($provider in @($providerTotals.Keys | Sort-Object)) {
            $bucket = $providerTotals[$provider]
            Write-Host ('  {0,-15} ${1,8}  ({2} cost reqs, {3} token records)' -f $provider, (Format-Cost $bucket.total), $bucket.requests, $bucket.tokenRecords)
        }
        Write-Host ('-' * 40)
        Write-Host ('  {0,-15} ${1,8}' -f 'GRAND TOTAL:', (Format-Cost $grandTotal))
        foreach ($warning in $script:warnings) {
            Write-Warning $warning
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        directory = $Directory
        days = $Days
        error = $_.Exception.Message
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau session cost report failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
