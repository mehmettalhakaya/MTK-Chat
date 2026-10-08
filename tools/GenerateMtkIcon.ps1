# Rebuild the application icon with: powershell -ExecutionPolicy Bypass -File tools/GenerateMtkIcon.ps1
Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path $PSScriptRoot '..\src\MTKChat.Desktop\Assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$graphics.Clear([System.Drawing.Color]::Transparent)
$dark = [System.Drawing.ColorTranslator]::FromHtml('#121528')
$violet = [System.Drawing.ColorTranslator]::FromHtml('#8B76FF')
$mint = [System.Drawing.ColorTranslator]::FromHtml('#47E0C3')
$white = [System.Drawing.ColorTranslator]::FromHtml('#F8F7FF')
$tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
$tile.AddArc(10, 10, 70, 70, 180, 90)
$tile.AddArc(176, 10, 70, 70, 270, 90)
$tile.AddArc(176, 176, 70, 70, 0, 90)
$tile.AddArc(10, 176, 70, 70, 90, 90)
$tile.CloseFigure()
$graphics.FillPath([System.Drawing.SolidBrush]::new($dark), $tile)
$bubble = [System.Drawing.Drawing2D.GraphicsPath]::new()
$bubble.AddArc(37, 43, 182, 172, 180, 90)
$bubble.AddArc(37, 43, 182, 172, 270, 90)
$bubble.AddArc(37, 43, 182, 172, 0, 90)
$bubble.AddLine(151, 215, 123, 237)
$bubble.AddLine(123, 237, 111, 214)
$bubble.AddArc(37, 43, 182, 172, 90, 90)
$bubble.CloseFigure()
$outline = [System.Drawing.Pen]::new($violet, 14)
$outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$graphics.DrawPath($outline, $bubble)
$font = [System.Drawing.Font]::new('Segoe UI', 58, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$letterFormat = [System.Drawing.StringFormat]::new()
$letterFormat.Alignment = [System.Drawing.StringAlignment]::Center
$letterFormat.LineAlignment = [System.Drawing.StringAlignment]::Center
$letters = [System.Drawing.SolidBrush]::new($white)
$graphics.DrawString('MTK', $font, $letters, [System.Drawing.RectangleF]::new(42, 83, 172, 102), $letterFormat)
$mintPen = [System.Drawing.Pen]::new($mint, 8)
$mintPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$mintPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($mintPen, 97, 174, 159, 174)
$font.Dispose()
$letterFormat.Dispose()
$letters.Dispose()
$mintPen.Dispose()
$pngPath = Join-Path $iconDirectory 'mtk-chat.png'
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$icoPath = Join-Path $iconDirectory 'mtk-chat.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Dispose()
Write-Output $icoPath
