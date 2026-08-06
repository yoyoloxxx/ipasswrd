# Снимает окно IPasswrd ровно 1280x800 - размер, который принимает витрина
# Chrome Web Store. Окно подгоняется под этот размер ДО съёмки, чтобы не было
# пересжатия: масштабирование скриншота съедает тонкие линии интерфейса.
param([string]$Name = 'shot')
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$p = Get-Process IPasswrd.App -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "Окно IPasswrd не найдено" }
$h = $p.MainWindowHandle

[W]::SetForegroundWindow($h) | Out-Null
[W]::SetWindowPos($h, [IntPtr]::Zero, 80, 80, 1280, 800, 0x0040) | Out-Null
Start-Sleep -Milliseconds 900

$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g.Dispose()

$out = 'D:\MyProjects\IPasswrd\Store\screenshots'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$path = Join-Path $out "$Name.png"

if ($w -ne 1280 -or $ht -ne 800) {
    # окно не встало точно (DPI / рамки) - доводим до нужного размера
    $fix = New-Object System.Drawing.Bitmap 1280, 800
    $fg = [System.Drawing.Graphics]::FromImage($fix)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.DrawImage($bmp, 0, 0, 1280, 800)
    $fg.Dispose(); $bmp.Dispose()
    $bmp = $fix
}
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"$path  ($w x $ht -> 1280x800)"
