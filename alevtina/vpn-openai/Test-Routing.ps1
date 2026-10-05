#Requires -Version 5.1
# Exercises file mutation only in a disposable directory, with synthetic data.
$ErrorActionPreference = 'Stop'
$routingScript = Join-Path $PSScriptRoot 'Set-OpenAIRouting.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('openai-routing-test-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$null = New-Item -ItemType Directory -Path (Join-Path $fixture 'profiles') -Force
$settingsPath = Join-Path $fixture 'shared_preferences.json'

function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
# The fixture does not control the real running VPN client.
function Get-Process { param($Name, $ErrorAction) return $null }

try {
    $config = [pscustomobject]@{
        profiles = @(
            [pscustomobject]@{ id = '100'; url = 'https://example.invalid/private-subscription'; selectedMap = @{ GLOBAL = 'USA' }; overrideData = @{ enable = $false } },
            [pscustomobject]@{ id = '200'; url = 'https://example.invalid/other'; overrideData = @{ enable = $false } }
        )
        currentProfileId = '100'
        patchClashConfig = @{ mode = 'global'; tun = @{ enable = $false }; dns = @{ enable = $true; nameserver = @('1.1.1.1') } }
        appSetting = @{ autoRun = $false; autoLaunch = $false; minimizeOnExit = $true }
        networkProps = @{ systemProxy = $false; routeMode = 'config' }
    }
    $original = $utf8.GetBytes((@{ 'flutter.config' = ($config | ConvertTo-Json -Depth 100 -Compress); untouched = 'preserve-me' } | ConvertTo-Json -Depth 100))
    [IO.File]::WriteAllBytes($settingsPath, $original)
    [IO.File]::WriteAllText((Join-Path $fixture 'profiles\100.yaml'), "proxies:`n  - name: 'USA'`n    type: vless`nproxy-groups:`n  - name: IgnoreThisGroup`nrules:`n  - MATCH,DIRECT`n", $utf8)
    $originalHash = (Get-FileHash -LiteralPath $settingsPath).Hash
    & $routingScript -Action Preview -DataDir $fixture
    Assert ((Get-FileHash -LiteralPath $settingsPath).Hash -eq $originalHash) 'Preview changed settings.'
    Assert (@(Get-ChildItem -LiteralPath $fixture -Filter '*.openai-backup-*').Count -eq 0) 'Preview created a backup.'

    & $routingScript -Action Apply -DataDir $fixture
    $outer = [IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json
    $updated = $outer.'flutter.config' | ConvertFrom-Json
    Assert ($outer.untouched -eq 'preserve-me') 'An outer preference was lost.'
    Assert ($updated.profiles[0].url -eq $config.profiles[0].url) 'Subscription URL changed.'
    Assert (($updated.profiles[1] | ConvertTo-Json -Depth 100 -Compress) -eq ($config.profiles[1] | ConvertTo-Json -Depth 100 -Compress)) 'Inactive profile changed.'
    Assert ($updated.patchClashConfig.dns.nameserver[0] -eq '1.1.1.1') 'Existing DNS changed.'
    Assert ($updated.patchClashConfig.mode -eq 'rule' -and $updated.patchClashConfig.tun.enable) 'Rule/TUN setup failed.'
    Assert ($updated.appSetting.autoRun -and $updated.appSetting.autoLaunch) 'Autostart setup failed.'
    Assert ($updated.profiles[0].overrideData.rule.type -eq 'override') 'Original MATCH rule would remain active.'
    $rules = @($updated.profiles[0].overrideData.rule.overrideRules.value)
    Assert ($rules[-1] -eq 'MATCH,DIRECT') 'Unmatched traffic is not direct.'
    Assert ($rules -contains 'DOMAIN-SUFFIX,chatgpt.com,USA') 'ChatGPT domain is missing.'
    Assert ($rules -contains 'PROCESS-NAME,Code.exe,USA') 'VS Code coverage is missing.'
    Assert (@($rules | Where-Object { $_ -match 'chatgpt.*DIRECT' }).Count -eq 0) 'ChatGPT has a DIRECT rule.'
    $backup = @(Get-ChildItem -LiteralPath $fixture -Filter '*.openai-backup-*')[0].FullName
    Assert ((Get-FileHash -LiteralPath $backup).Hash -eq $originalHash) 'Backup is not byte-exact.'
    & $routingScript -Action Restore -DataDir $fixture -BackupPath $backup
    Assert ((Get-FileHash -LiteralPath $settingsPath).Hash -eq $originalHash) 'Restore is not byte-exact.'
    & $routingScript -Action Apply -DataDir $fixture -DomainsOnly
    $domainsConfig = ([IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json).'flutter.config' | ConvertFrom-Json
    Assert (@($domainsConfig.profiles[0].overrideData.rule.overrideRules.value | Where-Object { $_ -match '^PROCESS-' }).Count -eq 0) 'DomainsOnly retained process rules.'
    Write-Host 'PASS: preview is read-only; active-profile routing, preservation, backup, restore, and DomainsOnly passed.'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedFixture) -match '^openai-routing-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    } else { throw 'Unsafe test cleanup path.' }
}
