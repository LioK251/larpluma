param([string]$PreviewPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$source = Get-Content -LiteralPath (Join-Path $projectRoot 'Assets/icon.svg') -Raw -Encoding UTF8
$background = [System.Windows.Media.BrushConverter]::new().ConvertFromString($source.svg.rect.fill)
$foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString($source.svg.path.fill)
$geometry = [System.Windows.Media.Geometry]::Parse($source.svg.path.d)
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    $drawing.DrawRectangle($background, $null, [System.Windows.Rect]::new(0, 0, 64, 64))
    $drawing.DrawGeometry($foreground, $null, $geometry)
    $drawing.Pop()
    $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    $stream.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $projectRoot 'icon.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
if ($PreviewPath) { [System.IO.File]::WriteAllBytes($PreviewPath, $frames[-1]) }
Write-Host "Created icon.ico with $($sizes.Count) L icon sizes."
