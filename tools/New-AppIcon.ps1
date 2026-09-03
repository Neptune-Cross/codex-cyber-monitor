[CmdletBinding()]
param(
    [string]$OutputPath = (
        Join-Path (Split-Path -Parent $PSScriptRoot) `
            'src\CodexCyberMonitor\Assets\CodexCyberMonitor.ico'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CodexCyberIconNative {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

$directory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$bitmap = [Drawing.Bitmap]::new(256, 256)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)

$points = [Drawing.PointF[]]@(
    [Drawing.PointF]::new(128, 12),
    [Drawing.PointF]::new(224, 48),
    [Drawing.PointF]::new(212, 150),
    [Drawing.PointF]::new(176, 208),
    [Drawing.PointF]::new(128, 244),
    [Drawing.PointF]::new(80, 208),
    [Drawing.PointF]::new(44, 150),
    [Drawing.PointF]::new(32, 48)
)

$fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(196, 43, 28))
$outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(114, 18, 13), 10)
$white = [Drawing.SolidBrush]::new([Drawing.Color]::White)

try {
    $graphics.FillPolygon($fill, $points)
    $graphics.DrawPolygon($outline, $points)
    $graphics.FillRectangle($white, 112, 62, 32, 101)
    $graphics.FillEllipse($white, 112, 181, 32, 32)

    $handle = $bitmap.GetHicon()
    try {
        $icon = [Drawing.Icon]::FromHandle($handle)
        $stream = [IO.FileStream]::new(
            $OutputPath,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        try {
            $icon.Save($stream)
        }
        finally {
            $stream.Dispose()
            $icon.Dispose()
        }
    }
    finally {
        [void][CodexCyberIconNative]::DestroyIcon($handle)
    }
}
finally {
    $white.Dispose()
    $outline.Dispose()
    $fill.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $OutputPath
