# Draws the app icon (a white clockwise arrow on a blue rounded square) and writes app.ico next to this
# script: 16-64 px as 32-bit DIBs (what System.Drawing.Icon and the tray read best), 256 px as PNG.
# -Preview <png> also writes a sheet of every size on light and dark backgrounds. ASCII only.
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'), [string]$Preview = '')
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $inset = [Math]::Max(0.5, $s * 0.03)
  $box = $s - 2 * $inset
  $d = [Math]::Max(3.0, $box * 0.46)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc([single]$inset, [single]$inset, [single]$d, [single]$d, 180, 90)
  $path.AddArc([single]($inset + $box - $d), [single]$inset, [single]$d, [single]$d, 270, 90)
  $path.AddArc([single]($inset + $box - $d), [single]($inset + $box - $d), [single]$d, [single]$d, 0, 90)
  $path.AddArc([single]$inset, [single]($inset + $box - $d), [single]$d, [single]$d, 90, 90)
  $path.CloseFigure()
  $rect = New-Object System.Drawing.RectangleF([single]$inset, [single]$inset, [single]$box, [single]$box)
  $top = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)
  $bot = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, [single]90)
  $g.FillPath($br, $path)

  # the arrow: an open circle going clockwise, the gap at the top right, the head at twelve o'clock
  $cx = $s / 2.0; $cy = $s / 2.0 + $s * 0.035
  $R = $s * 0.25
  $w = [Math]::Max(1.8, $s * 0.11)
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$w)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
  $start = -28.0; $sweep = 290.0
  $g.DrawArc($pen, [single]($cx - $R), [single]($cy - $R), [single](2 * $R), [single](2 * $R), [single]$start, [single]$sweep)
  $th = ($start + $sweep) * [Math]::PI / 180.0
  $px = $cx + $R * [Math]::Cos($th); $py = $cy + $R * [Math]::Sin($th)
  $tx = -[Math]::Sin($th); $ty = [Math]::Cos($th)
  $nx = [Math]::Cos($th); $ny = [Math]::Sin($th)
  $a = [Math]::Max(2.4, $s * 0.175); $b = [Math]::Max(2.2, $s * 0.15)
  $back = $a * 0.2
  $pts = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF([single]($px + $tx * $a), [single]($py + $ty * $a))),
    (New-Object System.Drawing.PointF([single]($px + $nx * $b - $tx * $back), [single]($py + $ny * $b - $ty * $back))),
    (New-Object System.Drawing.PointF([single]($px - $nx * $b - $tx * $back), [single]($py - $ny * $b - $ty * $back)))
  )
  $g.FillPolygon([System.Drawing.Brushes]::White, $pts)
  $pen.Dispose(); $br.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

# A 32-bit DIB for an icon entry: BITMAPINFOHEADER (height doubled for the mask), bottom-up BGRA, empty AND mask.
function Get-Dib([System.Drawing.Bitmap]$bmp) {
  $s = $bmp.Width
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
  $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
  for ($y = $s - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $s; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
  $bw.Write((New-Object byte[] ($maskRow * $s)))
  $bw.Flush()
  return , $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$frames = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
  $bmp = New-Frame $s
  if ($s -ge 256) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $ms.ToArray()
  } else { $data = Get-Dib $bmp }
  $frames.Add([pscustomobject]@{ Size = $s; Data = [byte[]]$data; Bmp = $bmp })
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$off = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f.Data.Length); $w.Write([int]$off)
  $off += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Close()
'wrote {0} ({1} bytes, sizes {2})' -f $Out, (Get-Item $Out).Length, ($sizes -join ', ')

if ($Preview) {
  $W = 0; foreach ($f in $frames) { $W += $f.Size + 24 }
  $H = 2 * (256 + 32)   # (the comma binds tighter than *, so this cannot go inline in the call)
  $sheet = New-Object System.Drawing.Bitmap($W, $H)
  $g = [System.Drawing.Graphics]::FromImage($sheet)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 244, 246))
  $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, 288, $W, 288)
  $x = 12
  foreach ($f in $frames) {
    $g.DrawImageUnscaled($f.Bmp, $x, 16)
    $g.DrawImageUnscaled($f.Bmp, $x, 288 + 16)
    $x += $f.Size + 24
  }
  $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $sheet.Dispose()
  'preview ' + $Preview
}
foreach ($f in $frames) { $f.Bmp.Dispose() }
