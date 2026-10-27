# Samples the memory and CPU of the running app and its helper programs, for the release measurements (NFR: idle RAM, and the
# 8 GB check with indexing, search and a summary running together). Writes a CSV and prints the peak and last values.
#   powershell -ExecutionPolicy Bypass -File scripts\measure-app.ps1 -Minutes 5 -Label idle
# Start the app first. Run it again with another -Label for each situation (idle, indexing, searching + summary).
param([int]$Minutes = 5, [int]$IntervalSeconds = 5, [string]$Label = 'run', [string]$Out)
$ErrorActionPreference = 'Stop'
$names = 'BookSearch', 'BookSearch.Worker', 'llama-server'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) "eval-reports\memory-$Label-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null

if (-not (Get-Process -Name 'BookSearch' -ErrorAction SilentlyContinue)) { throw 'BookSearch.exe is not running. Start the app first.' }

$cores = [Environment]::ProcessorCount
$previous = @{}
$rows = New-Object System.Collections.Generic.List[object]
$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end) {
    $now = Get-Date
    $total = 0; $cpuTotal = 0; $detail = @()
    foreach ($p in Get-Process -Name $names -ErrorAction SilentlyContinue) {
        $mb = $p.WorkingSet64 / 1MB
        $cpu = 0
        if ($previous.ContainsKey($p.Id)) {
            $dt = ($now - $previous[$p.Id].Time).TotalSeconds
            if ($dt -gt 0) { $cpu = 100 * ($p.TotalProcessorTime - $previous[$p.Id].Cpu).TotalSeconds / $dt / $cores }
        }
        $previous[$p.Id] = @{ Time = $now; Cpu = $p.TotalProcessorTime }
        $total += $mb; $cpuTotal += $cpu
        $detail += "$($p.ProcessName)=$([math]::Round($mb))MB"
    }
    $rows.Add([pscustomobject]@{ Time = $now.ToString('s'); Label = $Label; TotalMB = [math]::Round($total); CpuPercent = [math]::Round($cpuTotal, 1); Processes = ($detail -join ' ') })
    Write-Host ("{0}  total {1,5} MB  cpu {2,5}%  {3}" -f $now.ToString('HH:mm:ss'), [math]::Round($total), [math]::Round($cpuTotal, 1), ($detail -join ' '))
    Start-Sleep -Seconds $IntervalSeconds
}
$rows | Export-Csv -Path $Out -NoTypeInformation
$peak = ($rows | Measure-Object TotalMB -Maximum).Maximum
$app = Get-Process -Name 'BookSearch' | Select-Object -First 1
Write-Host ""
Write-Host "Peak total (app + worker + model servers): $peak MB. App alone now: $([math]::Round($app.WorkingSet64 / 1MB)) MB."
Write-Host "NFR-6 idle target: 300 MB or less with the models unloaded (they unload after 10 idle minutes). CSV: $Out"
