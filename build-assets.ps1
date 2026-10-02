param([string]$PreviewDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path $PSScriptRoot 'App\Assets'
$sourcePath = Join-Path $assetRoot 'ClickClean.png'
if (-not (Test-Path -LiteralPath $sourcePath)) { throw '缺少内置图标源文件 App\Assets\ClickClean.png。' }
if ($PreviewDirectory) { New-Item -ItemType Directory -Path $PreviewDirectory -Force | Out-Null }
$source = [Drawing.Bitmap]::new($sourcePath)
$images = @()
try {
    if ($source.Width -ne $source.Height) { throw '图标源文件必须为正方形。' }
    # 只缩放与转换格式，不在构建时改变图标设计，也不联网生成资源。
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = 'SourceCopy'
            $graphics.CompositingQuality = 'HighQuality'
            $graphics.InterpolationMode = 'HighQualityBicubic'
            $graphics.PixelOffsetMode = 'HighQuality'
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $rectangle = [Drawing.Rectangle]::new(0, 0, $size, $size)
            $graphics.DrawImage($source, $rectangle, 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $images += ,@($size, $stream.ToArray())
            if ($PreviewDirectory) {
                $bitmap.Save((Join-Path $PreviewDirectory ("icon-$size.png")), [Drawing.Imaging.ImageFormat]::Png)
            }
        } finally { $stream.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
$file = [IO.File]::Create((Join-Path $assetRoot 'ClickClean.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry[0] -eq 256) { 0 } else { $entry[0] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$entry[1].Length); $writer.Write([uint32]$offset)
        $offset += $entry[1].Length
    }
    foreach ($entry in $images) { $writer.Write([byte[]]$entry[1]) }
} finally { $writer.Dispose(); $file.Dispose() }
