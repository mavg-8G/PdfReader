param([switch]$SkipBrowser)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet run --project PdfReader.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    node --test tests/policy.test.mjs tests/ocr.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Viewer policy tests failed.' }
    if (!$SkipBrowser) {
        node tests/viewer.integration.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Renderer integration tests failed.' }
    }
} finally { Pop-Location }
