#Requires -Version 5.1
param([switch]$Prepare)
$ErrorActionPreference = 'Stop'
$appFolder = Join-Path $env:USERPROFILE 'ChatGPTBridge'
$appExe = Join-Path $appFolder 'ChatGPTBridge.exe'
$probeExe = Join-Path $appFolder 'codex-route-probe.exe'
$dataFolder = Join-Path $appFolder 'ChatGPTBridge-data'
$taskName = 'ChatGPTBridgeGuard-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$report = Join-Path $PSScriptRoot 'verification\tun-loss-report.txt'
function Stop-OwnWindow {
    $runtimePath = Join-Path $dataFolder 'runtime-status.json'
    if (Test-Path -LiteralPath $runtimePath) {
        $runtime = Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $appProcess = Get-Process -Id $runtime.PID -ErrorAction SilentlyContinue
        if ($appProcess -and $appProcess.Path -eq $appExe) { Stop-Process -Id $appProcess.Id -Force }
    }
}
function Probe([string]$name, [bool]$waitForGuard) {
    $outputPath = Join-Path $dataFolder ($name + '.json')
    $argument = if ($waitForGuard) { '--native-ip-probe-wait' } else { '--native-ip-probe' }
    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList $argument,('"' + $outputPath + '"') -WindowStyle Hidden -PassThru -Wait
    $result = Get-Content -LiteralPath $outputPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Content -LiteralPath $report -Value ($name + ': ' + ($result | ConvertTo-Json -Compress)) -Encoding UTF8
    return $result
}
if ($Prepare) {
    Stop-OwnWindow
    Stop-ScheduledTask -TaskName $taskName
    try {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $remaining = @(Get-Process -Name ChatGPTBridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe })
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\ChatGPTBridge.exe') -Destination $appExe -Force
        Copy-Item -LiteralPath $appExe -Destination $probeExe -Force
    } finally {
        Start-ScheduledTask -TaskName $taskName
        Start-Process -FilePath $appExe -ArgumentList '--tray' -WindowStyle Hidden
    }
    Set-Content -LiteralPath $report -Value ('Literal-IP firewall checks ' + [DateTime]::UtcNow.ToString('o')) -Encoding UTF8
    $positive = Probe 'native-ip-tun-on' $true
    if (-not $positive.Success -or $positive.Country -ne 'loc=US') { throw 'Positive control did not reach US; do not run the negative test.' }
    Write-Output 'PASS literal-IP positive control reaches US through TUN.'
    exit
}
$positive = Get-Content -LiteralPath (Join-Path $dataFolder 'native-ip-tun-on.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $positive.Success -or $positive.Country -ne 'loc=US') { throw 'Missing successful positive control.' }
$status = Get-Content -LiteralPath (Join-Path $dataFolder 'guard-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $status.Ready -or ([DateTime]::UtcNow - [DateTime]::Parse($status.UpdatedUtc).ToUniversalTime()).TotalSeconds -ge 40) { throw 'Background firewall guard is not ready.' }
Stop-OwnWindow
try {
    $null = Invoke-RestMethod -Uri 'http://127.0.0.1:9090/configs' -Method Patch -ContentType 'application/json' -Body '{"tun":{"enable":false}}' -TimeoutSec 5
    $config = Invoke-RestMethod -Uri 'http://127.0.0.1:9090/configs' -TimeoutSec 5
    if ($config.tun.enable) { throw 'TUN did not turn off.' }
    $negative = Probe 'native-ip-tun-off' $false
    if ($negative.Success) { throw 'FAIL native process bypassed disabled TUN.' }
    Add-Content -LiteralPath $report -Value 'PASS native process cannot connect to a literal Internet IP when TUN is off.' -Encoding UTF8
} finally {
    try {
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:9090/configs' -Method Patch -ContentType 'application/json' -Body '{"tun":{"enable":true}}' -TimeoutSec 5
        $config = Invoke-RestMethod -Uri 'http://127.0.0.1:9090/configs' -TimeoutSec 5
        if (-not $config.tun.enable) { throw 'TUN restoration failed.' }
        Add-Content -LiteralPath $report -Value 'PASS TUN restored in finally.' -Encoding UTF8
    } finally { Start-Process -FilePath $appExe -ArgumentList '--tray' -WindowStyle Hidden }
}
$restored = $null
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    Start-Sleep -Milliseconds 1000
    $restored = Probe 'native-ip-tun-restored' $false
    if ($restored.Success -and $restored.Country -eq 'loc=US') { break }
}
if (-not $restored.Success -or $restored.Country -ne 'loc=US') { throw 'Restored native route did not return US.' }
Add-Content -LiteralPath $report -Value 'PASS native route returns to US after TUN restoration.' -Encoding UTF8
Get-Content -LiteralPath $report -Encoding UTF8
