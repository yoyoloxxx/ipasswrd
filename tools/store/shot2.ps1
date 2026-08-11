param([string]$Name='shot')
Add-Type @"
using System; using System.Runtime.InteropServices;
public class WC {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$p = Get-Process IPasswrd.App -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if(-not $p){ throw 'no IPasswrd window' }
$h=$p.MainWindowHandle
[WC]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600
$r=New-Object WC+RECT; [WC]::GetWindowRect($h,[ref]$r) | Out-Null
$w=$r.R-$r.L; $ht=$r.B-$r.T
Add-Type -AssemblyName System.Drawing
$bmp=New-Object System.Drawing.Bitmap $w,$ht
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L,$r.T,0,0,(New-Object System.Drawing.Size($w,$ht)))
$g.Dispose()
$out='D:\MyProjects\IPasswrd\Store\screenshots'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$path=Join-Path $out "$Name.png"
$bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"$path ($w x $ht)"
