# Moves the llama.cpp build the app downloads: downloads the Windows CPU x64 zip of the given release, computes its SHA-256 and
# prints the manifest entry to paste over the "llama-server-..." entry in src\BookSearch.Models\models.manifest.json.
#   powershell -ExecutionPolicy Bypass -File scripts\pin-llama-server.ps1 -Build b11430
# Find the build you have now with:  & "$env:LOCALAPPDATA\BookSearch\bin\llama-server.exe" --version   (it says "build 11430").
# Then run `dotnet test`: a test checks the entry's shape. Try the new build with the real models before shipping it.
param([Parameter(Mandatory)][ValidatePattern('^b\d+$')][string]$Build)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$asset = "llama-$Build-bin-win-cpu-x64.zip"
$url = "https://github.com/ggml-org/llama.cpp/releases/download/$Build/$asset"
$zip = Join-Path ([IO.Path]::GetTempPath()) $asset

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
$hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
Remove-Item $zip

@"
    {
      "id": "llama-server-$Build",
      "role": "runtime",
      "file": "$asset",
      "url": "$url",
      "sha256": "$hash",
      "sizeBytes": $size,
      "license": "MIT",
      "source": "ggml-org/llama.cpp release $Build, Windows CPU x64 (the program that runs the models; unpacked into bin)",
      "serverArgs": []
    },
"@
