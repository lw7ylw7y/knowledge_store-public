# Builds the desktop app (self-contained, unpackaged, win-x64) into publish\BookSearch. Run from any folder on Windows:
#   powershell -ExecutionPolicy Bypass -File scripts\publish-app.ps1
# Needs the .NET 8 SDK (x64). No Visual Studio and no separate Windows App SDK install should be needed.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish\BookSearch'

dotnet publish "$root\src\BookSearch.App\BookSearch.App.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

# The background indexer runs from the same folder as the app (the app starts BookSearch.Worker.exe from there).
dotnet publish "$root\src\BookSearch.Worker\BookSearch.Worker.csproj" -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the worker failed (exit code $LASTEXITCODE)" }

$vec = Join-Path $out 'native\win-x64\vec0.dll'
if (-not (Test-Path $vec)) {
    Write-Warning "sqlite-vec is missing from the publish folder ($vec). The app will start but cannot open the library."
}
Write-Host ""
Write-Host "Built: $out\BookSearch.exe"
Write-Host "Fresh-profile test:  `$env:BOOKSEARCH_DATA = `"`$env:TEMP\booksearch-fresh`"; & `"$out\BookSearch.exe`""
