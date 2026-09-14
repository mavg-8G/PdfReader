param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$wixVersion = '5.0.2'
$toolDir = Join-Path $repo '.cache/wix'
$wix = Join-Path $toolDir 'wix.exe'
$publishDir = Join-Path $repo 'artifacts/installer-publish/win-x64'
$outputDir = Join-Path $repo 'artifacts/installer'
$extension = Join-Path $toolDir ".wix/extensions/WixToolset.UI.wixext/$wixVersion/wixext5/WixToolset.UI.wixext.dll"
$savedRollForward = $env:DOTNET_ROLL_FORWARD
Push-Location $repo
try {
    # WiX 5 targets .NET 6; allow the build tool to use an installed newer runtime.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    if (!(Test-Path -LiteralPath $wix)) {
        dotnet tool install wix --version $wixVersion --tool-path $toolDir --allow-roll-forward --configfile (Join-Path $repo 'NuGet.Config')
        if ($LASTEXITCODE -ne 0) { throw 'Unable to install the local WiX build tool.' }
    }
    if (!(Test-Path -LiteralPath $extension)) {
        Push-Location $toolDir
        try {
            & $wix extension add "WixToolset.UI.wixext/$wixVersion"
            if ($LASTEXITCODE -ne 0) { throw 'Unable to install the WiX setup dialog extension.' }
        } finally { Pop-Location }
    }
    if (!$SkipPublish) {
        node --use-system-ca scripts/setup-viewer.mjs
        if ($LASTEXITCODE -ne 0) { throw 'PDF renderer setup failed.' }
        node --use-system-ca scripts/setup-ocr.mjs
        if ($LASTEXITCODE -ne 0) { throw 'OCR setup failed.' }
        # A fresh staging folder prevents obsolete build artifacts entering the MSI.
        if (Test-Path -LiteralPath $publishDir) {
            $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
            $expectedPublish = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/installer-publish/win-x64'))
            if ($resolvedPublish -ne $expectedPublish -or !(($resolvedPublish + '\').StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Unexpected installer staging path.' }
            Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
        }
        dotnet restore PdfReader/PdfReader.csproj --configfile (Join-Path $repo 'NuGet.Config') -p:Platform=x64 -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
        dotnet publish PdfReader/PdfReader.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore -p:WindowsAppSDKSelfContained=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw 'Publishing Folio failed.' }
    }
    foreach ($required in @('PdfReader.exe','PdfReader.runtimeconfig.json','coreclr.dll','Microsoft.UI.Xaml.dll','Assets/Folio.ico','Viewer/vendor/build/pdf.mjs','Viewer/ocr-vendor/worker.min.js','Viewer/ocr-vendor/eng.traineddata.gz','Viewer/ocr-vendor/spa.traineddata.gz')) {
        if (!(Test-Path -LiteralPath (Join-Path $publishDir $required))) { throw "Missing installer payload: $required" }
    }
    $config = Get-Content -LiteralPath (Join-Path $publishDir 'PdfReader.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$config.runtimeOptions.includedFrameworks) { throw 'Installer payload must include the .NET runtime.' }
    $payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir 'PdfReader.exe')).ProductVersion.Split('+')[0]
    if ([version]$payloadVersion -ne [version]$Version) { throw "Published app version $payloadVersion does not match MSI version $Version. Run without -SkipPublish." }
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    $msi = Join-Path $outputDir "Folio-$Version-x64.msi"
    & $wix build installer/Folio.wxs -arch x64 -ext $extension "-d" "Version=$Version" "-d" "PublishDir=$publishDir" -pdbtype none -intermediatefolder (Join-Path $repo '.cache/installer-build') -out $msi
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    $instructions = [IO.File]::ReadAllText((Join-Path $repo 'installer/INSTALL.txt')).Replace('Folio-1.0.0-x64.msi', "Folio-$Version-x64.msi")
    [IO.File]::WriteAllText((Join-Path $outputDir 'INSTALL.txt'), $instructions)
    $hash = Get-FileHash -LiteralPath $msi -Algorithm SHA256
    "$($hash.Hash)  $([IO.Path]::GetFileName($msi))" | Set-Content -LiteralPath "$msi.sha256" -Encoding ascii
    Get-Item -LiteralPath $msi | Select-Object FullName, Length
} finally {
    $env:DOTNET_ROLL_FORWARD = $savedRollForward
    Pop-Location
}
