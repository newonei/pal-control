[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Port
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceWorld = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
$targetWorld = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
$sourceSeason = "11111111-1111-1111-1111-111111111111"
$targetSeason = "22222222-2222-2222-2222-222222222222"
$operationId = "33333333-3333-3333-3333-333333333333"
$gameBackupId = "44444444444444444444444444444444"
$gameCreateCommandId = "55555555-5555-5555-5555-555555555555"
$gameVerifyCommandId = "66666666-6666-6666-6666-666666666666"
$economyBackupId = "20260715T000000000Z-77777777777777777777777777777777"
$expiryJobId = "88888888-8888-8888-8888-888888888888"
$gameHash = ("a" * 64)
$economyHash = ("b" * 64)
$steps = @(
    "Preflight",
    "Drain",
    "GameBackup",
    "EconomyBackup",
    "Stop",
    "NewWorld",
    "Probe",
    "Commit",
    "Reopen",
    "Completed"
)

function New-State([string]$Scenario) {
    return [ordered]@{
        scenario = $Scenario
        operation = $null
        completedSteps = [Collections.ArrayList]::new()
        maintenance = $false
        actualWorldId = $sourceWorld
        currentSeasonId = $sourceSeason
        currentSeasonWorldId = $sourceWorld
        expiryPrepared = $false
        expiryCompleted = $false
        seasonCommitted = $false
        gameBackupKey = $null
        gameBackupCalls = 0
        gameVerifyKey = $null
        gameVerifyCalls = 0
        economySnapshotKey = $null
        economySnapshotCalls = 0
        economyStageCalls = 0
        stepCalls = [ordered]@{}
        dropped = [ordered]@{}
        stopped = $false
    }
}

function Get-StepKey([string]$Step) {
    return "rollover-$($operationId.Replace('-', ''))-$($Step.ToLowerInvariant())"
}

function Get-Wrapper {
    $required = if ($state.operation.currentStep -eq "Completed") {
        $null
    }
    else {
        Get-StepKey ([string]$state.operation.currentStep)
    }
    return [ordered]@{
        operation = $state.operation
        requiredStepKey = $required
    }
}

function Get-Backup {
    $createdAt = if ($state.scenario -eq "stale-game") {
        [DateTimeOffset]::UtcNow.AddHours(-1).ToString("O")
    }
    else {
        [DateTimeOffset]::UtcNow.ToString("O")
    }
    return [ordered]@{
        backupId = $gameBackupId
        kind = "managed"
        label = "weekly-test"
        worldGuid = if ($state.scenario -eq "wrong-game-world") {
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"
        }
        else {
            $sourceWorld
        }
        gameVersion = "test"
        createdAt = $createdAt
        fileCount = 2
        totalBytes = 1024
        integrity = "verified"
        consistency = "stable"
        actor = "test"
        reason = "test"
        manifestSha256 = $gameHash
    }
}

function Get-Manifest {
    return [ordered]@{
        schemaVersion = 2
        backupId = $economyBackupId
        serverId = "local"
        worldId = $sourceWorld
        createdAt = [DateTimeOffset]::UtcNow.ToString("O")
        lastEconomySequence = 10
        sqliteUserVersion = 1
        walLogFrames = 0
        walCheckpointedFrames = 0
        pendingTransactions = @(
            [ordered]@{
                kind = "rollover"
                id = $operationId
                state = "economy_backup"
                updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
            }
        )
        files = @()
        contentHash = $economyHash
        rpoMinutes = 15
        targetRtoMinutes = 60
        idempotencyKeyHash = ("c" * 64)
    }
}

function Get-Stage {
    $valid = $state.scenario -ne "bad-stage"
    return [ordered]@{
        backupId = $economyBackupId
        stagingDirectory = "C:\fake\stage\$economyBackupId"
        verifiedAt = [DateTimeOffset]::UtcNow.ToString("O")
        hashesValid = $valid
        sqliteIntegrityValid = $true
        economyReplayValid = $true
        worldIdValid = $true
        expectedWorldId = $sourceWorld
        manifestWorldId = $sourceWorld
        pendingTransactionCount = 1
        contentHash = $economyHash
        economyForcedClosed = $true
        activeSeasonWorldValid = $true
        ledgerProjectionValid = $true
        blockingOrderCount = 0
        sqliteSchemaValid = $true
        foreignKeysValid = $true
        commandReplayValid = $true
        commandIdempotencyValid = $true
        pendingCommandCount = 0
        pendingStateMatchesManifest = $true
    }
}

function Read-Body([Net.HttpListenerRequest]$Request) {
    if (-not $Request.HasEntityBody) {
        return $null
    }
    $reader = [IO.StreamReader]::new($Request.InputStream, $Request.ContentEncoding)
    try {
        $raw = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }
    return $raw | ConvertFrom-Json
}

function Send-Json(
    [Net.HttpListenerContext]$Context,
    [int]$StatusCode,
    [object]$Body) {
    $json = $Body | ConvertTo-Json -Depth 24 -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $Context.Response.StatusCode = $StatusCode
    $Context.Response.ContentType = "application/json; charset=utf-8"
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.OutputStream.Close()
}

function Send-Error(
    [Net.HttpListenerContext]$Context,
    [int]$StatusCode,
    [string]$Code,
    [string]$Message) {
    Send-Json $Context $StatusCode ([ordered]@{ code = $Code; message = $Message })
}

function Drop-Response([Net.HttpListenerContext]$Context) {
    $Context.Response.StatusCode = 200
    $Context.Response.ContentType = "application/json"
    $Context.Response.ContentLength64 = 32
    $Context.Response.OutputStream.Close()
}

function Should-Drop([string]$Key) {
    if ($state.dropped.Contains($Key)) {
        return $false
    }
    if ($state.scenario -eq "drop-$Key") {
        $state.dropped[$Key] = $true
        return $true
    }
    return $false
}

$state = New-State "clean"
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

try {
    while (-not $state.stopped) {
        $context = $listener.GetContext()
        try {
            $request = $context.Request
            $method = $request.HttpMethod.ToUpperInvariant()
            $path = $request.Url.AbsolutePath
            $body = Read-Body $request

            if ($method -eq "GET" -and $path -eq "/health/live") {
                Send-Json $context 200 ([ordered]@{ status = "ok" })
                continue
            }
            if ($path -eq "/__test/state" -and $method -eq "GET") {
                Send-Json $context 200 $state
                continue
            }
            if ($path -eq "/__test/reset" -and $method -eq "POST") {
                $scenario = if ($null -ne $body -and $null -ne $body.scenario) {
                    [string]$body.scenario
                }
                else {
                    "clean"
                }
                $state = New-State $scenario
                Send-Json $context 200 $state
                continue
            }
            if ($path -eq "/__test/stop" -and $method -eq "POST") {
                $state.stopped = $true
                Send-Json $context 200 ([ordered]@{ stopped = $true })
                continue
            }

            if ($path -eq "/api/v1/extraction/admin/rollover/preflight" -and $method -eq "GET") {
                $canStart = $state.currentSeasonId -eq $sourceSeason -and
                    $state.currentSeasonWorldId -eq $sourceWorld -and
                    $state.actualWorldId -eq $sourceWorld -and
                    $state.scenario -ne "timing-blocked"
                Send-Json $context 200 ([ordered]@{
                    checkedAt = [DateTimeOffset]::UtcNow.ToString("O")
                    currentSeasonId = $state.currentSeasonId
                    currentSeasonCode = "2026-W28"
                    currentSeasonWorldId = $state.currentSeasonWorldId
                    currentSeasonEndsAt = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToString("O")
                    actualWorldId = $state.actualWorldId
                    targetSeasonCode = "2026-W29"
                    targetSeasonStartsAt = [DateTimeOffset]::UtcNow.ToString("O")
                    targetSeasonEndsAt = [DateTimeOffset]::UtcNow.AddDays(7).ToString("O")
                    canStartWorldSwitch = $canStart
                    reason = if ($canStart) { "ready" } else { "world/version/timing mismatch" }
                })
                continue
            }
            if ($path -eq "/api/v1/extraction/admin/rollover/readiness" -and $method -eq "GET") {
                [object[]]$blockingOrders = @()
                if ($state.scenario -eq "blocking") {
                    $blockingOrders = @(
                        [pscustomobject]@{
                            orderId = [Guid]::NewGuid()
                            state = "uncertain"
                        }
                    )
                }
                $ready = $state.maintenance -and @($blockingOrders).Count -eq 0
                Send-Json $context 200 ([ordered]@{
                    maintenance = [ordered]@{ maintenance = $state.maintenance }
                    readyForWorldSwitch = $ready
                    activeOperations = 0
                    blockingOrders = $blockingOrders
                    blockingRuns = @()
                })
                continue
            }
            if ($path -eq "/api/v1/extraction/admin/rollover/maintenance" -and $method -eq "POST") {
                $state.maintenance = [bool]$body.maintenance
                Send-Json $context 200 ([ordered]@{ maintenance = $state.maintenance })
                continue
            }
            if ($path -eq "/api/v1/extraction/capabilities" -and $method -eq "GET") {
                Send-Json $context 200 ([ordered]@{
                    maintenance = $state.maintenance
                    writes = [ordered]@{
                        purchase = [ordered]@{ enabled = -not $state.maintenance; blockers = @() }
                        resourceExchange = [ordered]@{ enabled = -not $state.maintenance; blockers = @() }
                    }
                })
                continue
            }

            if ($path -eq "/api/v1/admin/weekly-rollover/operations/active" -and $method -eq "GET") {
                if ($null -eq $state.operation -or $state.operation.currentStep -eq "Completed") {
                    Send-Error $context 404 "ROLLOVER_NOT_ACTIVE" "no active operation"
                }
                else {
                    Send-Json $context 200 (Get-Wrapper)
                }
                continue
            }
            if ($path -eq "/api/v1/admin/weekly-rollover/operations" -and $method -eq "POST") {
                if ($null -eq $state.operation) {
                    $state.operation = [ordered]@{
                        operationId = $operationId
                        serverId = [string]$body.serverId
                        fromSeasonId = [string]$body.fromSeasonId
                        fromWorldId = [string]$body.fromWorldId
                        targetWorldId = [string]$body.targetWorldId
                        rulesVersion = [string]$body.rulesVersion
                        currentStep = "Preflight"
                        revision = 0
                        newSeasonCommitted = $false
                        createdAt = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString("O")
                        updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
                        completedSteps = $state.completedSteps
                    }
                }
                Send-Json $context 200 (Get-Wrapper)
                continue
            }
            if ($path -match '^/api/v1/admin/weekly-rollover/operations/[0-9a-fA-F-]+$' -and
                $method -eq "GET") {
                if ($null -eq $state.operation) {
                    Send-Error $context 404 "ROLLOVER_NOT_FOUND" "missing"
                }
                else {
                    Send-Json $context 200 (Get-Wrapper)
                }
                continue
            }
            if ($path -match '^/api/v1/admin/weekly-rollover/operations/[0-9a-fA-F-]+/steps/(?<step>[^/]+)$' -and
                $method -eq "POST") {
                $step = [string]$Matches.step
                if (-not $state.stepCalls.Contains($step)) {
                    $state.stepCalls[$step] = 0
                }
                $state.stepCalls[$step] = [int]$state.stepCalls[$step] + 1
                $existing = @($state.completedSteps | Where-Object { $_.step -eq $step })
                if ($existing.Count -eq 1) {
                    if ($existing[0].stepKey -ne [string]$body.stepKey -or
                        $existing[0].evidenceHash -ne [string]$body.evidence.evidenceHash) {
                        Send-Error $context 409 "EVIDENCE_CONFLICT" "conflicting replay"
                    }
                    else {
                        Send-Json $context 200 ([ordered]@{
                            applied = $false
                            idempotentReplay = $true
                            stepKey = [string]$body.stepKey
                            operation = Get-Wrapper
                        })
                    }
                    continue
                }
                if ($null -eq $state.operation -or $state.operation.currentStep -ne $step -or
                    [string]$body.stepKey -ne (Get-StepKey $step)) {
                    Send-Error $context 409 "STEP_MISMATCH" "wrong step or key"
                    continue
                }
                if ($step -eq "Drain" -and -not $state.maintenance) {
                    Send-Error $context 409 "MAINTENANCE_REQUIRED" "maintenance required"
                    continue
                }
                if ($step -eq "GameBackup" -and
                    ([string]$body.evidence.evidenceReference -ne $gameBackupId -or
                     [string]$body.evidence.evidenceHash -ne $gameHash)) {
                    Send-Error $context 409 "BACKUP_MISMATCH" "game backup mismatch"
                    continue
                }
                if ($step -eq "EconomyBackup" -and
                    ([string]$body.evidence.evidenceReference -ne $economyBackupId -or
                     [string]$body.evidence.evidenceHash -ne $economyHash)) {
                    Send-Error $context 409 "SNAPSHOT_MISMATCH" "economy snapshot mismatch"
                    continue
                }
                if (($step -eq "Probe" -or $step -eq "Commit") -and
                    [string]$body.evidence.observedWorldId -ne $targetWorld) {
                    Send-Error $context 409 "WORLD_MISMATCH" "target world mismatch"
                    continue
                }
                if ($step -eq "Commit" -and
                    (-not $state.expiryCompleted -or -not $state.seasonCommitted)) {
                    Send-Error $context 409 "COMMIT_PREREQUISITE" "expiry/season commit missing"
                    continue
                }
                [void]$state.completedSteps.Add([ordered]@{
                    step = $step
                    stepKey = [string]$body.stepKey
                    evidenceType = [string]$body.evidence.evidenceType
                    evidenceReference = [string]$body.evidence.evidenceReference
                    evidenceHash = [string]$body.evidence.evidenceHash
                    actor = "fake-admin"
                    completedAt = [DateTimeOffset]::UtcNow.ToString("O")
                })
                $index = [Array]::IndexOf($steps, $step)
                $state.operation.currentStep = $steps[$index + 1]
                $state.operation.revision = [int]$state.operation.revision + 1
                $state.operation.updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
                if ($step -eq "NewWorld") {
                    $state.actualWorldId = $targetWorld
                }
                if ($step -eq "Commit") {
                    $state.operation.newSeasonCommitted = $true
                }
                if ($step -eq "Reopen") {
                    $state.maintenance = $false
                }
                if (Should-Drop ("step-{0}" -f $step)) {
                    Drop-Response $context
                }
                else {
                    Send-Json $context 200 ([ordered]@{
                        applied = $true
                        idempotentReplay = $false
                        stepKey = [string]$body.stepKey
                        operation = Get-Wrapper
                    })
                }
                continue
            }

            if ($path -eq "/api/v1/servers/local/backups" -and $method -eq "POST") {
                $state.gameBackupCalls = [int]$state.gameBackupCalls + 1
                $key = [string]$request.Headers["Idempotency-Key"]
                if ($null -ne $state.gameBackupKey -and $state.gameBackupKey -ne $key) {
                    Send-Error $context 409 "IDEMPOTENCY_CONFLICT" "backup key changed"
                    continue
                }
                $state.gameBackupKey = $key
                $response = [ordered]@{
                    commandId = $gameCreateCommandId
                    type = "create-backup"
                    state = "succeeded"
                    stage = "completed"
                    createdAt = [DateTimeOffset]::UtcNow.ToString("O")
                    completedAt = [DateTimeOffset]::UtcNow.ToString("O")
                    statusUrl = "/api/v1/save-commands/$gameCreateCommandId"
                    backupId = $gameBackupId
                    result = [ordered]@{ backup = Get-Backup }
                    error = $null
                }
                if (Should-Drop "game-backup") {
                    Drop-Response $context
                }
                else {
                    Send-Json $context 200 $response
                }
                continue
            }
            if ($path -eq "/api/v1/servers/local/backups/$gameBackupId" -and $method -eq "GET") {
                Send-Json $context 200 (Get-Backup)
                continue
            }
            if ($path -eq "/api/v1/servers/local/backups/$gameBackupId/verify" -and $method -eq "POST") {
                $state.gameVerifyCalls = [int]$state.gameVerifyCalls + 1
                $key = [string]$request.Headers["Idempotency-Key"]
                if ($null -ne $state.gameVerifyKey -and $state.gameVerifyKey -ne $key) {
                    Send-Error $context 409 "IDEMPOTENCY_CONFLICT" "verify key changed"
                    continue
                }
                $state.gameVerifyKey = $key
                Send-Json $context 200 ([ordered]@{
                    commandId = $gameVerifyCommandId
                    type = "verify-backup"
                    state = "succeeded"
                    stage = "completed"
                    createdAt = [DateTimeOffset]::UtcNow.ToString("O")
                    completedAt = [DateTimeOffset]::UtcNow.ToString("O")
                    statusUrl = "/api/v1/save-commands/$gameVerifyCommandId"
                    backupId = $gameBackupId
                    result = [ordered]@{ backup = Get-Backup }
                    error = $null
                })
                continue
            }
            if ($path -eq "/api/v1/save-commands/$gameCreateCommandId" -and $method -eq "GET") {
                Send-Json $context 200 ([ordered]@{
                    commandId = $gameCreateCommandId
                    state = "succeeded"
                    backupId = $gameBackupId
                    result = [ordered]@{ backup = Get-Backup }
                })
                continue
            }
            if ($path -eq "/api/v1/save-commands/$gameVerifyCommandId" -and $method -eq "GET") {
                Send-Json $context 200 ([ordered]@{
                    commandId = $gameVerifyCommandId
                    state = "succeeded"
                    backupId = $gameBackupId
                    result = [ordered]@{ backup = Get-Backup }
                })
                continue
            }

            if ($path -eq "/api/v1/admin/economy-continuity/snapshots" -and $method -eq "POST") {
                $state.economySnapshotCalls = [int]$state.economySnapshotCalls + 1
                $key = [string]$body.idempotencyKey
                if ($null -ne $state.economySnapshotKey -and
                    $state.economySnapshotKey -ne $key) {
                    Send-Error $context 409 "IDEMPOTENCY_CONFLICT" "snapshot key changed"
                    continue
                }
                $state.economySnapshotKey = $key
                if (Should-Drop "economy-snapshot") {
                    Drop-Response $context
                }
                else {
                    Send-Json $context 200 (Get-Manifest)
                }
                continue
            }
            if ($path -eq "/api/v1/admin/economy-continuity/snapshots/local/$economyBackupId/verify" -and
                $method -eq "GET") {
                Send-Json $context 200 (Get-Manifest)
                continue
            }
            if ($path -eq "/api/v1/admin/economy-continuity/snapshots/local/$economyBackupId/stage" -and
                $method -eq "POST") {
                $state.economyStageCalls = [int]$state.economyStageCalls + 1
                if (Should-Drop "economy-stage") {
                    Drop-Response $context
                }
                else {
                    Send-Json $context 200 (Get-Stage)
                }
                continue
            }
            if ($path -eq "/api/v1/admin/economy-continuity/snapshots/local/$economyBackupId/post-snapshot" -and
                $method -eq "GET") {
                [object[]]$items = @()
                if ($state.scenario -eq "post-transaction") {
                    $items = @(
                        [pscustomobject]@{
                            kind = "economy_event"
                            id = "after"
                            state = "new"
                        }
                    )
                }
                Send-Json $context 200 ([ordered]@{ items = $items })
                continue
            }

            if ($path -eq "/api/v1/admin/season-settlement-jobs/voucher-expiry" -and
                $method -eq "POST") {
                if ([string]$body.rulesVersion -ne [string]$state.operation.rulesVersion) {
                    Send-Error $context 409 "RULES_VERSION_DRIFT" "version mismatch"
                    continue
                }
                $state.expiryPrepared = $true
                Send-Json $context 200 ([ordered]@{
                    jobId = $expiryJobId
                    state = if ($state.expiryCompleted) { 2 } else { 0 }
                    rulesVersion = [string]$state.operation.rulesVersion
                })
                continue
            }
            if ($path -eq "/api/v1/admin/season-settlement-jobs/$expiryJobId" -and
                $method -eq "GET") {
                Send-Json $context 200 ([ordered]@{
                    jobId = $expiryJobId
                    state = if ($state.expiryCompleted) { 2 } else { 0 }
                    rulesVersion = [string]$state.operation.rulesVersion
                })
                continue
            }
            if ($path -eq "/api/v1/admin/season-settlement-jobs/$expiryJobId/run" -and
                $method -eq "POST") {
                $state.expiryCompleted = $true
                Send-Json $context 200 ([ordered]@{
                    jobId = $expiryJobId
                    state = 2
                    rulesVersion = [string]$state.operation.rulesVersion
                })
                continue
            }
            if ($path -eq "/api/v1/extraction/admin/rollover/commit" -and $method -eq "POST") {
                if ($state.operation.currentStep -ne "Commit" -or -not $state.expiryCompleted) {
                    Send-Error $context 409 "COMMIT_BLOCKED" "wrong step"
                    continue
                }
                $state.seasonCommitted = $true
                $state.currentSeasonId = $targetSeason
                $state.currentSeasonWorldId = $targetWorld
                Send-Json $context 200 ([ordered]@{
                    seasonId = $targetSeason
                    worldId = $targetWorld
                    revision = 1
                })
                continue
            }

            Send-Error $context 404 "NOT_FOUND" "$method $path"
        }
        catch {
            try {
                Send-Error $context 500 "FAKE_API_ERROR" $_.Exception.Message
            }
            catch {
            }
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
