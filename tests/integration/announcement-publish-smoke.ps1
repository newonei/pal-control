$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "helpers\test-api-environment.ps1")

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$apiExecutable = Join-Path $serviceRoot "bin\Debug\net10.0\PalControl.ControlApi.exe"
$bridgeProject = Join-Path $repositoryRoot "tools\bridge-smoke\PalControl.BridgeSmoke.csproj"
$bridgeExecutable = Join-Path $repositoryRoot "tools\bridge-smoke\bin\Debug\net10.0\PalControl.BridgeSmoke.exe"
$fakeScript = Join-Path $PSScriptRoot "fake_palworld_rest.py"
$fakePort = 18212
$apiPort = 15180
$bridgePipe = "pal-control.announcement-smoke." + [guid]::NewGuid().ToString("N")
$dataDirectory = Join-Path $env:TEMP (
    "pal-control-announcement-smoke-" + [guid]::NewGuid().ToString("N"))
$stdout = Join-Path $env:TEMP (
    "pal-control-api-" + [guid]::NewGuid().ToString("N") + ".out.log")
$stderr = Join-Path $env:TEMP (
    "pal-control-api-" + [guid]::NewGuid().ToString("N") + ".err.log")
$fake = $null
$fakeBridge = $null
$api = $null

function Start-TestApi {
    Start-Process -FilePath $apiExecutable -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
}

function Wait-ForEndpoint([string] $uri, [int] $attempts = 80) {
    for ($index = 0; $index -lt $attempts; $index += 1) {
        try {
            Invoke-RestMethod $uri -TimeoutSec 1 | Out-Null
            return
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Endpoint did not become ready: $uri"
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
    Wait-ForEndpoint "http://127.0.0.1:$fakePort/__state" 40
    $fakeBridge = Start-Process -FilePath $bridgeExecutable -ArgumentList @(
        $bridgePipe
    ) -PassThru -WindowStyle Hidden

    $env:Urls = "http://127.0.0.1:$apiPort"
    $env:Palworld__OfficialRestApi__BaseUrl = "http://127.0.0.1:$fakePort/v1/api/"
    $env:Palworld__OfficialRestApi__Username = "admin"
    $env:Palworld__OfficialRestApi__Password = "test-password"
    $env:Palworld__OfficialRestApi__TimeoutSeconds = "2"
    $env:Palworld__Bridge__PipeName = $bridgePipe
    $env:CommandPersistence__DataDirectory = $dataDirectory
    $env:ExtractionMode__Persistence__DataDirectory = Join-Path $dataDirectory "extraction"

    $api = Start-TestApi
    Wait-ForEndpoint "http://127.0.0.1:$apiPort/health/live"

    $capabilityUri = "http://127.0.0.1:$apiPort/api/v1/servers/local/capabilities"
    $capabilities = $null
    for ($index = 0; $index -lt 40; $index += 1) {
        $capabilities = Invoke-RestMethod $capabilityUri -TimeoutSec 2
        if ($capabilities.publishChatAnnouncements -and
            $capabilities.publishClientOverlay -and
            $capabilities.publishTopBanner) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $capabilities.publishAnnouncements -or
        -not $capabilities.publishChatAnnouncements -or
        -not $capabilities.publishClientOverlay -or
        -not $capabilities.publishTopBanner -or
        -not $capabilities.commandQueueReady -or
        -not $capabilities.auditReady) {
        throw "Announcement capabilities were not enabled."
    }

    $overlayProbe = Invoke-RestMethod (
        "http://127.0.0.1:$apiPort/api/v1/servers/local/announcements/client-overlay/probe") -TimeoutSec 2
    if (-not $overlayProbe.ready -or $overlayProbe.dispatched -or
        $overlayProbe.propertiesSize -ne 16 -or $overlayProbe.functionFlags -ne 150720) {
        throw "Read-only client-overlay compatibility probe did not return the expected signature."
    }
    $bannerProbe = Invoke-RestMethod (
        "http://127.0.0.1:$apiPort/api/v1/servers/local/announcements/top-banner/probe") -TimeoutSec 2
    if (-not $bannerProbe.ready -or $bannerProbe.dispatched) {
        throw "Read-only top-banner compatibility probe did not report ready without dispatching."
    }

    $draftUri = "http://127.0.0.1:$apiPort/api/v1/servers/local/announcements"
    $draftHeaders = @{ "Idempotency-Key" = "draft-test-00000001" }
    $draftBody = @{
        title = "Integration Test"
        body = "This message only reaches the fake REST server."
        audience = @{ type = "global"; ids = $null }
        channels = @("chat")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress

    $draft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers $draftHeaders -ContentType "application/json" -Body $draftBody -TimeoutSec 2
    $draftReplay = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers $draftHeaders -ContentType "application/json" -Body $draftBody -TimeoutSec 2
    if ($draft.announcementId -ne $draftReplay.announcementId) {
        throw "Draft idempotency replay returned a different announcement."
    }

    $differentBody = @{
        title = "Different Payload"
        body = "Must conflict."
        audience = @{ type = "global"; ids = $null }
        channels = @("chat")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $draftConflict = $false
    try {
        Invoke-RestMethod -Method Post -Uri $draftUri -Headers $draftHeaders `
            -ContentType "application/json" -Body $differentBody -TimeoutSec 2 | Out-Null
    }
    catch {
        $draftConflict = [int]$_.Exception.Response.StatusCode -eq 409
    }
    if (-not $draftConflict) {
        throw "Draft idempotency conflict did not return 409."
    }

    $publishHeaders = @{ "Idempotency-Key" = "publish-test-00000001" }
    $publishUri = "$draftUri/$($draft.announcementId)/publish"
    $command = Invoke-RestMethod -Method Post -Uri $publishUri `
        -Headers $publishHeaders -TimeoutSec 2
    for ($index = 0;
        $index -lt 50 -and $command.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $command = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($command.statusUrl)") -TimeoutSec 2
    }
    if ($command.state -ne "succeeded") {
        $detail = $command | ConvertTo-Json -Depth 5 -Compress
        throw "Publish command did not succeed: $detail"
    }

    $replay = Invoke-RestMethod -Method Post -Uri $publishUri `
        -Headers $publishHeaders -TimeoutSec 2
    if ($replay.commandId -ne $command.commandId) {
        throw "Publish idempotency replay returned a different command."
    }

    $fakeState = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($fakeState.count -ne 1) {
        throw "Fake REST received $($fakeState.count) announcements; expected 1."
    }
    $expectedMessage = ([char]0x3010) + "Integration Test" + ([char]0x3011) +
        "`nThis message only reaches the fake REST server."
    if ($fakeState.lastBody.message -ne $expectedMessage) {
        throw "Official REST received an unexpected announcement payload: $($fakeState.lastBody.message | ConvertTo-Json -Compress)"
    }

    $overlayBody = @{
        title = "Overlay Integration Test"
        body = "This message only reaches the fake Native Bridge."
        audience = @{ type = "global"; ids = $null }
        channels = @("client-overlay")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $overlayDraft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers @{ "Idempotency-Key" = "draft-overlay-test-0001" } `
        -ContentType "application/json" -Body $overlayBody -TimeoutSec 2
    $overlayCommand = Invoke-RestMethod -Method Post `
        -Uri "$draftUri/$($overlayDraft.announcementId)/publish" `
        -Headers @{ "Idempotency-Key" = "publish-overlay-test-0001" } -TimeoutSec 2
    for ($index = 0;
        $index -lt 50 -and $overlayCommand.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $overlayCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($overlayCommand.statusUrl)") -TimeoutSec 2
    }
    $overlayResult = @($overlayCommand.result.channels) |
        Where-Object channel -eq "client-overlay" |
        Select-Object -First 1
    if ($overlayCommand.state -ne "succeeded" -or
        $overlayResult.state -ne "succeeded" -or
        $overlayResult.attemptedRecipients -ne 2 -or
        $null -ne $overlayResult.deliveredRecipients) {
        throw "Client-overlay command did not return the expected per-channel delivery result."
    }
    $afterOverlay = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($afterOverlay.count -ne 1) {
        throw "Overlay-only announcement unexpectedly reached official REST."
    }

    $bannerBody = @{
        title = "Top Banner Integration Test"
        body = "This message only reaches the fake Native Bridge banner operation."
        audience = @{ type = "global"; ids = $null }
        channels = @("top-banner")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $bannerDraft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers @{ "Idempotency-Key" = "draft-banner-test-00001" } `
        -ContentType "application/json" -Body $bannerBody -TimeoutSec 2
    $bannerCommand = Invoke-RestMethod -Method Post `
        -Uri "$draftUri/$($bannerDraft.announcementId)/publish" `
        -Headers @{ "Idempotency-Key" = "publish-banner-test-00001" } -TimeoutSec 2
    for ($index = 0;
        $index -lt 50 -and $bannerCommand.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $bannerCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($bannerCommand.statusUrl)") -TimeoutSec 2
    }
    $bannerResult = @($bannerCommand.result.channels) |
        Where-Object channel -eq "top-banner" |
        Select-Object -First 1
    if ($bannerCommand.state -ne "succeeded" -or
        $bannerResult.state -ne "succeeded" -or
        $bannerResult.attemptedRecipients -ne 2 -or
        $null -ne $bannerResult.deliveredRecipients) {
        throw "Top-banner command did not return the expected per-channel delivery result."
    }
    $afterBanner = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($afterBanner.count -ne 1) {
        throw "Top-banner-only announcement unexpectedly reached official REST."
    }

    $combinedBody = @{
        title = "Combined Integration Test"
        body = "This message reaches all three fake channels exactly once."
        audience = @{ type = "global"; ids = $null }
        channels = @("chat", "client-overlay", "top-banner")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $combinedDraft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers @{ "Idempotency-Key" = "draft-combined-test-01" } `
        -ContentType "application/json" -Body $combinedBody -TimeoutSec 2
    $combinedCommand = Invoke-RestMethod -Method Post `
        -Uri "$draftUri/$($combinedDraft.announcementId)/publish" `
        -Headers @{ "Idempotency-Key" = "publish-combined-test-01" } -TimeoutSec 2
    for ($index = 0;
        $index -lt 50 -and $combinedCommand.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $combinedCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($combinedCommand.statusUrl)") -TimeoutSec 2
    }
    $combinedResults = @($combinedCommand.result.channels)
    if ($combinedCommand.state -ne "succeeded" -or
        $combinedResults.Count -ne 3 -or
        @($combinedResults | Where-Object state -ne "succeeded").Count -ne 0) {
        throw "Combined announcement did not succeed on all three channels."
    }

    $audit = Invoke-RestMethod (
        "http://127.0.0.1:$apiPort/api/v1/audit/commands?limit=20") -TimeoutSec 2
    $states = @(
        $audit.items |
            Where-Object commandId -eq $command.commandId |
            Select-Object -ExpandProperty state
    )
    foreach ($required in @("accepted", "dispatched", "succeeded")) {
        if ($required -notin $states) {
            throw "Audit state '$required' is missing."
        }
    }

    $scheduledAt = [DateTimeOffset]::UtcNow.AddSeconds(1.5).ToString("o")
    $scheduledBody = @{
        title = "Scheduled Integration Test"
        body = "This scheduled message only reaches the fake REST server."
        audience = @{ type = "global"; ids = $null }
        channels = @("chat")
        publishAt = $scheduledAt
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $scheduledDraft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers @{ "Idempotency-Key" = "draft-test-00000002" } `
        -ContentType "application/json" -Body $scheduledBody -TimeoutSec 2
    $scheduledUri = "$draftUri/$($scheduledDraft.announcementId)/publish"
    $scheduledCommand = Invoke-RestMethod -Method Post -Uri $scheduledUri `
        -Headers @{ "Idempotency-Key" = "publish-test-00000002" } -TimeoutSec 2
    $beforeSchedule = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($beforeSchedule.count -ne 2) {
        throw "Scheduled announcement was sent before its due time."
    }
    $secondPublishConflict = $false
    try {
        Invoke-RestMethod -Method Post -Uri $scheduledUri `
            -Headers @{ "Idempotency-Key" = "publish-test-different-key" } -TimeoutSec 2 | Out-Null
    }
    catch {
        $secondPublishConflict = [int]$_.Exception.Response.StatusCode -eq 409
    }
    if (-not $secondPublishConflict) {
        throw "A second publish key was allowed for an already queued announcement."
    }
    for ($index = 0;
        $index -lt 50 -and $scheduledCommand.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $scheduledCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($scheduledCommand.statusUrl)") -TimeoutSec 2
    }
    if ($scheduledCommand.state -ne "succeeded") {
        throw "Scheduled command did not succeed."
    }

    $uncertainBody = @{
        title = "Uncertain Integration Test"
        body = "UNCERTAIN_TEST simulates a lost response after delivery."
        audience = @{ type = "global"; ids = $null }
        channels = @("chat")
        publishAt = $null
        expiresAt = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $uncertainDraft = Invoke-RestMethod -Method Post -Uri $draftUri `
        -Headers @{ "Idempotency-Key" = "draft-test-00000003" } `
        -ContentType "application/json" -Body $uncertainBody -TimeoutSec 2
    $uncertainHeaders = @{ "Idempotency-Key" = "publish-test-00000003" }
    $uncertainUri = "$draftUri/$($uncertainDraft.announcementId)/publish"
    $uncertainCommand = Invoke-RestMethod -Method Post -Uri $uncertainUri `
        -Headers $uncertainHeaders -TimeoutSec 2
    for ($index = 0;
        $index -lt 50 -and $uncertainCommand.state -in @("accepted", "dispatched");
        $index += 1) {
        Start-Sleep -Milliseconds 100
        $uncertainCommand = Invoke-RestMethod (
            "http://127.0.0.1:$apiPort$($uncertainCommand.statusUrl)") -TimeoutSec 2
    }
    if ($uncertainCommand.state -ne "uncertain") {
        throw "Lost-response simulation was not marked uncertain."
    }
    $uncertainReplay = Invoke-RestMethod -Method Post -Uri $uncertainUri `
        -Headers $uncertainHeaders -TimeoutSec 2
    if ($uncertainReplay.commandId -ne $uncertainCommand.commandId -or
        $uncertainReplay.state -ne "uncertain") {
        throw "Uncertain command replay did not return the original command."
    }
    Start-Sleep -Milliseconds 1200

    Stop-Process -Id $api.Id -Force
    Wait-Process -Id $api.Id -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    $api = Start-TestApi
    Wait-ForEndpoint "http://127.0.0.1:$apiPort/health/live"

    $replayAfterRestart = Invoke-RestMethod -Method Post -Uri $publishUri `
        -Headers $publishHeaders -TimeoutSec 2
    if ($replayAfterRestart.commandId -ne $command.commandId -or
        $replayAfterRestart.state -ne "succeeded") {
        throw "Restart-safe idempotency did not return the original command."
    }
    Start-Sleep -Milliseconds 300
    $fakeStateAfterRestart = Invoke-RestMethod (
        "http://127.0.0.1:$fakePort/__state") -TimeoutSec 2
    if ($fakeStateAfterRestart.count -ne 4) {
        throw "Fake REST received a duplicate announcement after restart."
    }

    [pscustomobject]@{
        publishCapability = $capabilities.publishAnnouncements
        overlayCapability = $capabilities.publishClientOverlay
        overlayProbeReady = $overlayProbe.ready
        bannerCapability = $capabilities.publishTopBanner
        bannerProbeReady = $bannerProbe.ready
        commandId = $command.commandId
        finalState = $command.state
        restPostCount = $fakeStateAfterRestart.count
        overlayState = $overlayCommand.state
        bannerState = $bannerCommand.state
        combinedState = $combinedCommand.state
        auditStates = $states -join ","
        scheduledState = $scheduledCommand.state
        uncertainState = $uncertainCommand.state
        restartReplayState = $replayAfterRestart.state
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
    Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stdout -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue
}
