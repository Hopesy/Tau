param(
    [string]$OutputPath = 'tests/Tau.Ai.Tests/Data/red-circle.png',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$width = 200
$height = 200
$radius = 50
$centerX = 100
$centerY = 100

function Convert-ToRepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($script:repoRoot)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $rootPrefix = $rootFull.TrimEnd($separator) + $separator

    if ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $rootUri = [System.Uri]::new($rootPrefix)
        $pathUri = [System.Uri]::new($fullPath)
        return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', $separator)
    }

    return $fullPath
}

function Get-BigEndianUInt32Bytes {
    param([uint32]$Value)

    return [byte[]]@(
        [byte](($Value -shr 24) -band 0xff),
        [byte](($Value -shr 16) -band 0xff),
        [byte](($Value -shr 8) -band 0xff),
        [byte]($Value -band 0xff)
    )
}

function Get-LittleEndianUInt16Bytes {
    param([uint16]$Value)

    return [byte[]]@(
        [byte]($Value -band 0xff),
        [byte](($Value -shr 8) -band 0xff)
    )
}

function New-Crc32Table {
    $table = [uint32[]]::new(256)
    for ($i = 0; $i -lt 256; $i++) {
        [uint32]$crc = [uint32]$i
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) {
                $crc = [uint32](($crc -shr 1) -bxor [uint32]3988292384)
            }
            else {
                $crc = [uint32]($crc -shr 1)
            }
        }
        $table[$i] = $crc
    }
    return $table
}

$script:crc32Table = New-Crc32Table

function Get-Crc32 {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    [uint32]$crc = [uint32]4294967295
    foreach ($byteValue in $Bytes) {
        $index = [int](($crc -bxor [uint32]$byteValue) -band 0xff)
        $crc = [uint32](($crc -shr 8) -bxor $script:crc32Table[$index])
    }

    return [uint32]($crc -bxor [uint32]4294967295)
}

function Write-Bytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.MemoryStream]$Stream,
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $Stream.Write($Bytes, 0, $Bytes.Length)
}

function New-PngChunk {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateLength(4, 4)]
        [string]$Type,
        [byte[]]$Data = [byte[]]::new(0)
    )

    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($Type)
    $crcInput = [byte[]]::new($typeBytes.Length + $Data.Length)
    [Array]::Copy($typeBytes, 0, $crcInput, 0, $typeBytes.Length)
    [Array]::Copy($Data, 0, $crcInput, $typeBytes.Length, $Data.Length)
    $crc = Get-Crc32 -Bytes $crcInput

    $stream = [System.IO.MemoryStream]::new()
    Write-Bytes -Stream $stream -Bytes (Get-BigEndianUInt32Bytes ([uint32]$Data.Length))
    Write-Bytes -Stream $stream -Bytes $typeBytes
    if ($Data.Length -gt 0) {
        Write-Bytes -Stream $stream -Bytes $Data
    }
    Write-Bytes -Stream $stream -Bytes (Get-BigEndianUInt32Bytes $crc)
    return $stream.ToArray()
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

function Compress-ZlibStored {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $stream = [System.IO.MemoryStream]::new()
    $stream.WriteByte([byte]0x78)
    $stream.WriteByte([byte]0x01)

    $offset = 0
    while ($offset -lt $Bytes.Length) {
        $remaining = $Bytes.Length - $offset
        $blockLength = [Math]::Min(65535, $remaining)
        $isFinal = (($offset + $blockLength) -ge $Bytes.Length)
        $stream.WriteByte([byte]$(if ($isFinal) { 1 } else { 0 }))

        $len = [uint16]$blockLength
        $nlen = [uint16]($len -bxor [uint16]0xffff)
        Write-Bytes -Stream $stream -Bytes (Get-LittleEndianUInt16Bytes $len)
        Write-Bytes -Stream $stream -Bytes (Get-LittleEndianUInt16Bytes $nlen)
        $stream.Write($Bytes, $offset, $blockLength)
        $offset += $blockLength
    }

    Write-Bytes -Stream $stream -Bytes (Get-BigEndianUInt32Bytes (Get-Adler32 -Bytes $Bytes))
    return $stream.ToArray()
}

function New-RedCircleScanlines {
    $stride = 1 + ($script:width * 3)
    [byte[]]$raw = [byte[]]::new($script:height * $stride)
    $radiusSquared = $script:radius * $script:radius

    for ($y = 0; $y -lt $script:height; $y++) {
        $rowOffset = $y * $stride
        $raw[$rowOffset] = 0

        for ($x = 0; $x -lt $script:width; $x++) {
            $pixelOffset = $rowOffset + 1 + ($x * 3)
            $dx = $x - $script:centerX
            $dy = $y - $script:centerY
            $insideCircle = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared

            $raw[$pixelOffset] = 255
            $raw[$pixelOffset + 1] = [byte]$(if ($insideCircle) { 0 } else { 255 })
            $raw[$pixelOffset + 2] = [byte]$(if ($insideCircle) { 0 } else { 255 })
        }
    }

    return $raw
}

function New-RedCirclePng {
    $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    $ihdr = [System.IO.MemoryStream]::new()
    Write-Bytes -Stream $ihdr -Bytes (Get-BigEndianUInt32Bytes ([uint32]$script:width))
    Write-Bytes -Stream $ihdr -Bytes (Get-BigEndianUInt32Bytes ([uint32]$script:height))
    $ihdr.WriteByte([byte]8)
    $ihdr.WriteByte([byte]2)
    $ihdr.WriteByte([byte]0)
    $ihdr.WriteByte([byte]0)
    $ihdr.WriteByte([byte]0)

    $scanlines = New-RedCircleScanlines
    $idat = Compress-ZlibStored -Bytes $scanlines

    $stream = [System.IO.MemoryStream]::new()
    Write-Bytes -Stream $stream -Bytes $signature
    Write-Bytes -Stream $stream -Bytes (New-PngChunk -Type 'IHDR' -Data $ihdr.ToArray())
    Write-Bytes -Stream $stream -Bytes (New-PngChunk -Type 'IDAT' -Data $idat)
    Write-Bytes -Stream $stream -Bytes (New-PngChunk -Type 'IEND')
    return $stream.ToArray()
}

$absoluteOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($absoluteOutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$pngBytes = New-RedCirclePng
[System.IO.File]::WriteAllBytes($absoluteOutputPath, $pngBytes)
$hash = (Get-FileHash -LiteralPath $absoluteOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$relativeOutputPath = Convert-ToRepoRelativePath -Path $absoluteOutputPath

$result = [ordered]@{
    schemaVersion = 1
    succeeded = $true
    outputPath = $relativeOutputPath
    width = $width
    height = $height
    radius = $radius
    format = 'png'
    background = 'white'
    foreground = 'red'
    sha256 = $hash
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
}
else {
    Write-Host "Generated AI test image at: $relativeOutputPath"
    Write-Host "  size: $width x $height"
    Write-Host "  sha256: $hash"
}
