[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$clientPath = Join-Path $repositoryRoot "extraction-mode\scripts\Invoke-WeeklyRollover.ps1"
$fakePath = Join-Path $PSScriptRoot "Fake-RolloverApi.ps1"
$targetWorld = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
$rulesVersion = "2026-W29-v1"
$knownApiKey = "test-admin-key-that-must-never-appear"
$knownTotp = "123456"
$operationId = "33333333-3333-3333-3333-333333333333"
$failures = [Collections.Generic.List[string]]::new()
$temporaryDirectories = [Collections.Generic.List[string]]::new()
$global:PalControlRolloverTestActionCounts = @{}
$previousTestHookEnvironment = $env:PAL_CONTROL_ROLLOVER_TEST_HOOKS
$env:PAL_CONTROL_ROLLOVER_TEST_HOOKS = "1"

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        $failures.Add($Message)
    }
}

function New-TestDirectory([string]$Name) {
    $path = Join-Path $env:TEMP (
        "pal-control-rollover-client-{0}-{1}" -f $Name, [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    $temporaryDirectories.Add($path)
    return $path
}

function New-TestSecureString([string]$Value) {
    $secure = [Security.SecureString]::new()
    foreach ($character in $Value.ToCharArray()) {
        $secure.AppendChar($character)
    }
    $secure.MakeReadOnly()
    return $secure
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-FakeApi([string]$BaseUrl, [Management.Automation.Job]$Job) {
    $deadline = (Get-Date).AddSeconds(10)
    do {
        try {
            if ((Invoke-RestMethod "$BaseUrl/health/live" -TimeoutSec 1).status -eq "ok") {
                return
            }
        }
        catch {
        }
        if ($Job.State -eq "Failed") {
            Receive-Job $Job
            throw "Fake rollover API failed during startup."
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Fake rollover API did not start."
}

function Reset-FakeApi([string]$Scenario = "clean") {
    return Invoke-RestMethod `
        -Method POST `
        -Uri "$script:BaseUrl/__test/reset" `
        -ContentType "application/json" `
        -Body (@{ scenario = $Scenario } | ConvertTo-Json -Compress)
}

function Get-FakeState {
    return Invoke-RestMethod -Uri "$script:BaseUrl/__test/state"
}

function Invoke-Client {
    param(
        [Parameter(Mandatory = $true)][string]$StateDirectory,
        [string]$FaultAfterActionStep = "",
        [string]$FaultAfterSubmitStep = "",
        [string]$SuppliedRulesVersion = $rulesVersion,
        [string]$ResumeOperationId = "",
        [switch]$Plan
    )
    $apiKey = New-TestSecureString $knownApiKey
    $totpProvider = {
        New-TestSecureString $knownTotp
    }
    $adapter = {
        param($Action, $Context)
        if (-not $global:PalControlRolloverTestActionCounts.ContainsKey($Action)) {
            $global:PalControlRolloverTestActionCounts[$Action] = 0
        }
        $global:PalControlRolloverTestActionCounts[$Action] =
            [int]$global:PalControlRolloverTestActionCounts[$Action] + 1
        [pscustomobject]@{
            success = $true
            action = $Action
            actualWorldId = $Context.operation.targetWorldId
        }
    }
    $parameters = @{
        ControlApiUrl = $script:BaseUrl
        ServerId = "local"
        AdminApiKey = $apiKey
        AdminTotpProvider = $totpProvider
        StateDirectory = $StateDirectory
        ApiTimeoutSeconds = 2
        PollIntervalMilliseconds = 10
        DrainTimeoutSeconds = 5
        CommandTimeoutSeconds = 10
        StartupTimeoutSeconds = 10
        ExternalActionAdapter = $adapter
        EnableTestHooks = $true
        Confirm = $false
    }
    if (-not $Plan) {
        $parameters.Execute = $true
        $parameters.TargetWorldId = $targetWorld
        $parameters.RulesVersion = $SuppliedRulesVersion
        if (-not [string]::IsNullOrWhiteSpace($FaultAfterActionStep)) {
            $parameters.FaultAfterActionStep = $FaultAfterActionStep
        }
        if (-not [string]::IsNullOrWhiteSpace($FaultAfterSubmitStep)) {
            $parameters.FaultAfterSubmitStep = $FaultAfterSubmitStep
        }
        if (-not [string]::IsNullOrWhiteSpace($ResumeOperationId)) {
            $parameters.OperationId = [Guid]$ResumeOperationId
        }
    }
    return & $clientPath @parameters
}

function Expect-ClientFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$MessagePattern,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )
    try {
        & $Action 3>$null | Out-Null
        $failures.Add($FailureMessage)
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            $failures.Add(
                "$FailureMessage Unexpected error: $($_.Exception.Message)")
        }
    }
}

$port = Get-FreePort
$script:BaseUrl = "http://127.0.0.1:$port"
$job = Start-Job -FilePath $fakePath -ArgumentList $port

try {
    Wait-FakeApi $script:BaseUrl $job

    Reset-FakeApi | Out-Null
    $planState = New-TestDirectory "plan"
    $plan = Invoke-Client -StateDirectory $planState -Plan
    $afterPlan = Get-FakeState
    Check ([bool]$plan.planOnly) "default invocation did not remain plan-only"
    Check ($null -eq $afterPlan.operation) "default plan created a rollover operation"
    Check (-not (Test-Path (Join-Path $planState "local.json"))) `
        "default plan wrote a client journal"

    $legacyState = New-TestDirectory "legacy-delete"
    $apiKey = New-TestSecureString $knownApiKey
    Expect-ClientFailure `
        -MessagePattern "retired|never moves or deletes" `
        -FailureMessage "legacy Delete parameter was not rejected" `
        -Action {
            & $clientPath `
                -ControlApiUrl $script:BaseUrl `
                -AdminApiKey $apiKey `
                -StateDirectory $legacyState `
                -PreviousWorldPolicy Delete `
                -AllowDeletePreviousWorld
        }

    foreach ($step in @(
        "Preflight",
        "Drain",
        "GameBackup",
        "EconomyBackup",
        "Stop",
        "NewWorld",
        "Probe",
        "Commit",
        "Reopen")) {
        Reset-FakeApi | Out-Null
        $global:PalControlRolloverTestActionCounts = @{}
        $stateDirectory = New-TestDirectory ("crash-{0}" -f $step)
        Expect-ClientFailure `
            -MessagePattern "Injected client crash after $step" `
            -FailureMessage "client did not inject a recoverable crash after $step action" `
            -Action {
                Invoke-Client `
                    -StateDirectory $stateDirectory `
                    -FaultAfterActionStep $step
            }
        $crashed = Get-FakeState
        Check ($crashed.operation.currentStep -eq $step) `
            "server advanced past $step before its evidence submit during injected crash"
        $result = Invoke-Client -StateDirectory $stateDirectory
        $recovered = Get-FakeState
        Check ([bool]$result.completed -and $recovered.operation.currentStep -eq "Completed") `
            "client did not resume from $step using server operation state"
        foreach ($external in @("Stop", "NewWorld", "Probe")) {
            if ($step -eq $external) {
                Check ([int]$global:PalControlRolloverTestActionCounts[$external] -eq 1) `
                    "$external external action was repeated after durable evidence recovery"
            }
        }
        $journal = Get-Content -Raw -Encoding UTF8 (Join-Path $stateDirectory "local.json")
        Check (-not $journal.Contains($knownApiKey) -and -not $journal.Contains($knownTotp)) `
            "client journal leaked an administrator credential after $step recovery"
    }

    Reset-FakeApi | Out-Null
    $completeSourceState = New-TestDirectory "completed-source"
    Invoke-Client -StateDirectory $completeSourceState | Out-Null
    $completeFreshState = New-TestDirectory "completed-fresh-journal"
    $completedReplay = Invoke-Client `
        -StateDirectory $completeFreshState `
        -ResumeOperationId $operationId
    Check ([bool]$completedReplay.completed -and
           $completedReplay.gameBackupId -eq "44444444444444444444444444444444" -and
           $completedReplay.economyBackupId -eq
            "20260715T000000000Z-77777777777777777777777777777777") `
        "completed operation could not be inspected after local journal loss"

    foreach ($step in @(
        "Preflight",
        "Drain",
        "GameBackup",
        "EconomyBackup",
        "Stop",
        "NewWorld",
        "Probe",
        "Commit",
        "Reopen")) {
        Reset-FakeApi ("drop-step-{0}" -f $step) | Out-Null
        $global:PalControlRolloverTestActionCounts = @{}
        $stateDirectory = New-TestDirectory ("lost-response-{0}" -f $step)
        $result = Invoke-Client -StateDirectory $stateDirectory 3>&1
        $state = Get-FakeState
        Check (@($result)[-1].completed -and $state.operation.currentStep -eq "Completed") `
            "client did not recover $step after a committed response was lost"
        Check ([int]$state.stepCalls.$step -eq 1) `
            "client blindly resubmitted $step instead of reading operation status"
    }

    foreach ($scenario in @("drop-game-backup", "drop-economy-snapshot", "drop-economy-stage")) {
        Reset-FakeApi $scenario | Out-Null
        $stateDirectory = New-TestDirectory $scenario
        Expect-ClientFailure `
            -MessagePattern "outcome is unknown|staging outcome is unknown" `
            -FailureMessage "$scenario did not stop after an ambiguous mutation response" `
            -Action { Invoke-Client -StateDirectory $stateDirectory }
        $result = Invoke-Client -StateDirectory $stateDirectory
        $state = Get-FakeState
        Check ([bool]$result.completed) "$scenario did not resume to completion"
        if ($scenario -eq "drop-game-backup") {
            Check ($state.gameBackupCalls -eq 2 -and
                   $state.gameBackupKey -eq
                    "rollover-$($operationId.Replace('-', ''))-gamebackup") `
                "game backup did not replay the same server step key"
        }
        if ($scenario -eq "drop-economy-snapshot") {
            Check ($state.economySnapshotCalls -eq 2 -and
                   $state.economySnapshotKey -eq
                    "rollover-$($operationId.Replace('-', ''))-economybackup") `
                "economy snapshot did not replay the same server step key"
        }
        if ($scenario -eq "drop-economy-stage") {
            Check ($state.economyStageCalls -eq 2) `
                "published staging verification was not replayed idempotently"
        }
    }

    foreach ($blocked in @(
        @{ scenario = "blocking"; expectedStep = $null; pattern = "unresolved order" },
        @{ scenario = "stale-game"; expectedStep = "GameBackup"; pattern = "RPO window" },
        @{ scenario = "wrong-game-world"; expectedStep = "GameBackup"; pattern = "another world" },
        @{ scenario = "bad-stage"; expectedStep = "EconomyBackup"; pattern = "required flag" },
        @{ scenario = "post-transaction"; expectedStep = "EconomyBackup"; pattern = "Transactions changed" }
    )) {
        Reset-FakeApi $blocked.scenario | Out-Null
        $stateDirectory = New-TestDirectory ("blocked-{0}" -f $blocked.scenario)
        Expect-ClientFailure `
            -MessagePattern $blocked.pattern `
            -FailureMessage "blocker scenario $($blocked.scenario) was not fail-closed" `
            -Action { Invoke-Client -StateDirectory $stateDirectory }
        $state = Get-FakeState
        if ($null -eq $blocked.expectedStep) {
            Check ($null -eq $state.operation) `
                "blocker $($blocked.scenario) created an operation before rejection"
        }
        else {
            Check ($state.operation.currentStep -eq $blocked.expectedStep) `
                "blocker $($blocked.scenario) advanced beyond $($blocked.expectedStep)"
        }
    }

    Reset-FakeApi | Out-Null
    $versionState = New-TestDirectory "version-drift"
    Expect-ClientFailure `
        -MessagePattern "Injected client crash after Preflight" `
        -FailureMessage "version-drift fixture did not freeze an operation" `
        -Action {
            Invoke-Client `
                -StateDirectory $versionState `
                -FaultAfterActionStep Preflight
        }
    Expect-ClientFailure `
        -MessagePattern "RulesVersion conflicts" `
        -FailureMessage "client accepted rules-version drift on resume" `
        -Action {
            Invoke-Client `
                -StateDirectory $versionState `
                -SuppliedRulesVersion "2026-W29-v2"
        }

    if ($failures.Count -ne 0) {
        $failures | ForEach-Object { Write-Error $_ }
        exit 1
    }
    Write-Output (
        "PASS: default plan, retired deletion, all-phase crash/status recovery, " +
        "deterministic backup replay, credential redaction, and fail-closed blockers.")
}
finally {
    try {
        Invoke-RestMethod -Method POST -Uri "$script:BaseUrl/__test/stop" -TimeoutSec 2 |
            Out-Null
    }
    catch {
    }
    Stop-Job $job -ErrorAction SilentlyContinue
    $jobOutput = Receive-Job $job -ErrorAction SilentlyContinue
    if ($null -ne $jobOutput) {
        $jobOutput | Write-Verbose
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    foreach ($directory in $temporaryDirectories) {
        if (Test-Path -LiteralPath $directory) {
            $resolved = [IO.Path]::GetFullPath($directory)
            $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
            if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($resolved).StartsWith(
                    "pal-control-rollover-client-",
                    [StringComparison]::Ordinal)) {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
    Remove-Variable PalControlRolloverTestActionCounts -Scope Global -ErrorAction SilentlyContinue
    $env:PAL_CONTROL_ROLLOVER_TEST_HOOKS = $previousTestHookEnvironment
}
