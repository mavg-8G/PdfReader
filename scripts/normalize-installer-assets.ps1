# Creates the unqualified fallback files referenced by Instalador/Package.appxmanifest.
# AppX validates these exact files against their logical pixel dimensions.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$imageDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'Instalador/Images'

$copyMap = [ordered]@{
    'StoreLogo.png' = 'StoreLogo.scale-100.png'
    'Square150x150Logo.png' = 'Square150x150Logo.scale-100.png'
    'Square44x44Logo.png' = 'Square44x44Logo.scale-100.png'
    'Wide310x150Logo.png' = 'Wide310x150Logo.scale-100.png'
    'SmallTile.png' = 'SmallTile.scale-100.png'
    'LargeTile.png' = 'LargeTile.scale-100.png'
    'SplashScreen.png' = 'SplashScreen.scale-100.png'
}
foreach ($destinationName in $copyMap.Keys) {
    $sourcePath = Join-Path $imageDir $copyMap[$destinationName]
    if (!(Test-Path -LiteralPath $sourcePath)) { throw "Missing source asset: $sourcePath" }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $imageDir $destinationName) -Force
}

$badgeSource = Join-Path $imageDir 'BadgeLogo.scale-400.png'
$badgePath = Join-Path $imageDir 'BadgeLogo.png'
$sourceBitmap = [System.Drawing.Bitmap]::FromFile($badgeSource)
try {
    $badgeBitmap = [System.Drawing.Bitmap]::new(24, 24, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($badgeBitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceBitmap, [System.Drawing.Rectangle]::new(0, 0, 24, 24))
        } finally { $graphics.Dispose() }
        $badgeBitmap.Save($badgePath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $badgeBitmap.Dispose() }
} finally { $sourceBitmap.Dispose() }

$expected = [ordered]@{
    'StoreLogo.png' = @(50, 50); 'Square150x150Logo.png' = @(150, 150); 'Square44x44Logo.png' = @(44, 44)
    'Wide310x150Logo.png' = @(310, 150); 'SmallTile.png' = @(71, 71); 'LargeTile.png' = @(310, 310)
    'SplashScreen.png' = @(620, 300); 'BadgeLogo.png' = @(24, 24)
}
foreach ($assetName in $expected.Keys) {
    $assetBitmap = [System.Drawing.Bitmap]::FromFile((Join-Path $imageDir $assetName))
    try {
        if ($assetBitmap.Width -ne $expected[$assetName][0] -or $assetBitmap.Height -ne $expected[$assetName][1]) {
            throw "$assetName is $($assetBitmap.Width)x$($assetBitmap.Height), expected $($expected[$assetName][0])x$($expected[$assetName][1])."
        }
    } finally { $assetBitmap.Dispose() }
}
Write-Output 'Installer fallback assets have valid AppX dimensions:'
$expected.Keys | ForEach-Object { Write-Output "  $_ : $($expected[$_][0])x$($expected[$_][1])" }
