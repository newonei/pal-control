$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$apiExecutable = Join-Path $serviceRoot "bin\Debug\net10.0\PalControl.ControlApi.exe"
$bridgeProject = Join-Path $repositoryRoot "tools\bridge-smoke\PalControl.BridgeSmoke.csproj"
$bridgeExecutable = Join-Path $repositoryRoot "tools\bridge-smoke\bin\Debug\net10.0\PalControl.BridgeSmoke.exe"
$fakeScript = Join-Path $PSScriptRoot "fake_palworld_rest.py"
$fakePort = 18213
$apiPort = 15181
$bridgePipe = "pal-control.notification-smoke." + [guid]::NewGuid().ToString("N")
$dataDirectory = Join-Path $env:TEMP (
    "pal-control-notification-smoke-" + [guid]::NewGuid().ToString("N"))
$deliveryState = Join-Path $dataDirectory "native-notification-deliveries.log"
$stdout = Join-Path $env:TEMP (
    "pal-control-notification-api-" + [guid]::NewGuid().ToString("N") + ".out.log")
$stderr = Join-Path $env:TEMP (
    "pal-control-notification-api-" + [guid]::NewGuid().ToString("N") + ".err.log")
$fake = $null
$fakeBridge = $null
$api = $null

function Start-TestApi {
    Start-Process -FilePath $apiExecutable -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
}

function Start-TestBridge {
    Start-Process -FilePath $bridgeExecutable -ArgumentList @(
        $bridgePipe, $deliveryState
    ) -PassThru -WindowStyle Hidden
}

function Wait-ForEndpoint([string] $uri, [int] $attempts = 80) {
    for ($index = 0; $index -lt $attempts; $index += 1) {
        try {
            return Invoke-RestMethod $uri -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Endpoint did not become ready: $uri"
}

function Wait-ForCommand([object] $command, [int] $attempts = 80) {
    for ($index = 0;
        $index -lt $attempts -and $command.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $command = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($command.statusUrl)") -TimeoutSec 2
    }
    return $command
}

function Assert-HttpStatus([scriptblock] $operation, [int] $status, [string] $message) {
    $matched = $false
    try {
        & $operation | Out-Null
    }
    catch {
        $matched = [int]$_.Exception.Response.StatusCode -eq $status
    }
    if (-not $matched) {
        throw $message
    }
}

function New-NotificationJson(
    [hashtable] $parameters,
    [string] $preset = "boss-defeat-reward",
    [object] $displayAt = $null,
    [object] $expiresAt = $null) {
    @{
        schemaVersion = "1"
        template = @{
            preset = $preset
            parameters = $parameters
        }
        audience = @{ type = "global"; ids = $null }
        displayAt = $displayAt
        expiresAt = $expiresAt
        reason = "isolated server-native notification smoke test"
    } | ConvertTo-Json -Depth 8 -Compress
}

try {
    & dotnet build $project --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Control API build failed."
    }
    & dotnet build $bridgeProject --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Fake Native Bridge build failed."
    }

    New-Item -ItemType Directory -Path $dataDirectory | Out-Null
    $fake = Start-Process -FilePath "python" -ArgumentList @(
        $fakeScript, "--port", $fakePort
    ) -PassThru -WindowStyle Hidden
    Wait-ForEndpoint "http://127.0.0.1:$fakePort/__state" 40 | Out-Null
    $fakeBridge = Start-TestBridge

    $env:Urls = "http://127.0.0.1:$apiPort"
    $env:Palworld__OfficialRestApi__BaseUrl = "http://127.0.0.1:$fakePort/v1/api/"
    $env:Palworld__OfficialRestApi__Username = "admin"
    $env:Palworld__OfficialRestApi__Password = "test-password"
    $env:Palworld__OfficialRestApi__TimeoutSeconds = "2"
    $env:Palworld__Bridge__PipeName = $bridgePipe
    $env:Palworld__Bridge__CommandTimeoutSeconds = "15"
    $env:CommandPersistence__DataDirectory = $dataDirectory
    $env:ExtractionMode__Persistence__DataDirectory = Join-Path $dataDirectory "extraction"

    $api = Start-TestApi
    Wait-ForEndpoint "http://127.0.0.1:$apiPort/health/live" | Out-Null

    $capabilityUri = "http://127.0.0.1:$apiPort/api/v1/servers/local/in-game-notifications/capabilities"
    $probe = Wait-ForEndpoint $capabilityUri
    if (-not $probe.ready -or $probe.dispatched -or
        $probe.mode -ne "server-native-presets" -or
        @($probe.supportedAudiences) -notcontains "global" -or
        @($probe.supportedAudiences) -contains "players") {
        throw "Native notification capability probe was not fail-closed global-only."
    }
    $presetIds = @($probe.supportedPresets | Select-Object -ExpandProperty name)
    foreach ($presetId in @(
        "boss-defeat-reward",
        "boss-bonus-exp",
        "expedition-bonus-exp",
        "unlock-hard-mode")) {
        if ($presetId -notin $presetIds) {
            throw "Native notification preset '$presetId' is missing."
        }
    }
    $bossCapability = @($probe.supportedPresets |
        Where-Object name -eq "boss-defeat-reward" |
        Select-Object -First 1)[0]
    if ($bossCapability.function -ne "/Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient" -or
        $bossCapability.functionFlags -ne 84020416 -or
        $bossCapability.propertiesSize -ne 20 -or
        $bossCapability.positionPolicy.mode -ne "game-defined" -or
        $bossCapability.durationPolicy.mode -ne "game-defined" -or
        $bossCapability.positionPolicy.configurable -or
        $bossCapability.durationPolicy.configurable -or
        [string]::IsNullOrWhiteSpace($bossCapability.positionPolicy.note) -or
        $bossCapability.durationPolicy.note -notmatch "delaySeconds") {
        throw "The exact native owner, ABI, or game-defined presentation constraints were not exposed honestly."
    }

    $rootCapabilities = $null
    for ($index = 0; $index -lt 40; $index += 1) {
        $rootCapabilities = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort/api/v1/servers/local/capabilities") -TimeoutSec 4
        if ($rootCapabilities.sendInGameNotifications) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $rootCapabilities.sendInGameNotifications) {
        throw "Root sendInGameNotifications capability was not enabled."
    }

    $notificationUri = "http://127.0.0.1:$apiPort/api/v1/servers/local/in-game-notifications"
    $createHeaders = @{ "Idempotency-Key" = "notification-create-0001" }
    $body = New-NotificationJson @{ technologyPoint = 1; delaySeconds = 0 }
    $notification = Invoke-RestMethod -Method Post -Uri $notificationUri `
        -Headers $createHeaders -ContentType "application/json" -Body $body -TimeoutSec 3
    $createReplay = Invoke-RestMethod -Method Post -Uri $notificationUri `
        -Headers $createHeaders -ContentType "application/json" -Body $body -TimeoutSec 3
    if ($notification.notificationId -ne $createReplay.notificationId) {
        throw "Notification create idempotency replay returned a different resource."
    }

    Assert-HttpStatus {
        Invoke-RestMethod -Method Post -Uri $notificationUri -Headers $createHeaders `
            -ContentType "application/json" `
            -Body (New-NotificationJson @{ technologyPoint = 2 }) -TimeoutSec 3
    } 409 "Notification create idempotency conflict did not return 409."

    $customOverride = @{
        schemaVersion = "1"
        template = @{
            preset = "boss-defeat-reward"
            parameters = @{}
            position = "top-left"
        }
        audience = @{ type = "global"; ids = $null }
        displayAt = $null
        expiresAt = $null
        reason = "unsupported custom position override"
    } | ConvertTo-Json -Depth 8 -Compress
    Assert-HttpStatus {
        Invoke-RestMethod -Method Post -Uri $notificationUri `
            -Headers @{ "Idempotency-Key" = "notification-custom-0001" } `
            -ContentType "application/json" -Body $customOverride -TimeoutSec 3
    } 400 "Unsupported custom presentation override did not return 400."

    $globalNullIdBody = @{
        schemaVersion = "1"
        template = @{ preset = "unlock-hard-mode"; parameters = @{} }
        audience = @{ type = "global"; ids = @($null) }
        displayAt = $null
        expiresAt = $null
        reason = "invalid global null target"
    } | ConvertTo-Json -Depth 8 -Compress
    Assert-HttpStatus {
        Invoke-RestMethod -Method Post -Uri $notificationUri `
            -Headers @{ "Idempotency-Key" = "notification-null-id-01" } `
            -ContentType "application/json" -Body $globalNullIdBody -TimeoutSec 3
    } 400 "Global audience ids=[null] did not fail validation."

    Assert-HttpStatus {
        Invoke-RestMethod -Method Post -Uri $notificationUri `
            -Headers @{ "Idempotency-Key" = "notification-unknown-0001" } `
            -ContentType "application/json" `
            -Body (New-NotificationJson @{ defeatCharacterId = "None" }) -TimeoutSec 3
    } 422 "Unadvertised preset parameter did not return 422."

    $playersBody = @{
        schemaVersion = "1"
        template = @{ preset = "unlock-hard-mode"; parameters = @{} }
        audience = @{ type = "players"; ids = @("player-1") }
        displayAt = $null
        expiresAt = $null
        reason = "unsupported targeted native notification"
    } | ConvertTo-Json -Depth 8 -Compress
    Assert-HttpStatus {
        Invoke-RestMethod -Method Post -Uri $notificationUri `
            -Headers @{ "Idempotency-Key" = "notification-players-01" } `
            -ContentType "application/json" -Body $playersBody -TimeoutSec 3
    } 422 "Audience not advertised by the native probe did not return 422."

    $dispatchUri = "$notificationUri/$($notification.notificationId)/dispatch"
    $dispatchHeaders = @{ "Idempotency-Key" = "notification-dispatch-0001" }
    $command = Invoke-RestMethod -Method Post -Uri $dispatchUri `
        -Headers $dispatchHeaders -TimeoutSec 3
    $command = Wait-ForCommand $command
    if ($command.state -ne "succeeded" -or
        $command.result.notificationId -ne $notification.notificationId -or
        $command.result.preset -ne "boss-defeat-reward" -or
        $command.result.attemptedRecipients -ne 2 -or
        $command.result.deliveryAcknowledged) {
        throw "Native notification command did not return the expected Client RPC result: $($command | ConvertTo-Json -Depth 8 -Compress)"
    }
    $dispatchReplay = Invoke-RestMethod -Method Post -Uri $dispatchUri `
        -Headers $dispatchHeaders -TimeoutSec 3
    if ($dispatchReplay.commandId -ne $command.commandId) {
        throw "Notification dispatch idempotency replay returned a different command."
    }

    $audit = Invoke-RestMethod (
        "http://127.0.0.1:$apiPort/api/v1/audit/in-game-notification-commands?limit=20") -TimeoutSec 3
    $auditStates = @($audit.items |
        Where-Object commandId -eq $command.commandId |
        Select-Object -ExpandProperty state)
    foreach ($state in @("accepted", "dispatched", "succeeded")) {
        if ($state -notin $auditStates) {
            throw "Notification audit state '$state' is missing."
        }
    }

    $scheduledAt = [DateTimeOffset]::UtcNow.AddSeconds(1.5)
    $scheduledBody = New-NotificationJson @{} "unlock-hard-mode" $scheduledAt.ToString("o") $null
    $scheduled = Invoke-RestMethod -Method Post -Uri $notificationUri `
        -Headers @{ "Idempotency-Key" = "notification-schedule-01" } `
        -ContentType "application/json" -Body $scheduledBody -TimeoutSec 3
    $scheduledCommand = Invoke-RestMethod -Method Post `
        -Uri "$notificationUri/$($scheduled.notificationId)/dispatch" `
        -Headers @{ "Idempotency-Key" = "notification-schedule-dispatch-01" } -TimeoutSec 3
    if ($scheduledCommand.state -ne "accepted") {
        throw "Scheduled notification was dispatched before its due time."
    }
    $scheduledCommand = Wait-ForCommand $scheduledCommand
    if ($scheduledCommand.state -ne "succeeded") {
        throw "Scheduled native notification did not succeed."
    }

    $restartBody = New-NotificationJson @{ technologyPoint = 9999; delaySeconds = 0 }
    $restartNotification = Invoke-RestMethod -Method Post -Uri $notificationUri `
        -Headers @{ "Idempotency-Key" = "notification-restart-01" } `
        -ContentType "application/json" -Body $restartBody -TimeoutSec 3
    $restartDispatchUri = "$notificationUri/$($restartNotification.notificationId)/dispatch"
    $restartHeaders = @{ "Idempotency-Key" = "notification-restart-dispatch-01" }
    $restartCommand = Invoke-RestMethod -Method Post -Uri $restartDispatchUri `
        -Headers $restartHeaders -TimeoutSec 3
    for ($index = 0; $index -lt 50; $index += 1) {
        $restartCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($restartCommand.statusUrl)") -TimeoutSec 2
        if ($restartCommand.state -eq "dispatched") {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if ($restartCommand.state -ne "dispatched") {
        throw "Restart test command never reached the durably dispatched state."
    }

    Stop-Process -Id $api.Id -Force
    Wait-Process -Id $api.Id -ErrorAction SilentlyContinue
    $api = $null
    Stop-Process -Id $fakeBridge.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $fakeBridge.Id -ErrorAction SilentlyContinue
    $fakeBridge = $null
    Start-Sleep -Milliseconds 300

    $sendCountBeforeRestart = @(Get-Content $deliveryState).Count
    $fakeBridge = Start-TestBridge
    $api = Start-TestApi
    Wait-ForEndpoint "http://127.0.0.1:$apiPort/health/live" | Out-Null
    for ($index = 0; $index -lt 50; $index += 1) {
        try {
            $recovered = Invoke-RestMethod (
                "http://127.0.0.1:$apiPort$($restartCommand.statusUrl)") -TimeoutSec 2
            if ($recovered.state -eq "uncertain") {
                break
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 100
    }
    if ($recovered.state -ne "uncertain") {
        throw "A command interrupted after dispatched was not recovered as uncertain."
    }
    $restartReplay = Invoke-RestMethod -Method Post -Uri $restartDispatchUri `
        -Headers $restartHeaders -TimeoutSec 3
    if ($restartReplay.commandId -ne $restartCommand.commandId -or
        $restartReplay.state -ne "uncertain") {
        throw "Restart recovery did not preserve the original idempotent uncertain command."
    }
    Start-Sleep -Milliseconds 500
    $sendCountAfterRestart = @(Get-Content $deliveryState).Count
    if ($sendCountAfterRestart -ne $sendCountBeforeRestart) {
        throw "The interrupted native notification was blindly resent after restart."
    }

    [pscustomobject]@{
        capability = $rootCapabilities.sendInGameNotifications
        presetCount = $presetIds.Count
        createReplay = $notification.notificationId -eq $createReplay.notificationId
        dispatchState = $command.state
        auditStates = $auditStates -join ","
        scheduledState = $scheduledCommand.state
        recoveredState = $recovered.state
        nativeSendCount = $sendCountAfterRestart
        restartResent = $sendCountAfterRestart -ne $sendCountBeforeRestart
    } | ConvertTo-Json -Compress
}
catch {
    if (Test-Path $stdout) {
        Write-Host (Get-Content -Raw $stdout)
    }
    if (Test-Path $stderr) {
        Write-Host (Get-Content -Raw $stderr)
    }
    throw
}
finally {
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
    if ($fake -and -not $fake.HasExited) {
        Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue
    }
    if ($fakeBridge -and -not $fakeBridge.HasExited) {
        Stop-Process -Id $fakeBridge.Id -Force -ErrorAction SilentlyContinue
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedDataDirectory = [IO.Path]::GetFullPath($dataDirectory)
    if ($resolvedDataDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $stdout -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue
}
