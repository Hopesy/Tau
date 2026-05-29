param(
    [string]$Cwd = (Get-Location).Path,
    [string[]]$SessionPath = @(),
    [string]$SessionsDirectory = '',
    [string[]]$ToolName = @('edit_file', 'edit'),
    [string]$Model = '',
    [string]$Extension = '',
    [string]$Since = '',
    [int]$Top = 20,
    [switch]$FailedOnly,
    [switch]$IncludeRecords,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
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

        foreach ($file in @(Get-ChildItem -LiteralPath $sessionDirectory -Filter '*.jsonl' -File -Recurse | Sort-Object FullName)) {
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
        foreach ($file in @(Get-ChildItem -LiteralPath $defaultSessionsDirectory -Filter '*.jsonl' -File -Recurse | Sort-Object FullName)) {
            Add-SessionPath -Paths $paths -Path $file.FullName
        }
    }

    return @($paths | Sort-Object)
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

function Get-StringValue {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string[]]$Names,
        [string]$Default = ''
    )

    $value = Get-PropertyValue -Object $Object -Names $Names
    if ($null -eq $value) {
        return $Default
    }

    return [string]$value
}

function Convert-ToTimestamp {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    try {
        return [DateTimeOffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $null
    }
}

function Get-PathExtension {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return '[unknown]'
    }

    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($extension)) {
        return $extension
    }

    $fileName = [System.IO.Path]::GetFileName($Path)
    if ($fileName.StartsWith('.') -and -not $fileName.Substring(1).Contains('.')) {
        return $fileName.ToLowerInvariant()
    }

    return '[no_ext]'
}

function Get-Utf8ByteCount {
    param([AllowNull()][string]$Value)
    return [System.Text.Encoding]::UTF8.GetByteCount($(if ($null -eq $Value) { '' } else { $Value }))
}

function Get-CommonPrefixLength {
    param([string]$Left, [string]$Right)

    $max = [Math]::Min($Left.Length, $Right.Length)
    $index = 0
    while ($index -lt $max -and $Left[$index] -eq $Right[$index]) {
        $index++
    }

    return $index
}

function Get-CommonSuffixLength {
    param([string]$Left, [string]$Right)

    $max = [Math]::Min($Left.Length, $Right.Length)
    $index = 0
    while ($index -lt $max -and $Left[$Left.Length - 1 - $index] -eq $Right[$Right.Length - 1 - $index]) {
        $index++
    }

    return $index
}

function Get-ReplacementStats {
    param([string]$OldText, [string]$NewText)

    $oldTextValue = if ($null -eq $OldText) { '' } else { $OldText }
    $newTextValue = if ($null -eq $NewText) { '' } else { $NewText }
    $prefixChars = Get-CommonPrefixLength -Left $oldTextValue -Right $newTextValue
    $oldRemainder = $oldTextValue.Substring($prefixChars)
    $newRemainder = $newTextValue.Substring($prefixChars)
    $suffixChars = Get-CommonSuffixLength -Left $oldRemainder -Right $newRemainder
    $oldCore = if ($suffixChars -gt 0) { $oldRemainder.Substring(0, $oldRemainder.Length - $suffixChars) } else { $oldRemainder }
    $newCore = if ($suffixChars -gt 0) { $newRemainder.Substring(0, $newRemainder.Length - $suffixChars) } else { $newRemainder }
    $prefix = $oldTextValue.Substring(0, $prefixChars)
    $suffix = if ($suffixChars -gt 0) { $oldRemainder.Substring($oldRemainder.Length - $suffixChars) } else { '' }

    $oldBytes = Get-Utf8ByteCount -Value $oldTextValue
    $newBytes = Get-Utf8ByteCount -Value $newTextValue
    $sharedPrefixBytes = Get-Utf8ByteCount -Value $prefix
    $sharedSuffixBytes = Get-Utf8ByteCount -Value $suffix
    $coreOldBytes = Get-Utf8ByteCount -Value $oldCore
    $coreNewBytes = Get-Utf8ByteCount -Value $newCore
    $coreBytes = $coreOldBytes + $coreNewBytes
    $totalEditBytes = $oldBytes + $newBytes

    return [ordered]@{
        oldBytes = $oldBytes
        newBytes = $newBytes
        totalEditBytes = $totalEditBytes
        sharedPrefixBytes = $sharedPrefixBytes
        sharedSuffixBytes = $sharedSuffixBytes
        sharedContextBytes = $sharedPrefixBytes + $sharedSuffixBytes
        coreOldBytes = $coreOldBytes
        coreNewBytes = $coreNewBytes
        coreBytes = $coreBytes
        wrapperPayloadBytes = $totalEditBytes - $coreBytes
        inflationRatio = if ($coreBytes -eq 0) { $null } else { [double]$totalEditBytes / [double]$coreBytes }
        noCoreChange = ($coreBytes -eq 0)
    }
}

function Convert-ToolArguments {
    param([AllowNull()][object]$Arguments)

    if ($null -eq $Arguments) {
        return [pscustomobject]@{}
    }

    if ($Arguments -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Arguments)) {
            return [pscustomobject]@{}
        }

        try {
            return $Arguments | ConvertFrom-Json
        }
        catch {
            $script:warnings += 'Skipped unparsable edit tool arguments JSON.'
            return [pscustomobject]@{}
        }
    }

    return $Arguments
}

function Get-ArgumentStyle {
    param([AllowNull()][object]$Arguments)

    $edits = Get-PropertyValue -Object $Arguments -Names @('edits')
    $oldText = Get-PropertyValue -Object $Arguments -Names @('oldText', 'newText')
    $oldString = Get-PropertyValue -Object $Arguments -Names @('old_string', 'new_string')
    if ($null -ne $edits) {
        return 'edits'
    }
    if ($null -ne $oldText -and $null -ne $oldString) {
        return 'mixed'
    }
    if ($null -ne $oldText) {
        return 'oldText/newText'
    }
    if ($null -ne $oldString) {
        return 'old_string/new_string'
    }
    return 'unknown'
}

function Get-EditTextPair {
    param([AllowNull()][object]$Arguments)

    $oldText = Get-PropertyValue -Object $Arguments -Names @('oldText', 'old_string')
    $newText = Get-PropertyValue -Object $Arguments -Names @('newText', 'new_string')
    return [ordered]@{
        oldText = if ($null -eq $oldText) { '' } else { [string]$oldText }
        newText = if ($null -eq $newText) { '' } else { [string]$newText }
    }
}

function Get-ToolArgumentStats {
    param([AllowNull()][object]$RawArguments)

    $arguments = Convert-ToolArguments -Arguments $RawArguments
    $path = Get-StringValue -Object $arguments -Names @('path', 'filePath', 'file_path')
    $extension = Get-PathExtension -Path $path
    $argStyle = Get-ArgumentStyle -Arguments $arguments
    $edits = Get-PropertyValue -Object $arguments -Names @('edits')

    if ($null -ne $edits) {
        $perEdit = @()
        foreach ($edit in @($edits)) {
            $pair = Get-EditTextPair -Arguments $edit
            $perEdit += Get-ReplacementStats -OldText $pair.oldText -NewText $pair.newText
        }

        $totals = [ordered]@{
            oldBytes = 0
            newBytes = 0
            totalEditBytes = 0
            sharedPrefixBytes = 0
            sharedSuffixBytes = 0
            sharedContextBytes = 0
            coreOldBytes = 0
            coreNewBytes = 0
            coreBytes = 0
            wrapperPayloadBytes = 0
            noCoreChangeCount = 0
        }

        foreach ($entry in $perEdit) {
            $totals.oldBytes += [int64]$entry.oldBytes
            $totals.newBytes += [int64]$entry.newBytes
            $totals.totalEditBytes += [int64]$entry.totalEditBytes
            $totals.sharedPrefixBytes += [int64]$entry.sharedPrefixBytes
            $totals.sharedSuffixBytes += [int64]$entry.sharedSuffixBytes
            $totals.sharedContextBytes += [int64]$entry.sharedContextBytes
            $totals.coreOldBytes += [int64]$entry.coreOldBytes
            $totals.coreNewBytes += [int64]$entry.coreNewBytes
            $totals.coreBytes += [int64]$entry.coreBytes
            $totals.wrapperPayloadBytes += [int64]$entry.wrapperPayloadBytes
            if ($entry.noCoreChange) {
                $totals.noCoreChangeCount++
            }
        }

        $inflations = @($perEdit | Where-Object { $null -ne $_.inflationRatio } | ForEach-Object { [double]$_.inflationRatio })
        return [ordered]@{
            path = $path
            extension = $extension
            mode = 'multi'
            argStyle = $argStyle
            editsCount = @($perEdit).Count
            oldBytes = $totals.oldBytes
            newBytes = $totals.newBytes
            totalEditBytes = $totals.totalEditBytes
            sharedPrefixBytes = $totals.sharedPrefixBytes
            sharedSuffixBytes = $totals.sharedSuffixBytes
            sharedContextBytes = $totals.sharedContextBytes
            coreOldBytes = $totals.coreOldBytes
            coreNewBytes = $totals.coreNewBytes
            coreBytes = $totals.coreBytes
            wrapperPayloadBytes = $totals.wrapperPayloadBytes
            inflationRatio = if ($totals.coreBytes -eq 0) { $null } else { [double]$totals.totalEditBytes / [double]$totals.coreBytes }
            noCoreChange = ($totals.coreBytes -eq 0)
            noCoreChangeCount = $totals.noCoreChangeCount
            medianEditInflationRatio = Get-Quantile -Numbers $inflations -Q 0.5
            maxEditInflationRatio = if ($inflations.Count -eq 0) { $null } else { ($inflations | Measure-Object -Maximum).Maximum }
        }
    }

    $singlePair = Get-EditTextPair -Arguments $arguments
    $replacement = Get-ReplacementStats -OldText $singlePair.oldText -NewText $singlePair.newText
    return [ordered]@{
        path = $path
        extension = $extension
        mode = 'single'
        argStyle = $argStyle
        editsCount = 1
        oldBytes = $replacement.oldBytes
        newBytes = $replacement.newBytes
        totalEditBytes = $replacement.totalEditBytes
        sharedPrefixBytes = $replacement.sharedPrefixBytes
        sharedSuffixBytes = $replacement.sharedSuffixBytes
        sharedContextBytes = $replacement.sharedContextBytes
        coreOldBytes = $replacement.coreOldBytes
        coreNewBytes = $replacement.coreNewBytes
        coreBytes = $replacement.coreBytes
        wrapperPayloadBytes = $replacement.wrapperPayloadBytes
        inflationRatio = $replacement.inflationRatio
        noCoreChange = $replacement.noCoreChange
        noCoreChangeCount = if ($replacement.noCoreChange) { 1 } else { 0 }
        medianEditInflationRatio = $replacement.inflationRatio
        maxEditInflationRatio = $replacement.inflationRatio
    }
}

function Get-Quantile {
    param(
        [AllowEmptyCollection()]
        [double[]]$Numbers,
        [double]$Q
    )

    $finite = @($Numbers | Where-Object { -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_) } | Sort-Object)
    if ($finite.Count -eq 0) {
        return $null
    }
    if ($finite.Count -eq 1) {
        return $finite[0]
    }

    $position = ($finite.Count - 1) * $Q
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $finite[$lower]
    }

    $weight = $position - $lower
    return ($finite[$lower] * (1 - $weight)) + ($finite[$upper] * $weight)
}

function Get-ContentText {
    param([AllowNull()][object]$Content)

    if ($null -eq $Content) {
        return ''
    }

    if ($Content -is [string]) {
        return $Content
    }

    $parts = @()
    foreach ($part in @($Content)) {
        if ($part -is [string]) {
            $parts += $part
            continue
        }

        $type = Get-StringValue -Object $part -Names @('type')
        $text = Get-StringValue -Object $part -Names @('text')
        if ($type.Equals('text', [StringComparison]::OrdinalIgnoreCase) -and -not [string]::IsNullOrWhiteSpace($text)) {
            $parts += $text
        }
    }

    return ($parts -join "`n")
}

function Get-ErrorKind {
    param(
        [string]$Text,
        [bool]$IsError,
        [bool]$MatchedResult
    )

    if (-not $MatchedResult) {
        return 'missing_result'
    }
    if (-not $IsError) {
        return $null
    }

    $normalized = $Text.ToLowerInvariant()
    if ($normalized.Contains('file not found')) { return 'file_not_found' }
    if ($normalized.Contains('old_string not found') -or $normalized.Contains('could not find the exact text')) { return 'not_found_exact_text' }
    if ($normalized.Contains('found multiple occurrences') -or $normalized.Contains('found ') -and $normalized.Contains(' times')) { return 'multiple_occurrences' }
    if ($normalized.Contains('no changes made')) { return 'no_changes_made' }
    if ($normalized.Contains('input is invalid')) { return 'invalid_input' }
    if ($normalized.Contains('must not overlap')) { return 'overlapping_edits' }
    if ($normalized.Contains('aborted') -or $normalized.Contains('cancelled')) { return 'aborted' }
    return 'other'
}

function New-CountRows {
    param(
        [AllowEmptyCollection()]
        [object[]]$Records,
        [Parameter(Mandatory = $true)]
        [scriptblock]$KeySelector,
        [string]$KeyName = 'key'
    )

    $groups = @{}
    foreach ($record in $Records) {
        $key = [string](& $KeySelector $record)
        if ([string]::IsNullOrWhiteSpace($key)) {
            $key = '[unknown]'
        }
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = 0
        }
        $groups[$key]++
    }

    $rows = @()
    foreach ($key in @($groups.Keys | Sort-Object)) {
        $rows += [ordered]@{
            $KeyName = $key
            count = $groups[$key]
        }
    }

    return @($rows | Sort-Object -Property @{ Expression = 'count'; Descending = $true }, $KeyName)
}

function Get-UniqueStringCount {
    param([AllowEmptyCollection()][object[]]$Values)

    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        if ($null -eq $value) {
            continue
        }

        $text = [string]$value
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $set.Add($text) | Out-Null
    }

    return $set.Count
}

function New-GroupStats {
    param(
        [AllowEmptyCollection()]
        [object[]]$Records,
        [Parameter(Mandatory = $true)]
        [scriptblock]$KeySelector
    )

    $groups = @{}
    foreach ($record in $Records) {
        $key = [string](& $KeySelector $record)
        if ([string]::IsNullOrWhiteSpace($key)) {
            $key = '[unknown]'
        }
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = @()
        }
        $groups[$key] += $record
    }

    $rows = @()
    foreach ($key in @($groups.Keys | Sort-Object)) {
        $group = @($groups[$key])
        $resolved = @($group | Where-Object { $null -ne $_.success })
        $success = @($resolved | Where-Object { $_.success -eq $true }).Count
        $failed = @($resolved | Where-Object { $_.success -eq $false }).Count
        $multi = @($group | Where-Object { $_.mode -eq 'multi' }).Count
        $inflations = @($group | Where-Object { $null -ne $_.inflationRatio } | ForEach-Object { [double]$_.inflationRatio })
        $rows += [ordered]@{
            key = $key
            calls = $group.Count
            resolved = $resolved.Count
            success = $success
            failed = $failed
            unresolved = $group.Count - $resolved.Count
            multi = $multi
            multiRate = if ($group.Count -eq 0) { $null } else { [double]$multi / [double]$group.Count }
            successRate = if ($resolved.Count -eq 0) { $null } else { [double]$success / [double]$resolved.Count }
            medianInflation = Get-Quantile -Numbers $inflations -Q 0.5
            p95Inflation = Get-Quantile -Numbers $inflations -Q 0.95
        }
    }

    return @($rows | Sort-Object -Property @{ Expression = 'calls'; Descending = $true }, 'key')
}

function New-InflationBuckets {
    param([AllowEmptyCollection()][object[]]$Records)

    $bucketDefinitions = @(
        [ordered]@{ key = 'no_core_change'; label = 'no-core-change' },
        [ordered]@{ key = 'lt4'; label = '<4x' },
        [ordered]@{ key = '4to10'; label = '4-10x' },
        [ordered]@{ key = '10to25'; label = '10-25x' },
        [ordered]@{ key = 'gte25'; label = '25x+' }
    )

    $rows = @()
    foreach ($bucket in $bucketDefinitions) {
        $bucketRecords = switch ($bucket.key) {
            'no_core_change' { @($Records | Where-Object { $null -eq $_.inflationRatio }) }
            'lt4' { @($Records | Where-Object { $null -ne $_.inflationRatio -and $_.inflationRatio -lt 4 }) }
            '4to10' { @($Records | Where-Object { $null -ne $_.inflationRatio -and $_.inflationRatio -ge 4 -and $_.inflationRatio -lt 10 }) }
            '10to25' { @($Records | Where-Object { $null -ne $_.inflationRatio -and $_.inflationRatio -ge 10 -and $_.inflationRatio -lt 25 }) }
            default { @($Records | Where-Object { $null -ne $_.inflationRatio -and $_.inflationRatio -ge 25 }) }
        }
        $resolved = @($bucketRecords | Where-Object { $null -ne $_.success })
        $failed = @($resolved | Where-Object { $_.success -eq $false }).Count
        $rows += [ordered]@{
            key = $bucket.key
            label = $bucket.label
            count = $bucketRecords.Count
            resolved = $resolved.Count
            failed = $failed
            failureRate = if ($resolved.Count -eq 0) { $null } else { [double]$failed / [double]$resolved.Count }
        }
    }

    return $rows
}

function New-SameFileClusterStats {
    param([AllowEmptyCollection()][object[]]$Records)

    $groups = @{}
    foreach ($record in $Records) {
        $key = "$($record.sessionFile)::$(if ($record.assistantEntryId) { $record.assistantEntryId } else { '[none]' })::$($record.path)"
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = @()
        }
        $groups[$key] += $record
    }

    $clusters = @($groups.Values | Where-Object { @($_).Count -ge 2 })
    $assistantMessagesWithCluster = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assistantMessagesWithMultiEdit = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $callsInsideClusters = 0

    foreach ($cluster in $clusters) {
        $items = @($cluster)
        if ($items.Count -gt 0) {
            $assistantMessagesWithCluster.Add("$($items[0].sessionFile)::$($items[0].assistantEntryId)") | Out-Null
            $callsInsideClusters += $items.Count
        }
    }

    foreach ($record in @($Records | Where-Object { $_.mode -eq 'multi' -and $_.editsCount -gt 1 })) {
        $assistantMessagesWithMultiEdit.Add("$($record.sessionFile)::$($record.assistantEntryId)") | Out-Null
    }

    return [ordered]@{
        clustersCount = $clusters.Count
        assistantMessagesWithCluster = $assistantMessagesWithCluster.Count
        assistantMessagesWithMultiEdit = $assistantMessagesWithMultiEdit.Count
        callsInsideClusters = $callsInsideClusters
        averageCallsPerCluster = if ($clusters.Count -eq 0) { $null } else { [double]$callsInsideClusters / [double]$clusters.Count }
        ratioClusterAssistantMessagesToMultiEditAssistantMessages = if ($assistantMessagesWithMultiEdit.Count -eq 0) { $null } else { [double]$assistantMessagesWithCluster.Count / [double]$assistantMessagesWithMultiEdit.Count }
    }
}

function New-WorstExamples {
    param(
        [AllowEmptyCollection()]
        [object[]]$Records,
        [int]$Limit
    )

    return @($Records | Sort-Object `
        @{ Expression = { if ($null -eq $_.inflationRatio) { [double]::PositiveInfinity } else { [double]$_.inflationRatio } }; Descending = $true },
        @{ Expression = { [int64]$_.totalEditBytes }; Descending = $true },
        'path' |
        Select-Object -First $Limit |
        ForEach-Object {
            [ordered]@{
                toolName = $_.toolName
                providerModel = $_.providerModel
                extension = $_.extension
                path = $_.path
                inflationRatio = $_.inflationRatio
                totalEditBytes = $_.totalEditBytes
                coreBytes = $_.coreBytes
                mode = $_.mode
                editsCount = $_.editsCount
                failed = ($_.success -eq $false)
                errorKind = $_.errorKind
                sessionFile = $_.sessionFile
            }
        })
}

function Read-EditToolRecords {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$SessionFiles,
        [Parameter(Mandatory = $true)]
        [string[]]$ToolNames
    )

    $records = @()
    $sessionFilesWithEditCalls = 0
    $malformedLineCount = 0
    $unmatchedToolResultCount = 0
    $toolNameSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ToolNames) {
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $toolNameSet.Add($name.Trim()) | Out-Null
        }
    }

    foreach ($file in $SessionFiles) {
        $pending = @{}
        $fileHadEditCall = $false
        $lineNumber = 0

        foreach ($line in [System.IO.File]::ReadLines($file)) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $entry = $line | ConvertFrom-Json
            }
            catch {
                $malformedLineCount++
                $script:warnings += "Skipped malformed JSONL line $lineNumber in $file"
                continue
            }

            if (-not (Get-StringValue -Object $entry -Names @('type')).Equals('message', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $message = Get-PropertyValue -Object $entry -Names @('message')
            if ($null -eq $message) {
                continue
            }

            $role = Get-StringValue -Object $message -Names @('role')
            if ($role.Equals('assistant', [StringComparison]::OrdinalIgnoreCase)) {
                foreach ($block in @((Get-PropertyValue -Object $message -Names @('content')))) {
                    if ($null -eq $block) {
                        continue
                    }

                    $blockType = Get-StringValue -Object $block -Names @('type')
                    $blockName = Get-StringValue -Object $block -Names @('name')
                    if (-not $blockType.Equals('toolCall', [StringComparison]::OrdinalIgnoreCase) -or -not $toolNameSet.Contains($blockName)) {
                        continue
                    }

                    $fileHadEditCall = $true
                    $argumentStats = Get-ToolArgumentStats -RawArguments (Get-PropertyValue -Object $block -Names @('arguments'))
                    $provider = Get-StringValue -Object $message -Names @('provider') -Default (Get-StringValue -Object $entry -Names @('provider') -Default '[unknown]')
                    $model = Get-StringValue -Object $message -Names @('model') -Default (Get-StringValue -Object $entry -Names @('model') -Default '[unknown]')
                    $toolCallId = Get-StringValue -Object $block -Names @('id')

                    $record = [ordered]@{
                        sessionFile = $file
                        lineNumber = $lineNumber
                        assistantEntryId = Get-StringValue -Object $entry -Names @('id')
                        toolCallId = $toolCallId
                        timestamp = Get-StringValue -Object $entry -Names @('timestamp')
                        timestampValue = Convert-ToTimestamp (Get-PropertyValue -Object $entry -Names @('timestamp'))
                        api = Get-StringValue -Object $message -Names @('api') -Default $null
                        provider = $provider
                        model = $model
                        providerModel = "$provider/$model"
                        toolName = $blockName
                        path = $argumentStats.path
                        extension = $argumentStats.extension
                        mode = $argumentStats.mode
                        argStyle = $argumentStats.argStyle
                        editsCount = $argumentStats.editsCount
                        oldBytes = $argumentStats.oldBytes
                        newBytes = $argumentStats.newBytes
                        totalEditBytes = $argumentStats.totalEditBytes
                        sharedPrefixBytes = $argumentStats.sharedPrefixBytes
                        sharedSuffixBytes = $argumentStats.sharedSuffixBytes
                        sharedContextBytes = $argumentStats.sharedContextBytes
                        coreOldBytes = $argumentStats.coreOldBytes
                        coreNewBytes = $argumentStats.coreNewBytes
                        coreBytes = $argumentStats.coreBytes
                        wrapperPayloadBytes = $argumentStats.wrapperPayloadBytes
                        inflationRatio = $argumentStats.inflationRatio
                        noCoreChange = $argumentStats.noCoreChange
                        noCoreChangeCount = $argumentStats.noCoreChangeCount
                        medianEditInflationRatio = $argumentStats.medianEditInflationRatio
                        maxEditInflationRatio = $argumentStats.maxEditInflationRatio
                        success = $null
                        errorKind = $null
                        errorText = ''
                        resultSummary = ''
                        matchedResult = $false
                    }

                    $recordObject = [pscustomobject]$record
                    $records += $recordObject
                    if (-not [string]::IsNullOrWhiteSpace($toolCallId)) {
                        $pending[$toolCallId] = $recordObject
                    }
                }
            }

            if ($role.Equals('toolResult', [StringComparison]::OrdinalIgnoreCase)) {
                $toolCallId = Get-StringValue -Object $message -Names @('toolCallId', 'tool_call_id')
                if ([string]::IsNullOrWhiteSpace($toolCallId) -or -not $pending.ContainsKey($toolCallId)) {
                    $toolName = Get-StringValue -Object $message -Names @('toolName', 'tool_name')
                    if ($toolNameSet.Contains($toolName)) {
                        $unmatchedToolResultCount++
                    }
                    continue
                }

                $record = $pending[$toolCallId]
                $isError = [bool](Get-PropertyValue -Object $message -Names @('isError', 'is_error'))
                $text = Get-ContentText -Content (Get-PropertyValue -Object $message -Names @('content'))
                $record.matchedResult = $true
                $record.success = -not $isError
                $record.resultSummary = $text
                $record.errorText = if ($isError) { $text } else { '' }
                $record.errorKind = Get-ErrorKind -Text $text -IsError $isError -MatchedResult $true
                $pending.Remove($toolCallId)
            }
        }

        foreach ($record in $pending.Values) {
            $record.matchedResult = $false
            $record.success = $null
            $record.errorKind = Get-ErrorKind -Text '' -IsError $false -MatchedResult $false
        }

        if ($fileHadEditCall) {
            $sessionFilesWithEditCalls++
        }
    }

    return [ordered]@{
        records = @($records)
        sessionFilesWithEditCalls = $sessionFilesWithEditCalls
        malformedLineCount = $malformedLineCount
        unmatchedToolResultCount = $unmatchedToolResultCount
    }
}

function Select-FilteredRecords {
    param(
        [AllowEmptyCollection()]
        [object[]]$Records,
        [AllowNull()]
        [Nullable[DateTimeOffset]]$SinceValue
    )

    return @($Records | Where-Object {
        if (-not [string]::IsNullOrWhiteSpace($Model) -and $_.providerModel.IndexOf($Model, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
        if (-not [string]::IsNullOrWhiteSpace($Extension) -and -not $_.extension.Equals($Extension.ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        if ($FailedOnly -and $_.success -ne $false) {
            return $false
        }
        if ($null -ne $SinceValue -and $null -ne $_.timestampValue -and $_.timestampValue -lt $SinceValue) {
            return $false
        }
        return $true
    })
}

function New-Summary {
    param(
        [AllowEmptyCollection()]
        [object[]]$Records,
        [object]$Scan,
        [string]$WorkingDirectory,
        [string[]]$SessionFiles,
        [AllowNull()]
        [Nullable[DateTimeOffset]]$SinceValue
    )

    $resolved = @($Records | Where-Object { $null -ne $_.success })
    $success = @($resolved | Where-Object { $_.success -eq $true }).Count
    $failed = @($resolved | Where-Object { $_.success -eq $false }).Count
    $single = @($Records | Where-Object { $_.mode -eq 'single' }).Count
    $multi = @($Records | Where-Object { $_.mode -eq 'multi' }).Count
    $inflations = @($Records | Where-Object { $null -ne $_.inflationRatio } | ForEach-Object { [double]$_.inflationRatio })
    $pathologicalThresholds = @(10, 25, 100 | ForEach-Object {
        $threshold = $_
        [ordered]@{
            threshold = $threshold
            count = @($Records | Where-Object { $null -ne $_.inflationRatio -and $_.inflationRatio -ge $threshold }).Count
        }
    })
    $hugeReplacements = @(1024, 4096, 16384, 65536 | ForEach-Object {
        $threshold = $_
        [ordered]@{
            threshold = $threshold
            count = @($Records | Where-Object { $_.totalEditBytes -gt $threshold }).Count
        }
    })

    return [ordered]@{
        schemaVersion = 1
        succeeded = $true
        filters = [ordered]@{
            toolNames = @($ToolName)
            model = if ([string]::IsNullOrWhiteSpace($Model)) { $null } else { $Model }
            extension = if ([string]::IsNullOrWhiteSpace($Extension)) { $null } else { $Extension.ToLowerInvariant() }
            failedOnly = $FailedOnly.IsPresent
            since = if ($null -eq $SinceValue) { $null } else { $SinceValue.ToString('O') }
        }
        scan = [ordered]@{
            cwd = $WorkingDirectory
            sessionFiles = @($SessionFiles)
            sessionFileCount = @($SessionFiles).Count
            sessionFilesWithEditCalls = $Scan.sessionFilesWithEditCalls
            malformedLineCount = $Scan.malformedLineCount
            unmatchedToolResultCount = $Scan.unmatchedToolResultCount
            warnings = $script:warnings
        }
        counts = [ordered]@{
            assistantMessagesWithEditCalls = Get-UniqueStringCount -Values @($Records | ForEach-Object { "$($_.sessionFile)::$($_.assistantEntryId)" })
            totalEditCalls = $Records.Count
            resolvedEditCalls = $resolved.Count
            success = $success
            failed = $failed
            unresolved = $Records.Count - $resolved.Count
            single = $single
            multi = $multi
            noCoreChange = @($Records | Where-Object { $null -eq $_.inflationRatio }).Count
        }
        modeStats = @(
            foreach ($mode in @('single', 'multi')) {
                $modeRecords = @($Records | Where-Object { $_.mode -eq $mode })
                $modeResolved = @($modeRecords | Where-Object { $null -ne $_.success })
                $modeSuccess = @($modeResolved | Where-Object { $_.success -eq $true }).Count
                $modeFailed = @($modeResolved | Where-Object { $_.success -eq $false }).Count
                [ordered]@{
                    mode = $mode
                    calls = $modeRecords.Count
                    resolved = $modeResolved.Count
                    success = $modeSuccess
                    failed = $modeFailed
                    unresolved = $modeRecords.Count - $modeResolved.Count
                    successRate = if ($modeResolved.Count -eq 0) { $null } else { [double]$modeSuccess / [double]$modeResolved.Count }
                    failureRate = if ($modeResolved.Count -eq 0) { $null } else { [double]$modeFailed / [double]$modeResolved.Count }
                }
            }
        )
        argStyles = @(New-CountRows -Records $Records -KeySelector { param($record) $record.argStyle } -KeyName 'style')
        toolNameStats = @(New-GroupStats -Records $Records -KeySelector { param($record) $record.toolName })
        providerStats = @(New-GroupStats -Records $Records -KeySelector { param($record) $record.providerModel })
        extensionStats = @(New-GroupStats -Records $Records -KeySelector { param($record) $record.extension })
        inflation = [ordered]@{
            median = Get-Quantile -Numbers $inflations -Q 0.5
            p90 = Get-Quantile -Numbers $inflations -Q 0.9
            p95 = Get-Quantile -Numbers $inflations -Q 0.95
            p99 = Get-Quantile -Numbers $inflations -Q 0.99
            pathologicalThresholds = $pathologicalThresholds
            hugeReplacements = $hugeReplacements
            failureByBucket = @(New-InflationBuckets -Records $Records)
        }
        sameFileClusters = New-SameFileClusterStats -Records $Records
        failureKinds = @(New-CountRows -Records @($Records | Where-Object { $_.success -eq $false }) -KeySelector { param($record) if ($record.errorKind) { $record.errorKind } else { 'other' } } -KeyName 'kind')
        worstExamples = @(New-WorstExamples -Records $Records -Limit $Top)
        records = if ($IncludeRecords) { @($Records | ForEach-Object {
            [ordered]@{
                sessionFile = $_.sessionFile
                lineNumber = $_.lineNumber
                assistantEntryId = $_.assistantEntryId
                toolCallId = $_.toolCallId
                timestamp = $_.timestamp
                providerModel = $_.providerModel
                toolName = $_.toolName
                path = $_.path
                extension = $_.extension
                mode = $_.mode
                argStyle = $_.argStyle
                editsCount = $_.editsCount
                totalEditBytes = $_.totalEditBytes
                coreBytes = $_.coreBytes
                inflationRatio = $_.inflationRatio
                success = $_.success
                errorKind = $_.errorKind
                matchedResult = $_.matchedResult
            }
        }) } else { $null }
        remainingGaps = @(
            'Tau records the built-in CodingAgent edit tool as edit_file; this report accepts both Tau edit_file and upstream edit tool names.',
            'This is a local JSONL audit utility; it does not change edit tool runtime behavior or prove external provider e2e parity.'
        )
    }
}

function Format-Percent {
    param(
        [int]$Part,
        [int]$Total
    )

    if ($Total -eq 0) {
        return 'n/a'
    }

    return (($Part / $Total) * 100).ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture) + '%'
}

function Format-Ratio {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return 'no-core-change'
    }

    $number = [double]$Value
    if ($number -ge 100) { return $number.ToString('0', [System.Globalization.CultureInfo]::InvariantCulture) + 'x' }
    if ($number -ge 10) { return $number.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture) + 'x' }
    return $number.ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture) + 'x'
}

try {
    if ($Top -le 0) {
        throw 'Top must be greater than zero.'
    }

    $workingDirectory = Convert-ToFullPath -Path $Cwd -BasePath $invocationDirectory
    $sessionFiles = @(Get-SessionFiles -WorkingDirectory $workingDirectory)
    if ($sessionFiles.Count -eq 0) {
        throw "No Tau JSONL sessions found for $workingDirectory"
    }

    $sinceValue = $null
    if (-not [string]::IsNullOrWhiteSpace($Since)) {
        try {
            $sinceValue = [DateTimeOffset]::Parse($Since, [System.Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            throw "Since must be a valid ISO timestamp. Actual: $Since"
        }
    }

    $scan = Read-EditToolRecords -SessionFiles $sessionFiles -ToolNames $ToolName
    $records = Select-FilteredRecords -Records @($scan.records) -SinceValue $sinceValue
    $result = New-Summary -Records $records -Scan $scan -WorkingDirectory $workingDirectory -SessionFiles $sessionFiles -SinceValue $sinceValue

    if (-not $IncludeRecords) {
        $result.Remove('records')
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host "Scanned $($result.scan.sessionFileCount) Tau JSONL session file(s)"
        Write-Host "Found $($result.counts.totalEditCalls) edit tool call(s)"
        Write-Host ''
        Write-Host 'Success rate'
        Write-Host "  success:    $($result.counts.success)  $(Format-Percent -Part $result.counts.success -Total $result.counts.resolvedEditCalls)"
        Write-Host "  failed:     $($result.counts.failed)  $(Format-Percent -Part $result.counts.failed -Total $result.counts.resolvedEditCalls)"
        Write-Host "  unresolved: $($result.counts.unresolved)"
        Write-Host ''
        Write-Host 'Mode usage'
        Write-Host "  single replacement: $($result.counts.single)  $(Format-Percent -Part $result.counts.single -Total $result.counts.totalEditCalls)"
        Write-Host "  multi-edit:         $($result.counts.multi)  $(Format-Percent -Part $result.counts.multi -Total $result.counts.totalEditCalls)"
        Write-Host ''
        Write-Host "Context inflation: median $(Format-Ratio $result.inflation.median), p95 $(Format-Ratio $result.inflation.p95)"
        if (@($result.failureKinds).Count -gt 0) {
            Write-Host ''
            Write-Host 'Failures by kind'
            foreach ($failure in @($result.failureKinds)) {
                Write-Host "  $($failure.kind): $($failure.count)"
            }
        }
        foreach ($warning in $script:warnings) {
            Write-Warning $warning
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        cwd = $Cwd
        error = $_.Exception.Message
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau edit tool stats report failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
