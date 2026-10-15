$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/DesktopNotes.Presentation/Assets'

function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x + $width - $d), $y, $d, $d, 270, 90)
    $path.AddArc(($x + $width - $d), ($y + $height - $d), $d, $d, 0, 90)
    $path.AddArc($x, ($y + $height - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function Write-Icon([string]$name, [int[]]$sizes, [scriptblock]$render) {
    $frames = foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            & $render $graphics $size
            $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size=$size; Data=$memory.ToArray() }
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $stream = [IO.File]::Create((Join-Path $taskAssets $name))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}

$taskSource = [Drawing.Image]::FromFile((Join-Path $taskAssets 'AppIcon-source.png'))
$taskMasked = [Drawing.Bitmap]::new($taskSource.Width, $taskSource.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$taskMaskGraphics = [Drawing.Graphics]::FromImage($taskMasked)
$taskMask = New-RoundedPath ($taskSource.Width * .075) ($taskSource.Height * .091) ($taskSource.Width * .825) ($taskSource.Height * .795) ($taskSource.Width * .16)
try {
    # Apply the icon mask at source resolution, then downsample for smooth edges.
    $taskMaskGraphics.SetClip($taskMask)
    $taskMaskGraphics.DrawImageUnscaled($taskSource, 0, 0)
    Write-Icon 'App.ico' @(16,20,24,32,40,48,64,128,256) {
        param($graphics, $size)
        $graphics.DrawImage($taskMasked, 0, 0, $size, $size)
    }
} finally { $taskMask.Dispose(); $taskMaskGraphics.Dispose(); $taskMasked.Dispose(); $taskSource.Dispose() }

foreach ($taskVariant in @(@('TrayLight.ico', [Drawing.Color]::White), @('TrayDark.ico', [Drawing.Color]::FromArgb(37,44,52)))) {
    $taskGlyphColor = $taskVariant[1]
    Write-Icon $taskVariant[0] @(16,20,24,32,40,48,64) {
        param($graphics, $size)
        $graphics.ScaleTransform(($size / 20.0), ($size / 20.0))
        $brush = [Drawing.SolidBrush]::new($taskGlyphColor)
        $pen = [Drawing.Pen]::new($taskGlyphColor, 2)
        $first = New-RoundedPath 1 1 8 8 1.2
        $second = New-RoundedPath 11 11 8 8 1.2
        $link = [Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $graphics.FillPath($brush, $first); $graphics.FillPath($brush, $second)
            $link.AddLine(10,4,13,4); $link.AddArc(11,4,4,4,270,90); $link.AddLine(15,6,15,10)
            $graphics.DrawPath($pen, $link)
        } finally { $link.Dispose(); $second.Dispose(); $first.Dispose(); $pen.Dispose(); $brush.Dispose() }
    }
}
