param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x86','x64','ARM64')][string]$Platform = 'x64',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet restore PdfReader/PdfReader.csproj --configfile NuGet.Config -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    if ($Publish) {
        $rid = "win-$($Platform.ToLowerInvariant())"
        dotnet publish PdfReader/PdfReader.csproj -c $Configuration -p:Platform=$Platform -r $rid --self-contained true -p:WindowsAppSDKSelfContained=true -o "artifacts/Folio-$Platform"
    } else {
        dotnet build PdfReader/PdfReader.csproj -c $Configuration -p:Platform=$Platform --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
