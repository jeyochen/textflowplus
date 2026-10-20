<#
.SYNOPSIS
  TextFlowPlus T+ Logo 渲染脚本（System.Drawing / GDI+）— 立体版
.DESCRIPTION
  使用与 AIELogo/*.svg 完全一致的几何与渐变参数，渲染：
    icon-*.png          立体图标：底板高光/暗角/内描边 + T 纵向渐变 + 投影
    mark-color.png      彩色标识（透明底，浅色背景用，纵向渐变）
    mark-white.png      白色标识（透明底，深色背景用，纵向渐变）
    lockup-light.png    完整标识（浅色版）  lockup-dark.png 深色版（含 @2x）
    aie.ico             多尺寸 ICO（256/128/64/48/32/16，PNG 内嵌）
  用法：pwsh ./render-logo.ps1 [-OutDir <路径>] [-MasterSize 1024]
#>
param(
  [string]$OutDir = (Join-Path $PSScriptRoot '.'),
  [int]$MasterSize = 1024
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$k = $MasterSize / 512.0

# ---------- 颜色 ----------
# 背景：透明（不绘制底板，标识可直接叠加在任意底色上）
# 深底（icon / mark-white / lockup-dark）用色：T 为白→淡靛
$cInkTop  = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)  # T 顶（受光）
$cInkBot  = [System.Drawing.Color]::FromArgb(255, 199, 210, 254)  # #C7D2FE T 底
# + 号（琥珀三色，唯一强调色）
$cCaretT  = [System.Drawing.Color]::FromArgb(255, 253, 230, 138)  # #FDE68A
$cCaretM  = [System.Drawing.Color]::FromArgb(255, 251, 191,  36)  # #FBBF24
$cCaretB  = [System.Drawing.Color]::FromArgb(255, 217, 119,   6)  # #D97706
# 浅底（mark-color / lockup-light）用色：T 为亮海蓝→深海洋蓝
$cLgInkT  = [System.Drawing.Color]::FromArgb(255,  79, 174, 245)  # #4FAEF5
$cLgInkB  = [System.Drawing.Color]::FromArgb(255,  11,  78, 162)  # #0B4EA2
$cLgCaretT= [System.Drawing.Color]::FromArgb(255, 251, 191,  36)  # #FBBF24
$cLgCaretB= [System.Drawing.Color]::FromArgb(255, 180,  83,   9)  # #B45309
# 外框描边：海蓝对角线渐变（两端色，中段由 New-DBrush3 插值）
$cFrameL  = [System.Drawing.Color]::FromArgb(255,  92, 192, 255)  # #5CC0FF
$cFrameM  = [System.Drawing.Color]::FromArgb(255,  30, 123, 224)  # #1E7BE0
$cFrameD  = [System.Drawing.Color]::FromArgb(255,  11,  78, 162)  # #0B4EA2
$cSlate   = [System.Drawing.Color]::FromArgb(255,  15,  23,  42)  # #0F172A 字标（浅底）

# ---------- 工具 ----------
function New-RoundedRectPath {
  param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $R
  $p.AddArc($X, $Y, $d, $d, 180, 90)
  $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
  $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
  $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function New-RoundPen {
  param($Brush, [float]$Width)
  $pen = New-Object System.Drawing.Pen($Brush, $Width)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  return $pen
}

# 纵向两色渐变笔刷
function New-VBrush {
  param($Rect, $Top, $Bottom)
  return New-Object System.Drawing.Drawing2D.LinearGradientBrush($Rect, $Top, $Bottom, 90.0)
}

# 纵向三色渐变笔刷（ColorBlend）
function New-VBrush3 {
  param($Rect, $Top, $Mid, $Bottom)
  $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush($Rect, $Top, $Bottom, 90.0)
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend
  $blend.Positions = [single[]]@(0.0, 0.55, 1.0)
  $blend.Colors = [System.Drawing.Color[]]@($Top, $Mid, $Bottom)
  $b.InterpolationColors = $blend
  return $b
}

# 对角线三色渐变笔刷（45°，与 SVG 外框渐变一致）
function New-DBrush3 {
  param($Rect, $Start, $Mid, $End)
  $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush($Rect, $Start, $End, 45.0)
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend
  $blend.Positions = [single[]]@(0.0, 0.5, 1.0)
  $blend.Colors = [System.Drawing.Color[]]@($Start, $Mid, $End)
  $b.InterpolationColors = $blend
  return $b
}

# 绘制 T+ 全部笔画（可选纵向偏移 DY，用于投影层）
# T 位于框内左上方；+ 位于框内右下方，二者不重叠
function Draw-Strokes {
  param($G, [float]$DY, [float]$S, $InkPen, $PlusPen)
  # T
  $G.DrawLine($InkPen, 104*$S, (152+$DY)*$S, 284*$S, (152+$DY)*$S)
  $G.DrawLine($InkPen, 194*$S, (152+$DY)*$S, 194*$S, (352+$DY)*$S)
  # +（框内右下方，与 T 不重叠）
  $G.DrawLine($PlusPen, 368*$S, (266+$DY)*$S, 368*$S, (400+$DY)*$S)
  $G.DrawLine($PlusPen, 301*$S, (333+$DY)*$S, 435*$S, (333+$DY)*$S)
}

# 蓝色描边方框（框内透明）
function Draw-Frame {
  param($G, [float]$S, $FramePen)
  $r = 104.0 * $S
  $path = New-RoundedRectPath (30*$S) (30*$S) (452*$S) (452*$S) $r
  $G.DrawPath($FramePen, $path)
  $path.Dispose()
}

# T+ 标识（含蓝描边方框）
function Draw-Monogram {
  param($G, [float]$Scale, $InkBrush, $PlusBrush, $FrameBrush, [switch]$Shadow)
  $wInk   = 50.0 * $Scale
  $wPlus  = 38.0 * $Scale
  $wFrame = 30.0 * $Scale
  if ($Shadow) {
    foreach ($d in 12, 18, 24) {
      $a = if ($d -eq 12) { 70 } elseif ($d -eq 18) { 40 } else { 20 }
      $dark     = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($a, 20, 11, 58))
      $dInkPen  = New-RoundPen $dark $wInk
      $dPlusPen = New-RoundPen $dark $wPlus
      Draw-Strokes $G $d $Scale $dInkPen $dPlusPen
      $dark.Dispose(); $dInkPen.Dispose(); $dPlusPen.Dispose()
    }
  }
  $framePen = New-RoundPen $FrameBrush $wFrame
  Draw-Frame $G $Scale $framePen
  $framePen.Dispose()

  $inkPen  = New-RoundPen $InkBrush  $wInk
  $plusPen = New-RoundPen $PlusBrush $wPlus
  Draw-Strokes $G 0 $Scale $inkPen $plusPen
  $inkPen.Dispose(); $plusPen.Dispose()
}

# T 笔画纵向渐变统一坐标（与 SVG userSpaceOnUse 120..372 一致）
function New-InkRect {
  return [System.Drawing.RectangleF]::new(104.0*$k, 120.0*$k, 180.0*$k, 252.0*$k)
}

# + 号纵向渐变坐标（与 SVG userSpaceOnUse 266..400 一致）
function New-PlusRect {
  return [System.Drawing.RectangleF]::new(301.0*$k, 266.0*$k, 134.0*$k, 134.0*$k)
}

# 外框描边渐变坐标（覆盖整个方框，与 SVG 对角线渐变一致）
function New-FrameRect {
  return [System.Drawing.RectangleF]::new(30.0*$k, 30.0*$k, 452.0*$k, 452.0*$k)
}

function New-Canvas {
  param([int]$W, [int]$H)
  $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  return @($bmp, $g)
}

function Save-Scaled {
  param([System.Drawing.Bitmap]$Master, [string]$Path, [int]$Size)
  $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.DrawImage($Master, 0, 0, $Size, $Size)
  $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
Write-Host "输出目录: $OutDir（主画布 ${MasterSize}px，立体版）"

$inkRect   = New-InkRect
$plusRect  = New-PlusRect
$frameRect = New-FrameRect

# ---- 1) 应用图标（蓝框 + 框内 T+，透明背景）----
$canvas = New-Canvas $MasterSize $MasterSize
$bmp = $canvas[0]; $g = $canvas[1]

$inkBrush   = New-VBrush  $inkRect  $cLgInkT $cLgInkB
$plusBrush  = New-VBrush3 $plusRect $cCaretT $cCaretM $cCaretB
$frameBrush = New-DBrush3 $frameRect $cFrameL $cFrameM $cFrameD
Draw-Monogram $g $k $inkBrush $plusBrush $frameBrush

foreach ($s in 512, 256, 192, 128, 64, 32) { Save-Scaled $bmp (Join-Path $OutDir "icon-$s.png") $s }
$inkBrush.Dispose(); $plusBrush.Dispose(); $frameBrush.Dispose()
$g.Dispose(); $bmp.Dispose()

# ---- 2) 彩色标识 ----
$canvas = New-Canvas $MasterSize $MasterSize
$bmp = $canvas[0]; $g = $canvas[1]
$inkBrush   = New-VBrush  $inkRect  $cLgInkT $cLgInkB
$plusBrush  = New-VBrush  $plusRect $cLgCaretT $cLgCaretB
$frameBrush = New-DBrush3 $frameRect $cFrameL $cFrameM $cFrameD
Draw-Monogram $g $k $inkBrush $plusBrush $frameBrush
$bmp.Save((Join-Path $OutDir 'mark-color.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$inkBrush.Dispose(); $plusBrush.Dispose(); $frameBrush.Dispose(); $g.Dispose(); $bmp.Dispose()

# ---- 3) 白色标识 ----
$canvas = New-Canvas $MasterSize $MasterSize
$bmp = $canvas[0]; $g = $canvas[1]
$inkBrush   = New-VBrush  $inkRect  $cInkTop $cInkBot
$plusBrush  = New-VBrush3 $plusRect $cCaretT $cCaretM $cCaretB
$frameBrush = New-DBrush3 $frameRect $cFrameL $cFrameM $cFrameD
Draw-Monogram $g $k $inkBrush $plusBrush $frameBrush
$bmp.Save((Join-Path $OutDir 'mark-white.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$inkBrush.Dispose(); $plusBrush.Dispose(); $frameBrush.Dispose(); $g.Dispose(); $bmp.Dispose()

# ---- 4/5) 完整标识 ----
function Draw-Lockup {
  param([string]$Path2x, [string]$Path, [bool]$Dark)
  $W = 960 * $k; $H = 512 * $k
  $canvas = New-Canvas ([int]$W) ([int]$H)
  $bmp = $canvas[0]; $g = $canvas[1]
  # 标识整体缩放并左上偏移，给右侧字标留出空间（与 lockup SVG 的 scale/translate 一致）
  $g.TranslateTransform(20.0 * $k, 46.0 * $k)
  $g.ScaleTransform(0.72, 0.72)
  if ($Dark) {
    $ib = New-VBrush  $inkRect  $cInkTop $cInkBot
    $cb = New-VBrush3 $plusRect $cCaretT $cCaretM $cCaretB
  } else {
    $ib = New-VBrush $inkRect  $cLgInkT $cLgInkB
    $cb = New-VBrush $plusRect $cLgCaretT $cLgCaretB
  }
  $fb = New-DBrush3 $frameRect $cFrameL $cFrameM $cFrameD
  Draw-Monogram $g $k $ib $cb $fb
  $ib.Dispose(); $cb.Dispose(); $fb.Dispose()
  $g.ResetTransform()

  $em = 86.0 * $k
  $font = New-Object System.Drawing.Font('Segoe UI', $em, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
  $sf = [System.Drawing.StringFormat]::GenericTypographic
  $textBrush = New-Object System.Drawing.SolidBrush($(if ($Dark) { $cInkTop } else { $cSlate }))
  $baseline = (292.0 + 0.365 * 86.0) * $k
  $asc = 0.80 * $em
  $g.DrawString('TextFlowPlus', $font, $textBrush, [single](418.0 * $k), [single]($baseline - $asc), $sf)
  $font.Dispose(); $textBrush.Dispose()
  $bmp.Save($Path2x, [System.Drawing.Imaging.ImageFormat]::Png)

  $bmp2 = New-Object System.Drawing.Bitmap(960, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g2.DrawImage($bmp, 0, 0, 960, 512)
  $bmp2.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g2.Dispose(); $bmp2.Dispose()
  $g.Dispose(); $bmp.Dispose()
}
Draw-Lockup (Join-Path $OutDir 'lockup-light@2x.png') (Join-Path $OutDir 'lockup-light.png') $false
Draw-Lockup (Join-Path $OutDir 'lockup-dark@2x.png') (Join-Path $OutDir 'lockup-dark.png') $true

# ---- 6) aie.ico ----
$icoSizes = 256, 128, 64, 48, 32, 16
$blobs = @()
foreach ($s in $icoSizes) {
  $t = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $tgs = [System.Drawing.Graphics]::FromImage($t)
  $tgs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $tgs.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $src = New-Object System.Drawing.Bitmap((Join-Path $OutDir 'icon-512.png'))
  $tgs.DrawImage($src, 0, 0, $s, $s)
  $ms = New-Object System.IO.MemoryStream
  $t.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $blobs += ,$ms.ToArray()
  $ms.Dispose(); $tgs.Dispose(); $t.Dispose(); $src.Dispose()
}
$fs = New-Object System.IO.FileStream((Join-Path $OutDir 'aie.ico'), [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
  $w = if ($icoSizes[$i] -ge 256) { 0 } else { $icoSizes[$i] }
  $bw.Write([byte]$w); $bw.Write([byte]$w); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$blobs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Close(); $fs.Close()
Write-Host '完成：立体版 PNG 全套 + aie.ico 已生成。'
