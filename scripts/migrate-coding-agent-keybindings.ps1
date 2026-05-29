param(
    [string]$AgentDirectory = (Join-Path $HOME '.tau'),
    [string]$KeybindingsPath = '',
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path

$keybindingNameMigrations = [ordered]@{
    cursorUp = 'tui.editor.cursorUp'
    cursorDown = 'tui.editor.cursorDown'
    cursorLeft = 'tui.editor.cursorLeft'
    cursorRight = 'tui.editor.cursorRight'
    cursorWordLeft = 'tui.editor.cursorWordLeft'
    cursorWordRight = 'tui.editor.cursorWordRight'
    cursorLineStart = 'tui.editor.cursorLineStart'
    cursorLineEnd = 'tui.editor.cursorLineEnd'
    jumpForward = 'tui.editor.jumpForward'
    jumpBackward = 'tui.editor.jumpBackward'
    pageUp = 'tui.editor.pageUp'
    pageDown = 'tui.editor.pageDown'
    deleteCharBackward = 'tui.editor.deleteCharBackward'
    deleteCharForward = 'tui.editor.deleteCharForward'
    deleteWordBackward = 'tui.editor.deleteWordBackward'
    deleteWordForward = 'tui.editor.deleteWordForward'
    deleteToLineStart = 'tui.editor.deleteToLineStart'
    deleteToLineEnd = 'tui.editor.deleteToLineEnd'
    yank = 'tui.editor.yank'
    yankPop = 'tui.editor.yankPop'
    undo = 'tui.editor.undo'
    newLine = 'tui.input.newLine'
    submit = 'tui.input.submit'
    tab = 'tui.input.tab'
    copy = 'tui.input.copy'
    selectUp = 'tui.select.up'
    selectDown = 'tui.select.down'
    selectPageUp = 'tui.select.pageUp'
    selectPageDown = 'tui.select.pageDown'
    selectConfirm = 'tui.select.confirm'
    selectCancel = 'tui.select.cancel'
    interrupt = 'app.interrupt'
    clear = 'app.clear'
    exit = 'app.exit'
    suspend = 'app.suspend'
    cycleThinkingLevel = 'app.thinking.cycle'
    cycleModelForward = 'app.model.cycleForward'
    cycleModelBackward = 'app.model.cycleBackward'
    selectModel = 'app.model.select'
    expandTools = 'app.tools.expand'
    toggleThinking = 'app.thinking.toggle'
    toggleSessionNamedFilter = 'app.session.toggleNamedFilter'
    externalEditor = 'app.editor.external'
    followUp = 'app.message.followUp'
    dequeue = 'app.message.dequeue'
    pasteImage = 'app.clipboard.pasteImage'
    newSession = 'app.session.new'
    tree = 'app.session.tree'
    fork = 'app.session.fork'
    resume = 'app.session.resume'
    treeFoldOrUp = 'app.tree.foldOrUp'
    treeUnfoldOrDown = 'app.tree.unfoldOrDown'
    treeEditLabel = 'app.tree.editLabel'
    treeToggleLabelTimestamp = 'app.tree.toggleLabelTimestamp'
    toggleSessionPath = 'app.session.togglePath'
    toggleSessionSort = 'app.session.toggleSort'
    renameSession = 'app.session.rename'
    deleteSession = 'app.session.delete'
    deleteSessionNoninvasive = 'app.session.deleteNoninvasive'
}

$canonicalOrder = @(
    'tui.editor.cursorUp',
    'tui.editor.cursorDown',
    'tui.editor.cursorLeft',
    'tui.editor.cursorRight',
    'tui.editor.cursorWordLeft',
    'tui.editor.cursorWordRight',
    'tui.editor.cursorLineStart',
    'tui.editor.cursorLineEnd',
    'tui.editor.jumpForward',
    'tui.editor.jumpBackward',
    'tui.editor.pageUp',
    'tui.editor.pageDown',
    'tui.editor.deleteCharBackward',
    'tui.editor.deleteCharForward',
    'tui.editor.deleteWordBackward',
    'tui.editor.deleteWordForward',
    'tui.editor.deleteToLineStart',
    'tui.editor.deleteToLineEnd',
    'tui.editor.yank',
    'tui.editor.yankPop',
    'tui.editor.undo',
    'tui.input.newLine',
    'tui.input.submit',
    'tui.input.tab',
    'tui.input.copy',
    'tui.select.up',
    'tui.select.down',
    'tui.select.pageUp',
    'tui.select.pageDown',
    'tui.select.confirm',
    'tui.select.cancel',
    'app.interrupt',
    'app.clear',
    'app.exit',
    'app.suspend',
    'app.thinking.cycle',
    'app.model.cycleForward',
    'app.model.cycleBackward',
    'app.model.select',
    'app.tools.expand',
    'app.thinking.toggle',
    'app.session.toggleNamedFilter',
    'app.editor.external',
    'app.message.followUp',
    'app.message.dequeue',
    'app.clipboard.pasteImage',
    'app.session.new',
    'app.session.tree',
    'app.session.fork',
    'app.session.resume',
    'app.tree.foldOrUp',
    'app.tree.unfoldOrDown',
    'app.tree.editLabel',
    'app.tree.toggleLabelTimestamp',
    'app.session.togglePath',
    'app.session.toggleSort',
    'app.session.rename',
    'app.session.delete',
    'app.session.deleteNoninvasive',
    'app.models.save',
    'app.models.enableAll',
    'app.models.clearAll',
    'app.models.toggleProvider',
    'app.models.reorderUp',
    'app.models.reorderDown',
    'app.tree.filter.default',
    'app.tree.filter.noTools',
    'app.tree.filter.userOnly',
    'app.tree.filter.labeledOnly',
    'app.tree.filter.all',
    'app.tree.filter.cycleForward',
    'app.tree.filter.cycleBackward'
)

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

function Get-JsonObjectProperties {
    param([Parameter(Mandatory = $true)]$Value)

    if ($Value -isnot [pscustomobject]) {
        return $null
    }

    return @($Value.PSObject.Properties)
}

function Get-HasOwnProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($candidate in $Names) {
        if ([string]::Equals($candidate, $Name, [StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

function Order-KeybindingsConfig {
    param([Parameter(Mandatory = $true)]$Config)

    $ordered = [ordered]@{}
    foreach ($keybinding in $canonicalOrder) {
        if ($Config.Contains($keybinding)) {
            $ordered[$keybinding] = $Config[$keybinding]
        }
    }

    $extras = @($Config.Keys | Where-Object { -not $ordered.Contains($_) } | Sort-Object)
    foreach ($key in $extras) {
        $ordered[$key] = $Config[$key]
    }

    return $ordered
}

function Invoke-KeybindingsMigration {
    param([Parameter(Mandatory = $true)][string]$Path)

    $result = [ordered]@{
        sourceKind = 'none'
        action = 'skipped'
        reason = ''
        changedKeys = @()
        skippedConflicts = @()
        preservedExtraKeys = @()
        orderedKeyCount = 0
        migratedConfig = $null
        error = ''
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        $result.reason = 'file-missing'
        return $result
    }

    try {
        $item = Get-Item -LiteralPath $Path -Force
    }
    catch {
        $result.reason = 'cannot-read-file'
        $result.error = $_.Exception.Message
        return $result
    }

    $result.sourceKind = Get-SourceKind -Item $item
    if (($item.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
        $result.reason = 'source-not-file'
        return $result
    }

    try {
        $parsed = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        $result.reason = 'invalid-json'
        $result.error = $_.Exception.Message
        return $result
    }

    $properties = Get-JsonObjectProperties -Value $parsed
    if ($null -eq $properties) {
        $result.reason = 'non-object-json'
        return $result
    }

    $propertyNames = @($properties | ForEach-Object { $_.Name })
    $config = [ordered]@{}
    $changedKeys = @()
    $skippedConflicts = @()
    $migrated = $false

    foreach ($property in $properties) {
        $key = $property.Name
        $nextKey = if ($keybindingNameMigrations.Contains($key)) { $keybindingNameMigrations[$key] } else { $key }

        if ($nextKey -ne $key) {
            $migrated = $true
            if (Get-HasOwnProperty -Names $propertyNames -Name $nextKey) {
                $skippedConflicts += [ordered]@{
                    legacyKey = $key
                    canonicalKey = $nextKey
                }
                continue
            }

            $changedKeys += [ordered]@{
                legacyKey = $key
                canonicalKey = $nextKey
            }
        }

        $config[$nextKey] = $property.Value
    }

    $orderedConfig = Order-KeybindingsConfig -Config $config
    $result.changedKeys = @($changedKeys)
    $result.skippedConflicts = @($skippedConflicts)
    $result.preservedExtraKeys = @($orderedConfig.Keys | Where-Object { $canonicalOrder -notcontains $_ })
    $result.orderedKeyCount = $orderedConfig.Count
    $result.migratedConfig = $orderedConfig

    if (-not $migrated) {
        $result.reason = 'no-legacy-keybindings'
        return $result
    }

    if ($Apply) {
        try {
            $jsonText = ($orderedConfig | ConvertTo-Json -Depth 20)
            [System.IO.File]::WriteAllText($Path, $jsonText + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
            $result.action = 'migrated'
        }
        catch {
            $result.action = 'failed'
            $result.reason = 'write-failed'
            $result.error = $_.Exception.Message
        }
    }
    else {
        $result.action = 'would-migrate'
    }

    return $result
}

try {
    $resolvedAgentDirectory = Resolve-FullPath -Path $AgentDirectory
    $resolvedKeybindingsPath = if ([string]::IsNullOrWhiteSpace($KeybindingsPath)) {
        Join-Path $resolvedAgentDirectory 'keybindings.json'
    }
    else {
        Resolve-FullPath -Path $KeybindingsPath
    }

    $migration = [pscustomobject](Invoke-KeybindingsMigration -Path $resolvedKeybindingsPath)
    $failedCount = if ($migration.action -eq 'failed') { 1 } else { 0 }
    $migratableCount = if ($migration.action -in @('would-migrate', 'migrated')) { 1 } else { 0 }

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = ($failedCount -eq 0)
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        agentDirectory = Convert-ToDisplayPath -Path $resolvedAgentDirectory
        keybindingsPath = Convert-ToDisplayPath -Path $resolvedKeybindingsPath
        scan = [ordered]@{
            migratable = $migratableCount
            migrated = if ($migration.action -eq 'migrated') { 1 } else { 0 }
            skipped = if ($migration.action -eq 'skipped') { 1 } else { 0 }
            failed = $failedCount
            changedKeyCount = @($migration.changedKeys).Count
            skippedConflictCount = @($migration.skippedConflicts).Count
            preservedExtraKeyCount = @($migration.preservedExtraKeys).Count
        }
        keybindings = [ordered]@{
            path = Convert-ToDisplayPath -Path $resolvedKeybindingsPath
            sourceKind = $migration.sourceKind
            action = $migration.action
            reason = $migration.reason
            changedKeys = @($migration.changedKeys)
            skippedConflicts = @($migration.skippedConflicts)
            preservedExtraKeys = @($migration.preservedExtraKeys)
            orderedKeyCount = $migration.orderedKeyCount
        }
        remainingGaps = @(
            'This helper only migrates upstream-style object keybinding ids in keybindings.json from legacy names to canonical app/tui ids.',
            'It does not convert Tau array-style coding-agent-keybindings.json files, close full keybinding runtime parity, or implement package/extension shortcut migration.'
        )
    }

    if (-not [string]::IsNullOrWhiteSpace($migration.error)) {
        $result.keybindings.error = $migration.error
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host "Scanned CodingAgent keybindings at $($result.keybindingsPath)."
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to rewrite legacy keybinding ids.'
        }
        Write-Host "Migratable: $($result.scan.migratable)"
        Write-Host "Migrated: $($result.scan.migrated)"
        Write-Host "Skipped: $($result.scan.skipped)"
        Write-Host "Failed: $($result.scan.failed)"
        if ($result.keybindings.action -eq 'skipped') {
            Write-Host "  SKIP $($result.keybindings.path): $($result.keybindings.reason)"
        }
        elseif ($result.keybindings.action -eq 'failed') {
            Write-Host "  FAIL $($result.keybindings.path): $($result.keybindings.error)"
        }
        else {
            Write-Host "  $($result.keybindings.action.ToUpperInvariant()) $($result.scan.changedKeyCount) legacy keybinding id(s)"
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
        agentDirectory = $AgentDirectory
        keybindingsPath = $KeybindingsPath
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent keybindings migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
