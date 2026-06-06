param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$script:results = [ordered]@{}

function Add-Assertion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [bool]$Passed,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $script:assertions += [ordered]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }

    if (-not $Passed) {
        throw $Detail
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Actual,
        [AllowNull()]
        [object]$Expected
    )

    Add-Assertion -Name $Name -Passed ([object]::Equals($Actual, $Expected)) -Detail "Expected '$Expected', actual '$Actual'."
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed ($Text -match $Pattern) -Detail "Expected text to match '$Pattern'."
}

function Assert-NotMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed (-not ($Text -match $Pattern)) -Detail "Expected text not to match '$Pattern'."
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$AllowFailure
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $text = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode. Output: $text"
    }

    return [ordered]@{
        exitCode = $exitCode
        output = $text
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'git' -Arguments (@('-C', $WorkingDirectory) + $Arguments) -AllowFailure:$AllowFailure
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'powershell' -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments) -AllowFailure:$AllowFailure
}

function ConvertFrom-JsonOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Output was not valid JSON. Output: $Text"
    }
}

function Get-CommandNames {
    param(
        [AllowNull()]
        [object[]]$Items
    )

    return @($Items | ForEach-Object { $_.name })
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-package-publish-" + [Guid]::NewGuid().ToString('N'))

try {
    $repo = Join-Path $tempRoot 'repo'
    $scriptsDir = Join-Path $repo 'scripts'
    $fakeBin = Join-Path $tempRoot 'bin'
    $srcDir = Join-Path $repo 'src'
    New-Item -ItemType Directory -Force -Path $scriptsDir, $fakeBin, $srcDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/publish-release-packages.ps1') -Destination (Join-Path $scriptsDir 'publish-release-packages.ps1')

    Set-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Value @(
        '<Project>',
        '  <PropertyGroup>',
        '    <TargetFramework>net10.0</TargetFramework>',
        '    <VersionPrefix>1.2.3</VersionPrefix>',
        '  </PropertyGroup>',
        '</Project>'
    ) -Encoding ASCII

    foreach ($projectName in @('Tau.Ai', 'Tau.Agent', 'Tau.Tui')) {
        $projectDir = Join-Path $srcDir $projectName
        New-Item -ItemType Directory -Force -Path $projectDir | Out-Null
        Set-Content -LiteralPath (Join-Path $projectDir "$projectName.csproj") -Value @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '  <PropertyGroup>',
            '    <TargetFramework>net10.0</TargetFramework>',
            '  </PropertyGroup>',
            '</Project>'
        ) -Encoding ASCII
    }

    $appDir = Join-Path $srcDir 'Tau.CodingAgent'
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    Set-Content -LiteralPath (Join-Path $appDir 'Tau.CodingAgent.csproj') -Value @(
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup>',
        '    <OutputType>Exe</OutputType>',
        '    <TargetFramework>net10.0</TargetFramework>',
        '  </PropertyGroup>',
        '</Project>'
    ) -Encoding ASCII

    $fakeDotnet = Join-Path $fakeBin 'dotnet.ps1'
    $fakeDotnetLog = Join-Path $tempRoot 'fake-dotnet.log'
    Set-Content -LiteralPath $fakeDotnet -Value @(
        '$ErrorActionPreference = "Stop"',
        'Add-Content -LiteralPath $env:TAU_FAKE_DOTNET_LOG -Value ($args -join " ")',
        'Write-Output ($args -join " ")',
        'if ($args.Count -gt 0 -and $args[0] -eq "pack") {',
        '    $project = [System.IO.Path]::GetFileNameWithoutExtension($args[1])',
        '    $output = "."',
        '    $version = "0.0.0"',
        '    for ($i = 0; $i -lt $args.Count; $i++) {',
        '        if ($args[$i] -eq "--output" -and ($i + 1) -lt $args.Count) {',
        '            $output = $args[$i + 1]',
        '        }',
        '        elseif ($args[$i].StartsWith("-p:PackageVersion=", [System.StringComparison]::OrdinalIgnoreCase)) {',
        '            $version = $args[$i].Substring(18)',
        '        }',
        '    }',
        '    New-Item -ItemType Directory -Force -Path $output | Out-Null',
        '    Set-Content -LiteralPath (Join-Path $output "$project.$version.nupkg") -Value "fake package" -Encoding ASCII',
        '}',
        'exit 0'
    ) -Encoding ASCII

    $init = Invoke-Native -FilePath 'git' -Arguments @('init', '-b', 'main', $repo) -AllowFailure
    if ($init.exitCode -ne 0) {
        Invoke-Native -FilePath 'git' -Arguments @('init', $repo) | Out-Null
        Invoke-Git -WorkingDirectory $repo -Arguments @('checkout', '-b', 'main') | Out-Null
    }

    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.email', 'tau@example.invalid') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.name', 'Tau Package Publish Smoke') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('add', '--', '.') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('commit', '-m', 'Initial package publish fixture') | Out-Null

    $publishScript = Join-Path $scriptsDir 'publish-release-packages.ps1'

    $dryRun = Invoke-Publish -ScriptPath $publishScript -Arguments @('-SkipPush', '-Json')
    $dryRunJson = ConvertFrom-JsonOutput -Text $dryRun.output
    $script:results.dryRun = [ordered]@{
        exitCode = $dryRun.exitCode
        projectCount = @($dryRunJson.projects).Count
        commandNames = Get-CommandNames -Items @($dryRunJson.plannedCommands)
    }
    Assert-Equal -Name 'dry-run exit code' -Actual $dryRun.exitCode -Expected 0
    Assert-Equal -Name 'dry-run schema version' -Actual $dryRunJson.schemaVersion -Expected 1
    Assert-Equal -Name 'dry-run true' -Actual $dryRunJson.dryRun -Expected $true
    Assert-Equal -Name 'default library project count' -Actual @($dryRunJson.projects).Count -Expected 3
    Assert-Equal -Name 'skip push command count' -Actual @($dryRunJson.plannedCommands | Where-Object { $_.name -like 'push-*' }).Count -Expected 0

    $env:TAU_TEST_NUGET_API_KEY = 'super-secret-package-key'
    try {
        $redactedPlan = Invoke-Publish -ScriptPath $publishScript -Arguments @('-ApiKeyEnv', 'TAU_TEST_NUGET_API_KEY', '-Json')
    }
    finally {
        Remove-Item Env:TAU_TEST_NUGET_API_KEY -ErrorAction SilentlyContinue
    }
    $redactedJson = ConvertFrom-JsonOutput -Text $redactedPlan.output
    $redactedCommands = @($redactedJson.plannedCommands | ForEach-Object { $_.command }) -join [Environment]::NewLine
    $script:results.redactedPlan = [ordered]@{
        exitCode = $redactedPlan.exitCode
        commandCount = @($redactedJson.plannedCommands).Count
    }
    Assert-Equal -Name 'redacted dry-run exit code' -Actual $redactedPlan.exitCode -Expected 0
    Assert-Equal -Name 'api key present without value' -Actual $redactedJson.apiKey.present -Expected $true
    Assert-Matches -Name 'planned push redacts api key' -Text $redactedCommands -Pattern '<redacted:TAU_TEST_NUGET_API_KEY>'
    Assert-NotMatches -Name 'json does not leak api key' -Text $redactedPlan.output -Pattern 'super-secret-package-key'

    $env:TAU_TEST_NUGET_API_KEY = 'super-secret-package-key'
    $env:TAU_FAKE_DOTNET_LOG = $fakeDotnetLog
    try {
        $apply = Invoke-Publish -ScriptPath $publishScript -Arguments @(
            '-ApiKeyEnv',
            'TAU_TEST_NUGET_API_KEY',
            '-DotnetCli',
            $fakeDotnet,
            '-Apply',
            '-SkipDuplicate',
            '-Json'
        )
    }
    finally {
        Remove-Item Env:TAU_TEST_NUGET_API_KEY -ErrorAction SilentlyContinue
        Remove-Item Env:TAU_FAKE_DOTNET_LOG -ErrorAction SilentlyContinue
    }
    $applyJson = ConvertFrom-JsonOutput -Text $apply.output
    $fakeDotnetOutput = if (Test-Path -LiteralPath $fakeDotnetLog) { Get-Content -LiteralPath $fakeDotnetLog -Raw } else { '' }
    $script:results.apply = [ordered]@{
        exitCode = $apply.exitCode
        commandResultCount = @($applyJson.commandResults).Count
        packageCount = @($applyJson.packages).Count
    }
    Assert-Equal -Name 'apply exit code' -Actual $apply.exitCode -Expected 0
    Assert-Equal -Name 'apply succeeded' -Actual $applyJson.succeeded -Expected $true
    Assert-Matches -Name 'fake dotnet ran pack' -Text $fakeDotnetOutput -Pattern '^pack .*Tau\.Ai\.csproj'
    Assert-Matches -Name 'fake dotnet ran push' -Text $fakeDotnetOutput -Pattern 'nuget push .*Tau\.Ai\.1\.2\.3\.nupkg'
    Assert-Matches -Name 'apply command redacted api key' -Text (@($applyJson.commandResults | ForEach-Object { $_.command }) -join [Environment]::NewLine) -Pattern '<redacted:TAU_TEST_NUGET_API_KEY>'
    Assert-Matches -Name 'apply output preview redacted api key' -Text (@($applyJson.commandResults | ForEach-Object { $_.outputPreview }) -join [Environment]::NewLine) -Pattern '<redacted:TAU_TEST_NUGET_API_KEY>'
    Assert-NotMatches -Name 'apply json does not leak api key' -Text $apply.output -Pattern 'super-secret-package-key'

    $appWarning = Invoke-Publish -ScriptPath $publishScript -Arguments @(
        '-ProjectPath',
        'src/Tau.CodingAgent/Tau.CodingAgent.csproj',
        '-SkipPush',
        '-Json'
    )
    $appWarningJson = ConvertFrom-JsonOutput -Text $appWarning.output
    $applicationWarningCount = @($appWarningJson.checks | Where-Object { $_.name -eq 'application-packages' -and $_.status -eq 'warning' }).Count
    $script:results.appWarning = [ordered]@{
        exitCode = $appWarning.exitCode
        applicationWarningCount = $applicationWarningCount
    }
    Assert-Equal -Name 'application warning exit code' -Actual $appWarning.exitCode -Expected 0
    Assert-Equal -Name 'application project warning' -Actual $applicationWarningCount -Expected 1

    Set-Content -LiteralPath (Join-Path $repo 'dirty.txt') -Value 'dirty' -Encoding ASCII
    $dirtyApply = Invoke-Publish -ScriptPath $publishScript -Arguments @('-Apply', '-DotnetCli', $fakeDotnet, '-SkipPush', '-Json') -AllowFailure
    $script:results.dirtyApply = [ordered]@{
        exitCode = $dirtyApply.exitCode
        outputLength = $dirtyApply.output.Length
    }
    Assert-Equal -Name 'dirty apply blocked' -Actual ($dirtyApply.exitCode -ne 0) -Expected $true
    Assert-Matches -Name 'dirty apply clean-worktree check' -Text $dirtyApply.output -Pattern '"clean-worktree"'
    Assert-Matches -Name 'dirty apply blocked status' -Text $dirtyApply.output -Pattern '"blocked"'

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        fixtureTempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau release package publish smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  fixture: $tempRoot"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        fixtureTempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau release package publish smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
