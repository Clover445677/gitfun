#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$appFolder = Join-Path $env:USERPROFILE 'ChatGPTBridge'
$appExe = Join-Path $appFolder 'ChatGPTBridge.exe'
$data = Join-Path $appFolder 'ChatGPTBridge-data'
$probeExe = Join-Path $appFolder 'codex-route-probe.exe'
$report = Join-Path $PSScriptRoot 'verification\forgotten-app-report.txt'
$runtime = Get-Content -LiteralPath (Join-Path $data 'runtime-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $runtime.Connected -or -not $runtime.ProtectedRouteReady) { throw 'The ordinary GUI is not connected; do not run this test.' }
$gui = Get-Process -Id $runtime.PID
if ($gui.Path -ne $appExe) { throw 'Runtime PID is not this application.' }
Set-Content -LiteralPath $report -Value ('Forgotten-app check ' + [DateTime]::UtcNow.ToString('o')) -Encoding UTF8
Stop-Process -Id $gui.Id -Force
try {
    Start-Sleep -Milliseconds 2500
    $resultPath = Join-Path $data 'native-ip-without-bridge.json'
    $process = Start-Process -FilePath $probeExe -ArgumentList '--native-ip-probe',('"' + $resultPath + '"') -WindowStyle Hidden -PassThru -Wait
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Content -LiteralPath $report -Value ($result | ConvertTo-Json -Compress) -Encoding UTF8
    if ($result.Success) { throw 'Native request bypassed the closed bridge.' }
    $socket = New-Object Net.Sockets.TcpClient
    $bridgeClosed = $false
    try { $socket.Connect('127.0.0.1',18881) } catch { $bridgeClosed = $true } finally { $socket.Dispose() }
    if (-not $bridgeClosed) { throw 'The mandatory Chrome bridge was still listening.' }
    Add-Content -LiteralPath $report -Value 'PASS native Internet request blocked while GUI is closed; Chrome mandatory proxy unavailable with no DIRECT fallback.' -Encoding UTF8
} finally { Start-Process -FilePath $appExe -ArgumentList '--tray' -WindowStyle Hidden }
Get-Content -LiteralPath $report -Encoding UTF8
