# Draws Glance.ico: three meter bars on a dark tile, the same mark the tray icon uses (see AppIcon in Tray.cs).
# Run it again only if the mark changes. The .ico it writes is checked in.

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([System.Drawing.RectangleF]$r, [single]$radius) {
    $d = [Math]::Min($radius * 2, [Math]::Min($r.Width, $r.Height))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MarkPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Laid out on a 32 unit grid and scaled.
    $u = $size / 32.0
    $tile = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x14, 0x16, 0x1B))
    $path = New-RoundedPath (New-Object System.Drawing.RectangleF $u, $u, (30 * $u), (30 * $u)) (7 * $u)
    $g.FillPath($tile, $path)

    $bars = @(
        @{ Color = [System.Drawing.Color]::FromArgb(0x6F, 0xB1, 0xFF); Fill = 0.85 },
        @{ Color = [System.Drawing.Color]::FromArgb(0xF2, 0xB0, 0x4A); Fill = 0.60 },
        @{ Color = [System.Drawing.Color]::FromArgb(0xF0, 0x61, 0x6D); Fill = 0.35 }
    )
    for ($i = 0; $i -lt $bars.Count; $i++) {
        $brush = New-Object System.Drawing.SolidBrush $bars[$i].Color
        $rect = New-Object System.Drawing.RectangleF (6 * $u), ((7 + $i * 7) * $u), (20 * $bars[$i].Fill * $u), (4 * $u)
        $g.FillPath($brush, (New-RoundedPath $rect (2 * $u)))
        $brush.Dispose()
    }
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { ,(New-MarkPng $_) }

# An .ico file is a small directory followed by the images, here stored as PNG.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'Glance.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes, sizes: $($sizes -join ', '))"
