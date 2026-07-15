[CmdletBinding()]
param(
    [string]$ControlApiUrl = "http://127.0.0.1:5180",
    [int]$PalDefenderPort = 17993,
    [int]$RconPort = 25575,
    [Security.SecureString]$AdminApiKey,
    [string]$AdminApiKeyFile = "",
    [switch]$FailOnMaintenance
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = [Uri]$ControlApiUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "http" -or -not $uri.IsLoopback) {
    throw "ControlApiUrl must be an absolute loopback HTTP URL."
}
$base = $ControlApiUrl.TrimEnd("/")

function Get-PlainText([Security.SecureString]$SecureString) {
    if ($null -eq $SecureString) {
        throw "A required secret was not provided."
    }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Assert-PrivateSecretFile([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The API key path must be a regular, non-reparse-point file."
    }

    $broadSids = @("S-1-1-0", "S-1-5-11", "S-1-5-32-545")
    $acl = Get-Acl -LiteralPath $resolved
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            continue
        }
        $sid = $rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]).Value
        $readRights = [Security.AccessControl.FileSystemRights]::Read -bor
            [Security.AccessControl.FileSystemRights]::ReadData -bor
            [Security.AccessControl.FileSystemRights]::FullControl
        if ($broadSids -contains $sid -and
            ($rule.FileSystemRights -band $readRights) -ne 0) {
            throw "The API key file grants read access to a broad Windows principal."
        }
    }
    return $resolved
}

function Initialize-AdminSecret {
    if ($null -ne $AdminApiKey -and -not [string]::IsNullOrWhiteSpace($AdminApiKeyFile)) {
        throw "Use either -AdminApiKey or -AdminApiKeyFile, never both."
    }
    if ($null -ne $AdminApiKey) {
        return $AdminApiKey
    }
    if (-not [string]::IsNullOrWhiteSpace($AdminApiKeyFile)) {
        $path = Assert-PrivateSecretFile $AdminApiKeyFile
        $plain = [IO.File]::ReadAllText($path).Trim()
        try {
            if ($plain.Length -lt 16 -or $plain.Length -gt 512 -or
                @($plain.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0) {
                throw "Admin API key must contain 16 to 512 non-control characters."
            }
            return ConvertTo-SecureString $plain -AsPlainText -Force
        }
        finally {
            $plain = $null
        }
    }
    return Read-Host "Control API read-only administrator key" -AsSecureString
}

$adminSecret = Initialize-AdminSecret
$apiKey = Get-PlainText $adminSecret
try {
    $adminHeaders = @{ "X-Pal-Admin-Key" = $apiKey }
}
finally {
    $apiKey = $null
}

$health = Invoke-RestMethod -Uri "$base/health/live" -TimeoutSec 5
try {
    $settlement = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/settlement/status" -Headers $adminHeaders -TimeoutSec 10
    $preflight = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/rollover/preflight" -Headers $adminHeaders -TimeoutSec 10
    $readiness = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/rollover/readiness" -Headers $adminHeaders -TimeoutSec 10
    $palDefenderStatus = Invoke-RestMethod -Uri "$base/api/v1/servers/local/paldefender/status" -Headers $adminHeaders -TimeoutSec 10
}
finally {
    foreach ($name in @($adminHeaders.Keys)) {
        $adminHeaders[$name] = $null
    }
    $adminHeaders.Clear()
    $adminSecret = $null
}

$controlListener = Get-NetTCPConnection -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
$palDefenderListener = Get-NetTCPConnection -LocalPort $PalDefenderPort -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
$rconListener = Get-NetTCPConnection -LocalPort $RconPort -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
$rconFirewall = Get-NetFirewallRule -DisplayName "Palworld RCON - Block all inbound TCP 25575" -ErrorAction SilentlyContinue
$restFirewall = Get-NetFirewallRule -DisplayName "Palworld REST API - Block all inbound TCP 8212" -ErrorAction SilentlyContinue
$firewallProfiles = @(Get-NetFirewallProfile)

$checks = [ordered]@{
    ControlApiHealthy = $health.status -eq "ok"
    ControlApiLoopback = $null -ne $controlListener -and $controlListener.LocalAddress -in @("127.0.0.1", "::1")
    PalDefenderConnected = [bool]$palDefenderStatus.connected
    PalDefenderLoopback = $null -ne $palDefenderListener -and $palDefenderListener.LocalAddress -in @("127.0.0.1", "::1")
    SettlementAdapterConnected = [bool]$settlement.connected
    SeasonWorldIdentityMatched = -not [string]::IsNullOrWhiteSpace($preflight.currentSeasonWorldId) -and
        $preflight.currentSeasonWorldId -eq $preflight.actualWorldId
    RolloverTimingKnown = $null -ne $preflight.currentSeasonEndsAt -and
        -not [string]::IsNullOrWhiteSpace($preflight.targetSeasonCode)
    RconFirewallBlockedInbound = $null -ne $rconFirewall -and
        [bool]$rconFirewall.Enabled -and
        $rconFirewall.Action.ToString() -eq "Block"
    OfficialRestFirewallBlockedInbound = $null -ne $restFirewall -and
        [bool]$restFirewall.Enabled -and
        $restFirewall.Action.ToString() -eq "Block"
    AllFirewallProfilesEnabled = $firewallProfiles.Count -ge 3 -and
        @($firewallProfiles | Where-Object { -not $_.Enabled }).Count -eq 0
    RolloverHasNoBlockingOrders = @($readiness.blockingOrders).Count -eq 0
    RolloverHasNoBlockingRuns = @($readiness.blockingRuns).Count -eq 0
    EconomyOpen = -not [bool]$readiness.maintenance.maintenance
}

if ($FailOnMaintenance -and -not $checks.EconomyOpen) {
    throw "The extraction economy is still in maintenance mode."
}
$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
$report = [pscustomobject]@{
    Passed = $failed.Count -eq 0
    FailedChecks = $failed
    Checks = [pscustomobject]$checks
    Runtime = [pscustomobject]@{
        ControlApiPid = $controlListener.OwningProcess
        PalDefenderPid = $palDefenderListener.OwningProcess
        RconPid = $rconListener.OwningProcess
        RconListenAddress = $rconListener.LocalAddress
        SettlementAdapter = $settlement.adapter
        SettlementOutcome = $settlement.outcome
        CanStartWorldSwitch = [bool]$preflight.canStartWorldSwitch
        RolloverTimingReason = $preflight.reason
        Maintenance = [bool]$readiness.maintenance.maintenance
    }
    CheckedAt = [DateTimeOffset]::UtcNow
}
$report
if (-not $report.Passed) {
    exit 1
}
