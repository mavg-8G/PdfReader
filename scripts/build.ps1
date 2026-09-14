param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','ARM64')][string]$Platform = 'x64',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    node --use-system-ca scripts/setup-viewer.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Viewer setup failed.' }
    node --use-system-ca scripts/setup-ocr.mjs
    if ($LASTEXITCODE -ne 0) { throw 'OCR setup failed.' }
    dotnet restore PdfReader/PdfReader.csproj --configfile NuGet.Config -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    if ($Publish) {
        $rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        dotnet publish PdfReader/PdfReader.csproj -c $Configuration -p:Platform=$Platform -r $rid --self-contained true -p:WindowsAppSDKSelfContained=true -o "artifacts/Folio-$Platform"
    } else {
        dotnet build PdfReader/PdfReader.csproj -c $Configuration -p:Platform=$Platform --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
