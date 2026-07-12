[CmdletBinding()]
param(
    [string]$ControlApiUrl = "http://127.0.0.1:5180",
    [int]$PalDefenderPort = 17993,
    [int]$RconPort = 25575,
    [switch]$FailOnMaintenance
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = [Uri]$ControlApiUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "http" -or -not $uri.IsLoopback) {
    throw "ControlApiUrl must be an absolute loopback HTTP URL."
}
$base = $ControlApiUrl.TrimEnd("/")

$health = Invoke-RestMethod -Uri "$base/health/live" -TimeoutSec 5
$rcon = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/rcon/status" -TimeoutSec 10
$preflight = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/rollover/preflight" -TimeoutSec 10
$readiness = Invoke-RestMethod -Uri "$base/api/v1/extraction/admin/rollover/readiness" -TimeoutSec 10
$palDefenderStatus = Invoke-RestMethod -Uri "$base/api/v1/servers/local/paldefender/status" -TimeoutSec 10

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
    RconAuthenticated = [bool]$rcon.connected
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
