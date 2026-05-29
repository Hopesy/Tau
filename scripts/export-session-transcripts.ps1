param(
    [string]$Cwd = (Get-Location).Path,
    [string]$OutputDirectory = 'session-transcripts',
    [string[]]$SessionPath = @(),
    [string]$SessionsDirectory = '',
    [int]$MaxCharsPerFile = 100000,
    [switch]$Analyze,
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

function Read-JsonlEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $entries = @()
    $malformed = 0
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $entries += ($line | ConvertFrom-Json)
        }
        catch {
            $malformed++
            $script:warnings += "Skipped malformed JSONL line $lineNumber in $Path"
        }
    }

    return [ordered]@{
        entries = $entries
        malformedLineCount = $malformed
    }
}

function Get-ContentText {
    param(
        [AllowNull()]
        [object]$Content
    )

    if ($null -eq $Content) {
        return ''
    }

    if ($Content -is [string]) {
        return $Content
    }

    $parts = @()
    foreach ($part in @($Content)) {
        if ($null -eq $part) {
            continue
        }

        if ($part -is [string]) {
            $parts += $part
            continue
        }

        $type = [string]$part.type
        $text = [string]$part.text
        if ($type.Equals('text', [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::IsNullOrWhiteSpace($text)) {
            $parts += $text
        }
    }

    return ($parts -join "`n")
}

function Read-Transcript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $read = Read-JsonlEntries -Path $Path
    $messages = @()
    foreach ($entry in @($read.entries)) {
        if (-not ([string]$entry.type).Equals('message', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $message = $entry.message
        if ($null -eq $message) {
            continue
        }

        $role = [string]$message.role
        if (-not $role.Equals('user', [StringComparison]::OrdinalIgnoreCase) -and
            -not $role.Equals('assistant', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $text = (Get-ContentText -Content $message.content).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $messages += [ordered]@{
            role = $role.ToUpperInvariant()
            text = $text
        }
    }

    if ($messages.Count -eq 0) {
        return [ordered]@{
            path = $Path
            transcript = ''
            messageCount = 0
            malformedLineCount = $read.malformedLineCount
        }
    }

    $chunks = @()
    foreach ($message in $messages) {
        $chunks += "[$($message.role)]`n$($message.text)"
    }

    $fileName = [System.IO.Path]::GetFileName($Path)
    return [ordered]@{
        path = $Path
        transcript = "=== SESSION: $fileName ===`n$($chunks -join "`n---`n")`n=== END SESSION ==="
        messageCount = $messages.Count
        malformedLineCount = $read.malformedLineCount
    }
}

function Write-TranscriptFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Transcripts,
        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $currentContent = ''
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $outputFiles = [System.Collections.Generic.List[object]]::new()
    $fileIndex = 0

    foreach ($transcript in $Transcripts) {
        if ($currentContent.Length -gt 0 -and
            ($currentContent.Length + $transcript.Length + 2) -gt $MaxCharsPerFile) {
            $name = 'session-transcripts-{0}.txt' -f $fileIndex.ToString('000')
            $path = Join-Path $OutputRoot $name
            [System.IO.File]::WriteAllText($path, $currentContent, $utf8NoBom)
            $outputFiles.Add([ordered]@{
                name = $name
                path = $path
                charCount = $currentContent.Length
                oversized = $false
            }) | Out-Null
            $fileIndex++
            $currentContent = ''
        }

        if ($transcript.Length -gt $MaxCharsPerFile) {
            if ($currentContent.Length -gt 0) {
                $name = 'session-transcripts-{0}.txt' -f $fileIndex.ToString('000')
                $path = Join-Path $OutputRoot $name
                [System.IO.File]::WriteAllText($path, $currentContent, $utf8NoBom)
                $outputFiles.Add([ordered]@{
                    name = $name
                    path = $path
                    charCount = $currentContent.Length
                    oversized = $false
                }) | Out-Null
                $fileIndex++
                $currentContent = ''
            }

            $name = 'session-transcripts-{0}.txt' -f $fileIndex.ToString('000')
            $path = Join-Path $OutputRoot $name
            [System.IO.File]::WriteAllText($path, $transcript, $utf8NoBom)
            $outputFiles.Add([ordered]@{
                name = $name
                path = $path
                charCount = $transcript.Length
                oversized = $true
            }) | Out-Null
            $fileIndex++
            continue
        }

        $currentContent = if ($currentContent.Length -eq 0) {
            $transcript
        }
        else {
            "$currentContent`n`n$transcript"
        }
    }

    if ($currentContent.Length -gt 0) {
        $name = 'session-transcripts-{0}.txt' -f $fileIndex.ToString('000')
        $path = Join-Path $OutputRoot $name
        [System.IO.File]::WriteAllText($path, $currentContent, $utf8NoBom)
        $outputFiles.Add([ordered]@{
            name = $name
            path = $path
            charCount = $currentContent.Length
            oversized = $false
        }) | Out-Null
    }

    return @($outputFiles)
}

try {
    if ($MaxCharsPerFile -le 0) {
        throw 'MaxCharsPerFile must be greater than zero.'
    }

    $workingDirectory = Convert-ToFullPath -Path $Cwd -BasePath $invocationDirectory
    $outputRoot = Convert-ToFullPath -Path $OutputDirectory -BasePath $invocationDirectory
    $sessionFiles = @(Get-SessionFiles -WorkingDirectory $workingDirectory)
    if ($sessionFiles.Count -eq 0) {
        throw "No Tau JSONL sessions found for $workingDirectory"
    }

    if ($Analyze) {
        $script:warnings += 'Analyze was requested, but Tau does not yet implement upstream subagent transcript analysis because the equivalent JSON mode analysis contract is still open.'
    }

    $parsedSessions = @()
    $transcripts = @()
    $messageCount = 0
    $malformedLineCount = 0
    foreach ($file in $sessionFiles) {
        $session = Read-Transcript -Path $file
        $parsedSessions += [ordered]@{
            path = $session.path
            messageCount = $session.messageCount
            malformedLineCount = $session.malformedLineCount
            included = (-not [string]::IsNullOrWhiteSpace($session.transcript))
        }
        $messageCount += [int]$session.messageCount
        $malformedLineCount += [int]$session.malformedLineCount
        if (-not [string]::IsNullOrWhiteSpace($session.transcript)) {
            $transcripts += $session.transcript
        }
    }

    if ($transcripts.Count -eq 0) {
        throw "No user or assistant text messages found in $($sessionFiles.Count) Tau session file(s)."
    }

    $outputFiles = @(Write-TranscriptFiles -Transcripts $transcripts -OutputRoot $outputRoot)
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        cwd = $workingDirectory
        outputDirectory = $outputRoot
        maxCharsPerFile = $MaxCharsPerFile
        sessionFileCount = $sessionFiles.Count
        transcriptSessionCount = $transcripts.Count
        messageCount = $messageCount
        malformedLineCount = $malformedLineCount
        sessions = $parsedSessions
        outputFiles = $outputFiles
        analysis = [ordered]@{
            requested = $Analyze.IsPresent
            ran = $false
            reason = if ($Analyze) { 'not-implemented' } else { 'not-requested' }
        }
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host "Found $($sessionFiles.Count) Tau session file(s) for $workingDirectory"
        foreach ($file in $outputFiles) {
            $oversized = if ($file.oversized) { ' oversized' } else { '' }
            Write-Host "Wrote $($file.name) ($($file.charCount) chars$oversized)"
        }
        Write-Host "Created $($outputFiles.Count) transcript file(s) in $outputRoot"
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
        outputDirectory = $OutputDirectory
        error = $_.Exception.Message
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau session transcript export failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
