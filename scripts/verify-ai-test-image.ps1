param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-ai-test-image-" + [Guid]::NewGuid().ToString('N'))
$scriptSucceeded = $false

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

function Invoke-JsonScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $fullScriptPath = Join-Path $repoRoot $ScriptPath
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $fullScriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "$Name did not return valid JSON. Output: $outputText"
    }
}

function Read-UInt32BigEndian {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,
        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    return [uint32](
        ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3]
    )
}

function Read-UInt16LittleEndian {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,
        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    return [uint16](([uint16]$Bytes[$Offset]) -bor ([uint16]$Bytes[$Offset + 1] -shl 8))
}

function Read-PngChunks {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $signature.Length; $i++) {
        if ($Bytes[$i] -ne $signature[$i]) {
            throw 'PNG signature mismatch.'
        }
    }

    $chunks = @()
    $offset = $signature.Length
    while ($offset -lt $Bytes.Length) {
        $length = [int](Read-UInt32BigEndian -Bytes $Bytes -Offset $offset)
        $offset += 4
        $type = [System.Text.Encoding]::ASCII.GetString($Bytes, $offset, 4)
        $offset += 4
        [byte[]]$data = [byte[]]::new($length)
        if ($length -gt 0) {
            [Array]::Copy($Bytes, $offset, $data, 0, $length)
        }
        $offset += $length
        $crc = Read-UInt32BigEndian -Bytes $Bytes -Offset $offset
        $offset += 4

        $chunks += [ordered]@{
            type = $type
            length = $length
            data = $data
            crc = $crc
        }

        if ($type -eq 'IEND') {
            break
        }
    }

    return @($chunks)
}

function Get-Adler32 {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    [uint32]$a = 1
    [uint32]$b = 0
    foreach ($byteValue in $Bytes) {
        $a = [uint32](($a + [uint32]$byteValue) % 65521)
        $b = [uint32](($b + $a) % 65521)
    }

    return [uint32](($b -shl 16) -bor $a)
}

function Expand-ZlibStoredBlocks {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    if ($Bytes.Length -lt 6) {
        throw 'Zlib payload is too short.'
    }

    Assert-Equal -Name 'zlib header cmf' -Actual $Bytes[0] -Expected ([byte]0x78)
    Assert-Equal -Name 'zlib header flg' -Actual $Bytes[1] -Expected ([byte]0x01)

    $output = [System.Collections.Generic.List[byte]]::new()
    $offset = 2
    $payloadEnd = $Bytes.Length - 4
    $finalSeen = $false

    while ($offset -lt $payloadEnd) {
        $header = $Bytes[$offset]
        $offset++
        $finalSeen = (($header -band 1) -eq 1)
        $blockType = ($header -shr 1) -band 0x3
        Assert-Equal -Name 'deflate block type stored' -Actual $blockType -Expected 0

        $len = Read-UInt16LittleEndian -Bytes $Bytes -Offset $offset
        $offset += 2
        $nlen = Read-UInt16LittleEndian -Bytes $Bytes -Offset $offset
        $offset += 2
        Assert-Equal -Name 'deflate nlen complement' -Actual (($len -bxor $nlen) -band 0xffff) -Expected 0xffff

        for ($i = 0; $i -lt $len; $i++) {
            $output.Add($Bytes[$offset + $i])
        }
        $offset += $len

        if ($finalSeen) {
            break
        }
    }

    Assert-Equal -Name 'deflate final block seen' -Actual $finalSeen -Expected $true
    Assert-Equal -Name 'deflate consumed before adler' -Actual $offset -Expected $payloadEnd

    [byte[]]$raw = $output.ToArray()
    $expectedAdler = Read-UInt32BigEndian -Bytes $Bytes -Offset $payloadEnd
    $actualAdler = Get-Adler32 -Bytes $raw
    Assert-Equal -Name 'zlib adler32' -Actual $actualAdler -Expected $expectedAdler
    return $raw
}

function Get-Pixel {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Raw,
        [Parameter(Mandatory = $true)]
        [int]$Width,
        [Parameter(Mandatory = $true)]
        [int]$X,
        [Parameter(Mandatory = $true)]
        [int]$Y
    )

    $stride = 1 + ($Width * 3)
    $offset = ($Y * $stride) + 1 + ($X * 3)
    return [byte[]]@($Raw[$offset], $Raw[$offset + 1], $Raw[$offset + 2])
}

function Assert-Pixel {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [byte[]]$Actual,
        [Parameter(Mandatory = $true)]
        [byte[]]$Expected
    )

    $passed = $Actual.Length -eq $Expected.Length
    if ($passed) {
        for ($i = 0; $i -lt $Actual.Length; $i++) {
            if ($Actual[$i] -ne $Expected[$i]) {
                $passed = $false
                break
            }
        }
    }

    Add-Assertion -Name $Name -Passed $passed -Detail "Expected RGB $($Expected -join ','), actual $($Actual -join ',')."
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $outputPath = Join-Path $tempRoot 'red-circle.png'
    $generated = Invoke-JsonScript `
        -Name 'generate-ai-test-image' `
        -ScriptPath 'scripts/generate-ai-test-image.ps1' `
        -Arguments @('-OutputPath', $outputPath, '-Json')

    Add-Assertion -Name 'generator succeeded' -Passed ($generated.succeeded -eq $true) -Detail 'Generator did not report success.'
    Assert-Equal -Name 'generator width' -Actual $generated.width -Expected 200
    Assert-Equal -Name 'generator height' -Actual $generated.height -Expected 200
    Assert-Equal -Name 'generator radius' -Actual $generated.radius -Expected 50
    Assert-Equal -Name 'generator format' -Actual $generated.format -Expected 'png'
    Add-Assertion -Name 'generated file exists' -Passed (Test-Path -LiteralPath $outputPath -PathType Leaf) -Detail "Generated file was missing: $outputPath"

    $fileHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Equal -Name 'generator reports sha256' -Actual $generated.sha256 -Expected $fileHash

    [byte[]]$png = [System.IO.File]::ReadAllBytes($outputPath)
    $chunks = Read-PngChunks -Bytes $png
    Assert-Equal -Name 'png chunk count' -Actual @($chunks).Count -Expected 3
    Assert-Equal -Name 'png first chunk' -Actual $chunks[0].type -Expected 'IHDR'
    Assert-Equal -Name 'png second chunk' -Actual $chunks[1].type -Expected 'IDAT'
    Assert-Equal -Name 'png final chunk' -Actual $chunks[2].type -Expected 'IEND'

    $ihdr = [byte[]]$chunks[0].data
    $width = [int](Read-UInt32BigEndian -Bytes $ihdr -Offset 0)
    $height = [int](Read-UInt32BigEndian -Bytes $ihdr -Offset 4)
    Assert-Equal -Name 'ihdr width' -Actual $width -Expected 200
    Assert-Equal -Name 'ihdr height' -Actual $height -Expected 200
    Assert-Equal -Name 'ihdr bit depth' -Actual $ihdr[8] -Expected ([byte]8)
    Assert-Equal -Name 'ihdr color type' -Actual $ihdr[9] -Expected ([byte]2)

    $raw = Expand-ZlibStoredBlocks -Bytes ([byte[]]$chunks[1].data)
    Assert-Equal -Name 'scanline byte count' -Actual $raw.Length -Expected (200 * (1 + (200 * 3)))
    for ($y = 0; $y -lt 200; $y++) {
        $filterByte = $raw[$y * (1 + (200 * 3))]
        if ($filterByte -ne 0) {
            Add-Assertion -Name 'scanline filter bytes' -Passed $false -Detail "Expected filter byte 0 at row $y, actual $filterByte."
        }
    }
    Add-Assertion -Name 'scanline filter bytes' -Passed $true -Detail 'All scanlines use filter byte 0.'

    Assert-Pixel -Name 'center pixel red' -Actual (Get-Pixel -Raw $raw -Width 200 -X 100 -Y 100) -Expected ([byte[]]@(255, 0, 0))
    Assert-Pixel -Name 'circle edge red' -Actual (Get-Pixel -Raw $raw -Width 200 -X 150 -Y 100) -Expected ([byte[]]@(255, 0, 0))
    Assert-Pixel -Name 'corner pixel white' -Actual (Get-Pixel -Raw $raw -Width 200 -X 0 -Y 0) -Expected ([byte[]]@(255, 255, 255))
    Assert-Pixel -Name 'outside circle white' -Actual (Get-Pixel -Raw $raw -Width 200 -X 10 -Y 100) -Expected ([byte[]]@(255, 255, 255))

    $scriptSucceeded = $true
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        tempRoot = $tempRoot
        outputPath = $outputPath
        sha256 = $fileHash
        assertions = $script:assertions
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau AI test image smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  sha256: $fileHash"
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        tempRoot = $tempRoot
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau AI test image smoke failed'
        Write-Host $_.Exception.Message
        Write-Host "temp root: $tempRoot"
    }

    exit 1
}
finally {
    if ($scriptSucceeded -and -not $KeepTemp) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
