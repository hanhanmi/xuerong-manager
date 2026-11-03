// 生成应用源图标 PNG（1024x1024），供 `tauri icon` 生成全套图标
// 粉色渐变圆角方块 + 白色「雪」字
Add-Type -AssemblyName System.Drawing

$size = 1024
$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# 粉色对角渐变
$grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
  [System.Drawing.Point]::new(0, 0),
  [System.Drawing.Point]::new($size, $size),
  [System.Drawing.Color]::FromArgb(255, 255, 120, 160),
  [System.Drawing.Color]::FromArgb(255, 214, 48, 100)
)
$g.FillRectangle($grad, 0, 0, $size, $size)

# 左上柔光
$hl = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
  [System.Drawing.Point]::new(0, 0),
  [System.Drawing.Point]::new([int]($size * 0.7), [int]($size * 0.7)),
  [System.Drawing.Color]::FromArgb(90, 255, 255, 255),
  [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
)
$g.FillEllipse($hl, [int](-$size * 0.25), [int](-$size * 0.35), [int]($size * 1.5), [int]($size * 1.4))

# 白色「雪」字
$font = New-Object System.Drawing.Font("Microsoft YaHei UI", [float]($size * 0.66), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$rect = New-Object System.Drawing.RectangleF(0, [float]($size * 0.02), [float]$size, [float]$size)
$g.DrawString("雪", $font, $brush, $rect, $sf)

$g.Dispose()
$grad.Dispose()
$hl.Dispose()

$out = Join-Path $PSScriptRoot "app-icon.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Generated: $out ($((Get-Item $out).Length) bytes)"
