#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Apply', 'Restore')]
    [string]$Action = 'Preview',
    [string]$DataDir = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'com.follow\clashx'),
    [string]$ServerName = '',
    [string]$BackupPath = '',
    [switch]$DomainsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Set-Field($Object, [string]$Name, $Value) {
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
}

function Assert-Stopped {
    if (Get-Process -Name FlClashX -ErrorAction SilentlyContinue) {
        throw 'Fully exit FlClashX using its tray menu, then run this command again. Closing its window may only minimize it.'
    }
}

function Read-Settings([string]$Path) {
    $outer = [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json
    if (-not $outer.PSObject.Properties['flutter.config']) {
        throw 'Unsupported settings format: flutter.config was not found.'
    }
    $inner = $outer.'flutter.config' | ConvertFrom-Json
    foreach ($field in @('profiles', 'currentProfileId', 'patchClashConfig', 'appSetting', 'networkProps')) {
        if (-not $inner.PSObject.Properties[$field]) { throw "Unsupported settings format: $field was not found." }
    }
    return [pscustomobject]@{ Outer = $outer; Inner = $inner }
}

function Get-ProxyNames([string]$Path) {
    # Read only proxy names; do not print credentials or subscription URLs.
    $inProxies = $false
    foreach ($line in [IO.File]::ReadAllLines($Path, $utf8)) {
        if ($line -match '^proxies:\s*$') { $inProxies = $true; continue }
        if ($inProxies -and $line -match '^\S') { break }
        if ($inProxies -and $line -match '^\s*-?\s*name:\s*(.+?)\s*$') {
            $name = $Matches[1]
            if ($name.StartsWith('"')) {
                try { $name = $name | ConvertFrom-Json } catch { throw 'Unsupported quoted proxy name.' }
            } elseif ($name.StartsWith("'")) {
                if (-not $name.EndsWith("'")) { throw 'Unsupported quoted proxy name.' }
                $name = $name.Substring(1, $name.Length - 2).Replace("''", "'")
            } else {
                $name = ($name -replace '\s+#.*$', '').Trim()
            }
            $name
        }
    }
}

function New-RoutingPlan($Settings, [string]$Root) {
    $cfg = $Settings.Inner
    $profiles = @($cfg.profiles | Where-Object { $_.id -ceq $cfg.currentProfileId })
    if ($profiles.Count -ne 1) { throw 'Exactly one active profile is required.' }
    $profile = $profiles[0]
    if ($profile.id -notmatch '^\d+$') { throw 'Unexpected profile ID; refusing to construct a file path.' }
    $profilePath = Join-Path $Root ('profiles\' + $profile.id + '.yaml')
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) { throw 'The active subscription profile file is missing.' }
    $names = @(Get-ProxyNames $profilePath)
    if ($names.Count -eq 0) { throw 'No proxy names could be read. No changes made.' }
    if ($ServerName) {
        $candidates = @($names | Where-Object { $_ -ceq $ServerName })
    } else {
        $candidates = @($names | Where-Object { $_ -match '(^|\s)(США|USA|United States)(\s|$)' })
    }
    if ($candidates.Count -ne 1) {
        throw 'A unique US server was not found. Use -ServerName with the exact full proxy name, including its flag.'
    }
    $target = [string]$candidates[0]
    if ($target -match '[,\r\n]') { throw 'The server name cannot contain commas or newlines.' }

    $values = @(
        'DOMAIN,localhost,DIRECT',
        'DOMAIN-SUFFIX,local,DIRECT',
        'IP-CIDR,127.0.0.0/8,DIRECT,no-resolve',
        'IP-CIDR,10.0.0.0/8,DIRECT,no-resolve',
        'IP-CIDR,172.16.0.0/12,DIRECT,no-resolve',
        'IP-CIDR,192.168.0.0/16,DIRECT,no-resolve',
        'IP-CIDR6,::1/128,DIRECT,no-resolve',
        'IP-CIDR6,fc00::/7,DIRECT,no-resolve',
        'IP-CIDR6,fe80::/10,DIRECT,no-resolve'
    )
    # Explicit entries remain usable even if a geosite database is unavailable.
    foreach ($domain in @('openai.com', 'chatgpt.com', 'chat.com', 'oaistatic.com', 'oaiusercontent.com', 'openaiapi-site.azureedge.net', 'o33249.ingest.sentry.io', 'chatgpt.livekit.cloud')) {
        $values += "DOMAIN-SUFFIX,$domain,$target"
    }
    $geoPath = Join-Path $Root 'GeoSite.dat'
    if (Test-Path -LiteralPath $geoPath -PathType Leaf) {
        $geoText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($geoPath))
        if ($geoText.Contains('openai')) { $values += "GEOSITE,openai,$target" }
    }
    if (-not $DomainsOnly) {
        # Entire VS Code is routed because extensions can use helper processes.
        foreach ($process in @('ChatGPT.exe', 'Code.exe', 'codex.exe', 'Codex.exe', 'codex-code-mode-host.exe')) {
            $values += "PROCESS-NAME,$process,$target"
        }
    }
    $values += 'MATCH,DIRECT'
    $rules = @($values | ForEach-Object { [pscustomobject]@{ id = [guid]::NewGuid().ToString(); value = $_ } })
    Set-Field $profile 'overrideData' ([pscustomobject]@{
        enable = $true
        rule = [pscustomobject]@{ type = 'override'; overrideRules = $rules; addedRules = @() }
    })
    Set-Field $cfg.patchClashConfig 'mode' 'rule'
    Set-Field $cfg.patchClashConfig 'find-process-mode' 'always'
    if (-not $cfg.patchClashConfig.PSObject.Properties['tun']) { throw 'Unsupported settings format: tun was not found.' }
    Set-Field $cfg.patchClashConfig.tun 'enable' $true
    foreach ($field in @('autoLaunch', 'silentLaunch', 'autoRun')) { Set-Field $cfg.appSetting $field $true }
    Set-Field $cfg.appSetting 'closeConnections' $true
    if ($cfg.appSetting.PSObject.Properties['overrideProviderSettings']) {
        Set-Field $cfg.appSetting 'overrideProviderSettings' $true
    }
    # Keep DNS, subscription URL, credentials, and other profiles intact.
    Set-Field $Settings.Outer 'flutter.config' ($cfg | ConvertTo-Json -Depth 100 -Compress)
    $json = $Settings.Outer | ConvertTo-Json -Depth 100 -Compress
    $roundTrip = ($json | ConvertFrom-Json).'flutter.config' | ConvertFrom-Json
    if ($roundTrip.patchClashConfig.mode -ne 'rule') { throw 'Settings serialization failed.' }
    return [pscustomobject]@{ Json = $json; Rules = $values; Target = $target; ProfileId = $profile.id }
}

function Write-WithBackup([string]$Destination, [byte[]]$Bytes) {
    $backup = $Destination + '.openai-backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff') + '.json'
    # Backup stays on this computer because it contains private subscription data.
    if (Test-Path -LiteralPath $backup) { throw 'Backup filename already exists; no changes made.' }
    $temp = $Destination + '.openai-' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllBytes($temp, $Bytes)
        [IO.File]::Replace($temp, $Destination, $backup)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
    Write-Host "Backup: $backup"
}

try {
    $root = [IO.Path]::GetFullPath($DataDir)
    $prefsPath = Join-Path $root 'shared_preferences.json'
    if (-not (Test-Path -LiteralPath $prefsPath -PathType Leaf)) { throw "FlClashX settings were not found in $root" }
    if ($Action -eq 'Restore') {
        Assert-Stopped
        if (-not $BackupPath) { throw 'Specify -BackupPath with the backup printed during Apply.' }
        $resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
        if ([IO.Path]::GetDirectoryName($resolvedBackup) -ine $root -or
            [IO.Path]::GetFileName($resolvedBackup) -notmatch '^shared_preferences\.json\.openai-backup-.*\.json$') {
            throw 'Restore accepts only a backup file from this FlClashX settings directory.'
        }
        $null = Read-Settings $resolvedBackup
        Write-WithBackup $prefsPath ([IO.File]::ReadAllBytes($resolvedBackup))
        Write-Host 'Original settings restored. Start FlClashX again.'
    } else {
        $settings = Read-Settings $prefsPath
        $plan = New-RoutingPlan $settings $root
        Write-Host "US proxy: $($plan.Target)"
        Write-Host "Active profile: $($plan.ProfileId)"
        Write-Host 'Mode: Rule. TUN: enabled. Automatic startup: enabled.'
        Write-Host 'Rules:'
        $plan.Rules | ForEach-Object { Write-Host "  $_" }
        if ($Action -eq 'Preview') {
            Write-Host 'PREVIEW ONLY: no files or network settings changed.'
        } else {
            Assert-Stopped
            Write-WithBackup $prefsPath ($utf8.GetBytes($plan.Json))
            Write-Host 'Saved. Start FlClashX again. Verify Rule mode and TUN before using ChatGPT.'
        }
    }
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
