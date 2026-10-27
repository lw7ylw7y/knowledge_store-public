# Watches the app and its helper programs for network connections to anything but this PC (NFR-9, NFR-10), for the
# privacy check. A normal session should print nothing; the only expected exceptions are the first-run downloads.
#   powershell -ExecutionPolicy Bypass -File scripts\check-network.ps1 -Minutes 10
# Start the app first, use it as usual (add a book, search, summarize), and read the result at the end. This lists
# TCP connections and UDP endpoints each second; it does not capture packets, so also do the Wireshark run for the release.
param([int]$Minutes = 10)
$ErrorActionPreference = 'Stop'
$names = 'BookSearch', 'BookSearch.Worker', 'llama-server'
if (-not (Get-Process -Name 'BookSearch' -ErrorAction SilentlyContinue)) { throw 'BookSearch.exe is not running. Start the app first.' }

function Test-Local([string]$address) { $address -in '127.0.0.1', '::1', '0.0.0.0', '::', 'localhost' -or $address -like '::ffff:127.*' }

$seen = @{}
$end = (Get-Date).AddMinutes($Minutes)
Write-Host "Watching for $Minutes minutes. Use the app now. Press Ctrl+C to stop early."
while ((Get-Date) -lt $end) {
    $ids = @(Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object Id)
    foreach ($c in Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $ids -contains $_.OwningProcess -and $_.State -ne 'Listen' }) {
        if (-not (Test-Local $c.RemoteAddress)) {
            $key = "TCP $($c.OwningProcess) $($c.RemoteAddress):$($c.RemotePort)"
            if (-not $seen.ContainsKey($key)) {
                $seen[$key] = Get-Date
                Write-Host ("{0}  {1}  {2} -> {3}:{4}" -f (Get-Date -Format 'HH:mm:ss'), (Get-Process -Id $c.OwningProcess).ProcessName, $c.State, $c.RemoteAddress, $c.RemotePort) -ForegroundColor Yellow
            }
        }
    }
    foreach ($c in Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $ids -contains $_.OwningProcess }) {
        if (-not (Test-Local $c.LocalAddress)) {
            $key = "UDP $($c.OwningProcess) $($c.LocalAddress):$($c.LocalPort)"
            if (-not $seen.ContainsKey($key)) {
                $seen[$key] = Get-Date
                Write-Host ("{0}  {1}  UDP endpoint on {2}:{3} (listening on a non-local address)" -f (Get-Date -Format 'HH:mm:ss'), (Get-Process -Id $c.OwningProcess).ProcessName, $c.LocalAddress, $c.LocalPort) -ForegroundColor Yellow
            }
        }
    }
    Start-Sleep -Seconds 1
}
Write-Host ""
if ($seen.Count -eq 0) { Write-Host "No connection to anything but this PC in $Minutes minutes." -ForegroundColor Green }
else { Write-Host "$($seen.Count) non-local connection(s) were seen; each needs an explanation (see the lines above)." -ForegroundColor Red; exit 1 }
