$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet run --project PdfReader.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
} finally { Pop-Location }
