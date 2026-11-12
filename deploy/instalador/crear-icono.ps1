param([string]$Salida = (Join-Path $PSScriptRoot 'acusa.ico'))
# Genera el ícono de Acusa Worker (256 px, PNG dentro de ICO): cuadro azul con una «A» blanca y un punto verde de conexión.
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)
$ruta = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 52; $d = $r * 2
$ruta.AddArc(8, 8, $d, $d, 180, 90); $ruta.AddArc(248 - $d, 8, $d, $d, 270, 90)
$ruta.AddArc(248 - $d, 248 - $d, $d, $d, 0, 90); $ruta.AddArc(8, 248 - $d, $d, $d, 90, 90); $ruta.CloseFigure()
$pincel = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(256, 256)), ([System.Drawing.Color]::FromArgb(255, 20, 70, 140)), ([System.Drawing.Color]::FromArgb(255, 10, 35, 80))
$g.FillPath($pincel, $ruta)
$fuente = New-Object System.Drawing.Font 'Segoe UI', 150, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
$g.DrawString('A', $fuente, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 6, 256, 256), $fmt)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 204, 113))), 180, 180, 52, 52)
$g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 6), 180, 180, 52, 52)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $png = $ms.ToArray()
$fs = [System.IO.File]::Create($Salida); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
$w.Write([uint32]$png.Length); $w.Write([uint32]22); $w.Write($png)
$w.Close(); $fs.Close(); $bmp.Dispose()
Write-Output "Ícono: $Salida"
