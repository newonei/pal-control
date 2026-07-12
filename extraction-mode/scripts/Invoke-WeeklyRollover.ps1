[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$ControlApiUrl = "http://127.0.0.1:5180",
    [string]$OfficialRestBaseUrl = "http://127.0.0.1:8212/v1/api/",
    [PSCredential]$OfficialRestCredential,
    [string]$InstallRoot = "C:\PalServerRuntime",
    [ValidateSet("Keep", "Archive", "Delete")]
    [string]$PreviousWorldPolicy = "Keep",
    [switch]$PlanOnly,
    [switch]$AllowDeletePreviousWorld,
    [string]$ArchiveRoot = "C:\PalServerRuntime\Pal\Saved\SaveGames\WeeklyArchive",
    [string]$GuardedStartScript = "",
    [int]$DrainTimeoutSeconds = 90,
    [int]$StartupTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-LoopbackUrl([string]$Value, [string]$Name) {
    $uri = [Uri]$Value
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "http" -or -not $uri.IsLoopback) {
        throw "$Name must be an absolute loopback HTTP URL."
    }
    return $uri
}

function Assert-ChildPath([string]$Candidate, [string]$Root, [string]$Name) {
    $fullCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $fullCandidate.StartsWith(
            $fullRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escaped its intended root."
    }
    return $fullCandidate
}

function Invoke-ControlApi([string]$Method, [string]$Path, [object]$Body = $null) {
    $parameters = @{
        Method = $Method
        Uri = $ControlApiUrl.TrimEnd("/") + $Path
        TimeoutSec = 15
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 8
    }
    return Invoke-RestMethod @parameters
}

function Get-PlainText([Security.SecureString]$SecureString) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Write-AtomicUtf8File([string]$Path, [string]$Content) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    $tempPath = Join-Path $directory (".{0}.{1}.tmp" -f [IO.Path]::GetFileName($fullPath), [Guid]::NewGuid().ToString("N"))
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
        $stream = [IO.FileStream]::new(
            $tempPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [IO.File]::Replace($tempPath, $fullPath, $null)
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

$null = Assert-LoopbackUrl $ControlApiUrl "ControlApiUrl"
$officialUri = Assert-LoopbackUrl $OfficialRestBaseUrl "OfficialRestBaseUrl"
$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$saveRoot = Assert-ChildPath (Join-Path $root "Pal\Saved\SaveGames\0") $root "save root"
$settingsPath = Assert-ChildPath (
    Join-Path $root "Pal\Saved\Config\WindowsServer\GameUserSettings.ini") $root "settings path"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "GameUserSettings.ini does not exist."
}
if (-not (Test-Path -LiteralPath $saveRoot -PathType Container)) {
    throw "Palworld save root does not exist."
}
if ([string]::IsNullOrWhiteSpace($GuardedStartScript)) {
    $GuardedStartScript = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "..\..\deploy\windows\start-palserver-guarded.ps1"))
}
$startScript = (Resolve-Path -LiteralPath $GuardedStartScript).Path

if ($PreviousWorldPolicy -eq "Delete" -and -not $AllowDeletePreviousWorld) {
    throw "Delete requires -AllowDeletePreviousWorld. Use Keep on production unless deletion is intentional."
}
$newWorldId = ([Guid]::NewGuid().ToString("N")).ToUpperInvariant()
if ($WhatIfPreference -and -not $PlanOnly) {
    throw "Use -PlanOnly for a non-mutating rollover preview."
}
if ($PlanOnly) {
    $settings = [IO.File]::ReadAllText($settingsPath)
    $nameMatch = [regex]::Match($settings, "(?m)^DedicatedServerName=(?<id>[A-Fa-f0-9]{32})\s*$")
    if (-not $nameMatch.Success) {
        throw "DedicatedServerName was not found or was not a 32-character world id."
    }
    $readiness = Invoke-ControlApi "GET" "/api/v1/extraction/admin/rollover/readiness"
    $preflight = Invoke-ControlApi "GET" "/api/v1/extraction/admin/rollover/preflight"
    [pscustomobject]@{
        PlanOnly = $true
        CurrentWorldId = $nameMatch.Groups["id"].Value.ToUpperInvariant()
        ProposedWorldId = $newWorldId
        PreviousWorldPolicy = $PreviousWorldPolicy
        CurrentMaintenance = $readiness.maintenance.maintenance
        BlockingOrders = @($readiness.blockingOrders).Count
        BlockingRuns = @($readiness.blockingRuns).Count
        SeasonEndsAt = $preflight.currentSeasonEndsAt
        TargetSeasonCode = $preflight.targetSeasonCode
        TimingReason = $preflight.reason
        CanEnterRollover = [bool]$preflight.canStartWorldSwitch -and
            @($readiness.blockingOrders).Count -eq 0 -and
            @($readiness.blockingRuns).Count -eq 0
    }
    return
}
$maintenanceEnabled = $false
$serverStopped = $false
$settingsChanged = $false
$previousWorldMovedTo = $null
$previousWorldId = $null
$previousWorldPath = $null
$previousSettingsContent = $null
$seasonCommitAttempted = $false
$seasonCommitted = $false
$rolloverLock = $null

try {
    $rolloverLockPath = Join-Path $PSScriptRoot ".weekly-rollover.lock"
    $rolloverLock = [IO.FileStream]::new(
        $rolloverLockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None,
        1,
        [IO.FileOptions]::WriteThrough)
}
catch {
    throw "Another weekly rollover process already owns the exclusive lock."
}

try {
    $health = Invoke-RestMethod -Uri ($ControlApiUrl.TrimEnd("/") + "/health/live") -TimeoutSec 5
    if ($health.status -ne "ok") {
        throw "Control API is not healthy."
    }

    $preflight = Invoke-ControlApi "GET" "/api/v1/extraction/admin/rollover/preflight"
    if (-not [bool]$preflight.canStartWorldSwitch) {
        throw ("Weekly rollover preflight rejected the world switch before shutdown: {0}" -f
            $preflight.reason)
    }
    if ($null -eq $OfficialRestCredential) {
        $OfficialRestCredential = Get-Credential `
            -UserName "admin" `
            -Message "Palworld official REST credentials"
    }

    $gate = Invoke-ControlApi "POST" "/api/v1/extraction/admin/rollover/maintenance" @{
        maintenance = $true
        reason = "Weekly world rollover"
    }
    $maintenanceEnabled = [bool]$gate.maintenance

    $drainDeadline = (Get-Date).AddSeconds($DrainTimeoutSeconds)
    do {
        $readiness = Invoke-ControlApi "GET" "/api/v1/extraction/admin/rollover/readiness"
        if ($readiness.readyForWorldSwitch) {
            break
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $drainDeadline)
    if (-not $readiness.readyForWorldSwitch) {
        throw "Economic writes did not drain before the rollover timeout."
    }

    $password = Get-PlainText $OfficialRestCredential.Password
    try {
        $pair = $OfficialRestCredential.UserName + ":" + $password
        $authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
    }
    finally {
        $password = $null
        $pair = $null
    }
    $officialHeaders = @{ Authorization = $authorization }
    Invoke-RestMethod `
        -Method Post `
        -Uri ([Uri]::new($officialUri, "save")) `
        -Headers $officialHeaders `
        -ContentType "application/json" `
        -Body "{}" `
        -TimeoutSec 15 | Out-Null
    Invoke-RestMethod `
        -Method Post `
        -Uri ([Uri]::new($officialUri, "shutdown")) `
        -Headers $officialHeaders `
        -ContentType "application/json" `
        -Body (@{ waittime = 5; message = "Weekly world rollover in progress." } | ConvertTo-Json) `
        -TimeoutSec 15 | Out-Null
    $authorization = $null

    $shutdownDeadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $serverProcesses = Get-Process PalServer, PalServer-Win64-Shipping-Cmd -ErrorAction SilentlyContinue
    } while ($serverProcesses -and (Get-Date) -lt $shutdownDeadline)
    if ($serverProcesses) {
        throw "PalServer did not finish its graceful shutdown."
    }
    $serverStopped = $true

    $settings = [IO.File]::ReadAllText($settingsPath)
    $previousSettingsContent = $settings
    $nameMatch = [regex]::Match($settings, "(?m)^DedicatedServerName=(?<id>[A-Fa-f0-9]{32})\s*$")
    if (-not $nameMatch.Success) {
        throw "DedicatedServerName was not found or was not a 32-character world id."
    }
    $previousWorldId = $nameMatch.Groups["id"].Value.ToUpperInvariant()
    $previousWorldPath = Assert-ChildPath (Join-Path $saveRoot $previousWorldId) $saveRoot "previous world"
    $newWorldPath = Assert-ChildPath (Join-Path $saveRoot $newWorldId) $saveRoot "new world"
    if (Test-Path -LiteralPath $newWorldPath) {
        throw "Generated new world id already exists."
    }

    if ($PreviousWorldPolicy -eq "Archive" -and (Test-Path -LiteralPath $previousWorldPath)) {
        $archive = [IO.Path]::GetFullPath($ArchiveRoot)
        New-Item -ItemType Directory -Path $archive -Force | Out-Null
        $archiveTarget = Assert-ChildPath (
            (Join-Path $archive ("{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), $previousWorldId))) `
            $archive `
            "archive target"
        Move-Item -LiteralPath $previousWorldPath -Destination $archiveTarget
        $previousWorldMovedTo = $archiveTarget
    }
    elseif ($PreviousWorldPolicy -eq "Delete" -and (Test-Path -LiteralPath $previousWorldPath)) {
        if ($PSCmdlet.ShouldProcess($previousWorldPath, "delete previous Palworld development world")) {
            Remove-Item -LiteralPath $previousWorldPath -Recurse -Force
        }
        else {
            throw "Deletion was not confirmed."
        }
    }

    $updatedSettings = [regex]::Replace(
        $settings,
        "(?m)^DedicatedServerName=[A-Fa-f0-9]{32}\s*$",
        "DedicatedServerName=$newWorldId",
        1)
    # Treat the write as having changed state before it starts so a partial write
    # also enters the guarded rollback path.
    $settingsChanged = $true
    Write-AtomicUtf8File $settingsPath $updatedSettings

    & $startScript -InstallRoot $root | Out-Null
    $startupDeadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Seconds 1
        $gameReady = [bool](Get-NetUDPEndpoint -LocalPort 8211 -ErrorAction SilentlyContinue)
        $palDefenderReady = $false
        $rconReady = $false
        try {
            $palDefenderProbe = Invoke-ControlApi "GET" "/api/v1/servers/local/paldefender/status"
            $rconProbe = Invoke-ControlApi "GET" "/api/v1/extraction/admin/rcon/status"
            $palDefenderReady = [bool]$palDefenderProbe.connected
            $rconReady = [bool]$rconProbe.connected
        }
        catch {
            $palDefenderReady = $false
            $rconReady = $false
        }
    } while ((-not $gameReady -or -not $palDefenderReady -or -not $rconReady) -and
             (Get-Date) -lt $startupDeadline)
    if (-not $gameReady -or -not $palDefenderReady -or -not $rconReady) {
        throw "The new world did not expose all required local services before timeout."
    }

    # A timeout after this point has an ambiguous commit result. Never switch back
    # to the old world automatically once the commit request has been attempted.
    $seasonCommitAttempted = $true
    Invoke-ControlApi "POST" "/api/v1/extraction/admin/rollover/commit" @{
        worldId = $newWorldId
    } | Out-Null
    $seasonCommitted = $true
    Invoke-ControlApi "POST" "/api/v1/extraction/admin/rollover/maintenance" @{
        maintenance = $false
        reason = "Weekly world rollover completed"
    } | Out-Null
    $maintenanceEnabled = $false

    [pscustomobject]@{
        Completed = $true
        PreviousWorldId = $previousWorldId
        NewWorldId = $newWorldId
        PreviousWorldPolicy = $PreviousWorldPolicy
        GameReady = $gameReady
        PalDefenderReady = $palDefenderReady
        RconReady = $rconReady
    }
}
catch {
    $rolloverFailure = $_
    if ($serverStopped -and
        $PreviousWorldPolicy -ne "Delete" -and
        -not $seasonCommitAttempted) {
        try {
            if ([string]::IsNullOrWhiteSpace($previousWorldId)) {
                throw "The previous world id is unknown; automatic recovery is unsafe."
            }

            # A partially started new world must be stopped before restoring the
            # old configuration. Never launch a second PalServer process.
            $runningProcesses = Get-Process PalServer, PalServer-Win64-Shipping-Cmd `
                -ErrorAction SilentlyContinue
            if ($runningProcesses) {
                $rollbackPassword = Get-PlainText $OfficialRestCredential.Password
                try {
                    $rollbackPair = $OfficialRestCredential.UserName + ":" + $rollbackPassword
                    $rollbackAuthorization = "Basic " + [Convert]::ToBase64String(
                        [Text.Encoding]::ASCII.GetBytes($rollbackPair))
                }
                finally {
                    $rollbackPassword = $null
                    $rollbackPair = $null
                }
                $rollbackHeaders = @{ Authorization = $rollbackAuthorization }
                Invoke-RestMethod `
                    -Method Post `
                    -Uri ([Uri]::new($officialUri, "save")) `
                    -Headers $rollbackHeaders `
                    -ContentType "application/json" `
                    -Body "{}" `
                    -TimeoutSec 15 | Out-Null
                Invoke-RestMethod `
                    -Method Post `
                    -Uri ([Uri]::new($officialUri, "shutdown")) `
                    -Headers $rollbackHeaders `
                    -ContentType "application/json" `
                    -Body (@{
                        waittime = 5
                        message = "Weekly rollover failed; restoring the previous world."
                    } | ConvertTo-Json) `
                    -TimeoutSec 15 | Out-Null
                $rollbackAuthorization = $null
                $rollbackHeaders = $null

                $rollbackShutdownDeadline = (Get-Date).AddSeconds(45)
                do {
                    Start-Sleep -Milliseconds 500
                    $runningProcesses = Get-Process PalServer, PalServer-Win64-Shipping-Cmd `
                        -ErrorAction SilentlyContinue
                } while ($runningProcesses -and (Get-Date) -lt $rollbackShutdownDeadline)
                if ($runningProcesses) {
                    throw "The failed new-world process did not finish its graceful shutdown."
                }
            }

            if ($settingsChanged) {
                if ([string]::IsNullOrWhiteSpace($previousSettingsContent)) {
                    throw "The original GameUserSettings.ini content is unavailable."
                }
                Write-AtomicUtf8File $settingsPath $previousSettingsContent
            }
            if ($null -ne $previousWorldMovedTo -and (Test-Path -LiteralPath $previousWorldMovedTo)) {
                Move-Item -LiteralPath $previousWorldMovedTo -Destination $previousWorldPath
            }

            $rollbackSettings = [IO.File]::ReadAllText($settingsPath)
            $rollbackNameMatch = [regex]::Match(
                $rollbackSettings,
                "(?m)^DedicatedServerName=(?<id>[A-Fa-f0-9]{32})\s*$")
            if (-not $rollbackNameMatch.Success -or
                $rollbackNameMatch.Groups["id"].Value -ne $previousWorldId) {
                throw "The previous DedicatedServerName could not be verified after restoration."
            }

            & $startScript -InstallRoot $root | Out-Null
            $rollbackStartupDeadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
            do {
                Start-Sleep -Seconds 1
                $rollbackGameReady = [bool](
                    Get-NetUDPEndpoint -LocalPort 8211 -ErrorAction SilentlyContinue)
                $rollbackPalDefenderReady = $false
                $rollbackRconReady = $false
                try {
                    $rollbackPalDefenderProbe = Invoke-ControlApi `
                        "GET" `
                        "/api/v1/servers/local/paldefender/status"
                    $rollbackRconProbe = Invoke-ControlApi `
                        "GET" `
                        "/api/v1/extraction/admin/rcon/status"
                    $rollbackPalDefenderReady = [bool]$rollbackPalDefenderProbe.connected
                    $rollbackRconReady = [bool]$rollbackRconProbe.connected
                }
                catch {
                    $rollbackPalDefenderReady = $false
                    $rollbackRconReady = $false
                }
            } while ((-not $rollbackGameReady -or
                       -not $rollbackPalDefenderReady -or
                       -not $rollbackRconReady) -and
                     (Get-Date) -lt $rollbackStartupDeadline)
            if (-not $rollbackGameReady -or
                -not $rollbackPalDefenderReady -or
                -not $rollbackRconReady) {
                throw "The previous world did not expose all required services after recovery."
            }
            Write-Warning (
                "Rollover failed before season commit. Previous world {0} was restored and " +
                "8211/17993/25575 probes are ready; economy maintenance remains enabled " +
                "until an operator verifies the world and reopens writes." -f $previousWorldId)
        }
        catch {
            Write-Warning (
                "Automatic previous-world recovery failed: {0}. Keep economy maintenance " +
                "enabled; verify GameUserSettings.ini, ensure no PalServer process is running, " +
                "then start the guarded old world manually." -f $_.Exception.Message)
        }
    }
    elseif ($seasonCommitAttempted) {
        $commitState = if ($seasonCommitted) { "succeeded" } else { "is uncertain" }
        Write-Warning (
            "Season commit $commitState. Automatic old-world recovery is intentionally " +
            "disabled to prevent a world/economy split. Keep maintenance enabled and " +
            "reconcile the committed world id before taking any action.")
    }
    if ($maintenanceEnabled) {
        Write-Warning "Economy maintenance remains enabled. Reconcile the failure before reopening writes."
    }
    throw $rolloverFailure
}
finally {
    if ($null -ne $rolloverLock) {
        $rolloverLock.Dispose()
    }
}
