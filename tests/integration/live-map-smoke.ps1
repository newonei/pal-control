$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$apiExecutable = Join-Path $serviceRoot "bin\Debug\net10.0\PalControl.ControlApi.exe"
$fakeScript = Join-Path $PSScriptRoot "fake_palworld_rest.py"
$fakePort = 18214
$apiPort = 15182
$dataDirectory = Join-Path $env:TEMP (
    "pal-control-live-map-smoke-" + [guid]::NewGuid().ToString("N"))
$stdout = Join-Path $env:TEMP (
    "pal-control-live-map-api-" + [guid]::NewGuid().ToString("N") + ".out.log")
$stderr = Join-Path $env:TEMP (
    "pal-control-live-map-api-" + [guid]::NewGuid().ToString("N") + ".err.log")
$fake = $null
$api = $null

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

function Wait-ForSnapshot(
    [string] $uri,
    [scriptblock] $predicate,
    [string] $failure,
    [int] $attempts = 80) {
    $snapshot = $null
    for ($index = 0; $index -lt $attempts; $index += 1) {
        try {
            $snapshot = Invoke-RestMethod $uri -TimeoutSec 2
            if (& $predicate $snapshot) {
                return $snapshot
            }
        }
        catch {
            # The API may still be starting.
        }
        Start-Sleep -Milliseconds 100
    }
    $detail = if ($null -eq $snapshot) { "no snapshot" } else {
        $snapshot | ConvertTo-Json -Depth 8 -Compress
    }
    throw "$failure Last response: $detail"
}

try {
    & dotnet build $project --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Control API build failed."
    }

    New-Item -ItemType Directory -Path $dataDirectory | Out-Null
    $fake = Start-Process -FilePath "python" -ArgumentList @(
        $fakeScript, "--port", $fakePort
    ) -PassThru -WindowStyle Hidden
    Wait-ForEndpoint "http://127.0.0.1:$fakePort/__state" 40 | Out-Null

    $env:Urls = "http://127.0.0.1:$apiPort"
    $env:Palworld__OfficialRestApi__BaseUrl = "http://127.0.0.1:$fakePort/v1/api/"
    $env:Palworld__OfficialRestApi__Username = "admin"
    $env:Palworld__OfficialRestApi__Password = "test-password"
    $env:Palworld__OfficialRestApi__TimeoutSeconds = "1"
    $env:Palworld__Bridge__PipeName = "pal-control.live-map-smoke.unavailable"
    $env:CommandPersistence__DataDirectory = $dataDirectory
    $env:ExtractionMode__Persistence__DataDirectory = Join-Path $dataDirectory "extraction"
    $env:LiveMap__SampleIntervalMs = "500"
    $env:LiveMap__StaleAfterMs = "1000"
    $env:LiveMap__UnavailableAfterMs = "2000"
    $env:LiveMap__HeartbeatIntervalMs = "1000"
    $env:LiveMap__MaxSubscribers = "4"

    $api = Start-Process -FilePath $apiExecutable -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-ForEndpoint "http://127.0.0.1:$apiPort/health/live" | Out-Null

    $mapUri = "http://127.0.0.1:$apiPort/api/v1/servers/local/live-map"
    $eventsUri = "$mapUri/events"
    $initial = Wait-ForSnapshot $mapUri {
        param($value)
        $value.status -eq "live" -and @($value.items).Count -eq 2
    } "Initial live-map sample was not published."

    if ($initial.source -ne "palworld-official-rest.players" -or
        $initial.coordinateSpace.units -ne "unreal-centimeters" -or
        $initial.items[0].position.x -ne -123888.0 -or
        $initial.items[0].position.y -ne 158000.0) {
        throw "Initial snapshot metadata or coordinates were incorrect."
    }
    $parsedStreamId = [guid]::Empty
    if (-not [guid]::TryParse([string]$initial.streamId, [ref]$parsedStreamId)) {
        throw "Snapshot streamId was not a UUID."
    }
    $serialized = $initial | ConvertTo-Json -Depth 8 -Compress
    if ($serialized -match '"(ip|accountName|ping)"\s*:') {
        throw "The browser snapshot leaked a private upstream player field."
    }

    $notModified = $false
    $etag = $null
    for ($index = 0; $index -lt 10 -and -not $notModified; $index += 1) {
        $httpResponse = Invoke-WebRequest $mapUri -TimeoutSec 2 -UseBasicParsing
        $etag = [string]$httpResponse.Headers["ETag"]
        if ([string]::IsNullOrWhiteSpace($etag)) {
            throw "Snapshot response did not include an ETag."
        }
        try {
            $conditional = Invoke-WebRequest $mapUri `
                -Headers @{ "If-None-Match" = $etag } -TimeoutSec 2 -UseBasicParsing
            $notModified = [int]$conditional.StatusCode -eq 304
        }
        catch {
            $notModified = [int]$_.Exception.Response.StatusCode -eq 304
        }
    }
    if (-not $notModified) {
        throw "If-None-Match did not return 304."
    }

    $sseLines = & curl.exe --silent --no-buffer --noproxy "*" --max-time 3 $eventsUri 2>$null
    $curlStatus = $LASTEXITCODE
    if ($curlStatus -notin @(0, 28)) {
        throw "SSE request failed with curl exit code $curlStatus."
    }
    $sse = $sseLines -join "`n"
    if ($sse -notmatch 'event:\s*snapshot' -or
        $sse -notmatch 'palworld-official-rest\.players') {
        throw "SSE did not immediately emit a complete snapshot."
    }

    $updatedBody = @{
        players = @(
            @{
                name = "Map Tester One"
                accountName = "must-not-leak"
                playerId = "11111111111111111111111111111111"
                userId = "steam_111"
                ip = "203.0.113.99"
                ping = 999
                location_x = 345678.25
                location_y = -456789.5
                level = 36
                building_count = 9
            }
        )
    } | ConvertTo-Json -Depth 6 -Compress
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$fakePort/__players" `
        -ContentType "application/json" -Body $updatedBody -TimeoutSec 2 | Out-Null

    $moved = Wait-ForSnapshot $mapUri {
        param($value)
        $value.status -eq "live" -and
            $value.sequence -gt $initial.sequence -and
            @($value.items).Count -eq 1 -and
            $value.items[0].position.x -eq 345678.25 -and
            $value.items[0].position.y -eq -456789.5
    } "Moved player snapshot was not observed."

    $beforeBurst = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    1..10 | ForEach-Object {
        Invoke-RestMethod $mapUri -TimeoutSec 2 | Out-Null
    }
    $afterBurst = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if (($afterBurst.playersRequestCount - $beforeBurst.playersRequestCount) -gt 2) {
        throw "Snapshot readers triggered extra upstream REST requests."
    }

    Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:$fakePort/__players/availability" `
        -ContentType "application/json" -Body '{"available":false}' -TimeoutSec 2 | Out-Null
    $stale = Wait-ForSnapshot $mapUri {
        param($value)
        $value.status -eq "stale" -and @($value.items).Count -eq 1
    } "Failed samples did not retain the last position and become stale."
    $unavailable = Wait-ForSnapshot $mapUri {
        param($value)
        $value.status -eq "unavailable" -and @($value.items).Count -eq 1
    } "Long-lived failure did not become unavailable while retaining positions."

    [pscustomobject]@{
        initialSequence = $initial.sequence
        movedSequence = $moved.sequence
        retainedSequence = $unavailable.sequence
        initialPlayers = @($initial.items).Count
        movedPlayers = @($moved.items).Count
        staleStatus = $stale.status
        unavailableStatus = $unavailable.status
        etag = $etag
        sseSnapshot = $true
        privateFieldsLeaked = $false
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
    Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stdout -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue
}
