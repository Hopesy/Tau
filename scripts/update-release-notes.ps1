param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,
    [string]$NotesPath = 'docs/releases/feature-release-notes.md',
    [string]$Date = '',
    [string]$FeatureDomain = 'Release',
    [string]$UserValue = '',
    [string]$ChangeSummary = '',
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$semverPattern = '^\d+\.\d+\.\d+$'
if ($Version -notmatch $semverPattern) {
    throw "Version must use x.y.z semver format. Actual: $Version"
}

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

function Convert-ToTableCell {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ''
    }

    $cell = $Value.Trim()
    $cell = $cell -replace '\r?\n', '<br>'
    $cell = $cell -replace '\|', '\|'
    return $cell
}

function Get-ReleaseDate {
    if ([string]::IsNullOrWhiteSpace($Date)) {
        return (Get-Date).ToString('yyyy-MM-dd')
    }

    if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "Date must use yyyy-MM-dd format. Actual: $Date"
    }

    [void][datetime]::ParseExact($Date, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
    return $Date
}

function Get-MarkdownTableTemplate {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    for ($i = 0; $i -lt ($Lines.Count - 1); $i++) {
        $line = $Lines[$i]
        $next = $Lines[$i + 1]
        if ($line -match '^\|.*\|$' -and $line -notmatch '---' -and $next -match '^\|\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$') {
            return [ordered]@{
                header = $line
                separator = $next
            }
        }
    }

    return [ordered]@{
        header = '| Date | Area | User value | Change summary |'
        separator = '| --- | --- | --- | --- |'
    }
}

function Get-InsertPlan {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$Lines,
        [Parameter(Mandatory = $true)]
        [string]$MonthHeading,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseRow
    )

    $headingIndex = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq $MonthHeading) {
            $headingIndex = $i
            break
        }
    }

    if ($headingIndex -ge 0) {
        for ($i = $headingIndex + 1; $i -lt ($Lines.Count - 1); $i++) {
            if ($Lines[$i] -match '^##\s+') {
                break
            }

            if ($Lines[$i] -match '^\|.*\|$' -and $Lines[$i] -notmatch '---' -and $Lines[$i + 1] -match '^\|\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$') {
                return [ordered]@{
                    insertIndex = $i + 2
                    lines = @($ReleaseRow)
                    sectionCreated = $false
                }
            }
        }

        throw "Release notes month section '$MonthHeading' exists but no markdown table header was found."
    }

    $template = Get-MarkdownTableTemplate -Lines $Lines
    $insertIndex = 0
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match '^#\s+') {
            $insertIndex = $i + 1
            while ($insertIndex -lt $Lines.Count -and [string]::IsNullOrWhiteSpace($Lines[$insertIndex])) {
                $insertIndex++
            }
            break
        }
    }

    return [ordered]@{
        insertIndex = $insertIndex
        lines = @(
            $MonthHeading,
            '',
            $template.header,
            $template.separator,
            $ReleaseRow,
            ''
        )
        sectionCreated = $true
    }
}

$releaseDate = Get-ReleaseDate
$releaseMonth = $releaseDate.Substring(0, 7)
$releaseTag = "v$Version"

if ([string]::IsNullOrWhiteSpace($UserValue)) {
    $UserValue = "Release artifacts can point to $releaseTag for audit."
}

if ([string]::IsNullOrWhiteSpace($ChangeSummary)) {
    $ChangeSummary = "Record Tau release $releaseTag ($releaseDate). This helper only updates release notes; version writeback, commit, tag, publish and push stay in separate release steps."
}

$notesFullPath = Resolve-FullPath -Path $NotesPath
if (-not (Test-Path -LiteralPath $notesFullPath)) {
    throw "Release notes file not found: $notesFullPath"
}

$content = [System.IO.File]::ReadAllText($notesFullPath)
$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$hasTrailingNewline = $content.EndsWith("`r`n") -or $content.EndsWith("`n")
$lines = @([System.Text.RegularExpressions.Regex]::Split($content, '\r\n|\n'))
if ($hasTrailingNewline -and $lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
    $lines = @($lines[0..($lines.Count - 2)])
}

$releaseRow = "| $releaseDate | $(Convert-ToTableCell -Value $FeatureDomain) | $(Convert-ToTableCell -Value $UserValue) | $(Convert-ToTableCell -Value $ChangeSummary) |"
$alreadyPresent = $content.IndexOf($releaseTag, [StringComparison]::OrdinalIgnoreCase) -ge 0
$applied = $false
$status = 'would-insert'
$insertIndex = -1
$sectionCreated = $false

if ($alreadyPresent) {
    $status = 'already-present'
}
else {
    $insertPlan = Get-InsertPlan -Lines $lines -MonthHeading "## $releaseMonth" -ReleaseRow $releaseRow
    $insertIndex = $insertPlan.insertIndex
    $sectionCreated = [bool]$insertPlan.sectionCreated

    if ($Apply) {
        $updatedLines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            $updatedLines.Add($line)
        }
        $updatedLines.InsertRange($insertIndex, [string[]]$insertPlan.lines)

        $updatedContent = ([string[]]$updatedLines) -join $newline
        if ($hasTrailingNewline -or -not $updatedContent.EndsWith($newline)) {
            $updatedContent += $newline
        }

        [System.IO.File]::WriteAllText($notesFullPath, $updatedContent, [System.Text.UTF8Encoding]::new($false))
        $status = 'inserted'
        $applied = $true
    }
}

$result = [ordered]@{
    schemaVersion = 1
    dryRun = -not $Apply.IsPresent
    applied = $applied
    status = $status
    version = $Version
    releaseTag = $releaseTag
    releaseDate = $releaseDate
    releaseMonth = $releaseMonth
    notesPath = Convert-ToDisplayPath -Path $notesFullPath
    insertIndex = $insertIndex
    sectionCreated = $sectionCreated
    entry = [ordered]@{
        date = $releaseDate
        featureDomain = $FeatureDomain
        userValue = $UserValue
        changeSummary = $ChangeSummary
        markdown = $releaseRow
    }
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
}
else {
    Write-Host 'Tau release notes update'
    Write-Host "  notes: $($result.notesPath)"
    Write-Host "  release: $releaseTag"
    Write-Host "  date: $releaseDate"
    Write-Host "  status: $status"
    Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
    Write-Host "  row: $releaseRow"
}
