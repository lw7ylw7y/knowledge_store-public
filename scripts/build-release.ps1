# Builds the installer: publishes the app, compiles scripts\installer\BookSearch.iss with Inno Setup, and writes the installer's
# SHA-256 next to it. llama.cpp is not in the installer: the app downloads it on first run, like the models (see the manifest). Fails if the installer is over 200 MB (NFR-6).
#   powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 0.1.0
# Needs the .NET 8 SDK and Inno Setup 6 (free: winget install JRSoftware.InnoSetup, or choco install innosetup).
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish\BookSearch'
$outDir = Join-Path $root 'publish\installer'
$limit = 200MB

& (Join-Path $PSScriptRoot 'publish-app.ps1')
if ($LASTEXITCODE -ne 0) { throw 'publish-app.ps1 failed.' }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it: winget install JRSoftware.InnoSetup' }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$outDir" (Join-Path $PSScriptRoot 'installer\BookSearch.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$installer = Join-Path $outDir "Docent-Setup-$Version.exe"
$hash = (Get-FileHash -Algorithm SHA256 -Path $installer).Hash.ToLowerInvariant()
# The usual "<hash>  <file name>" line, so `sha256sum -c` and a manual comparison with certutil both work.
Set-Content -Path "$installer.sha256" -Value "$hash  $(Split-Path -Leaf $installer)" -Encoding ASCII

$size = (Get-Item $installer).Length
Write-Host ("Installer: {0} ({1:N1} MB)" -f $installer, ($size / 1MB))
Write-Host "SHA-256:   $hash"
if ($size -gt $limit) { throw ("The installer is {0:N1} MB; the limit is 200 MB (NFR-6)." -f ($size / 1MB)) }
