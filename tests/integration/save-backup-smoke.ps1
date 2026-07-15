$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$fakeScript = Join-Path $PSScriptRoot "fake_palworld_rest.py"
$fakePort = Get-FreeTcpPort
$apiPort = Get-FreeTcpPort
while ($apiPort -eq $fakePort) {
    $apiPort = Get-FreeTcpPort
}
$worldGuid = "ABCDEF0123456789ABCDEF0123456789"
$testRoot = Join-Path $env:TEMP (
    "pal-control-save-backup-smoke-" + [guid]::NewGuid().ToString("N"))
$buildDirectory = Join-Path $testRoot "build"
$apiExecutable = Join-Path $buildDirectory "PalControl.ControlApi.exe"
$installRoot = Join-Path $testRoot "PalServer"
$configRoot = Join-Path $installRoot "Pal\Saved\Config\WindowsServer"
$worldRoot = Join-Path $installRoot "Pal\Saved\SaveGames\0\$worldGuid"
$playersRoot = Join-Path $worldRoot "Players"
$backupRoot = Join-Path $testRoot "managed-backups"
$dataDirectory = Join-Path $testRoot "control-data"
$stdout = Join-Path $testRoot "control-api.out.log"
$stderr = Join-Path $testRoot "control-api.err.log"
$fakeStdout = Join-Path $testRoot "fake-rest.out.log"
$fakeStderr = Join-Path $testRoot "fake-rest.err.log"
$fake = $null
$api = $null
$baseUri = "http://127.0.0.1:$apiPort"

function Wait-ForEndpoint(
    [string] $uri,
    [int] $attempts = 100,
    [Diagnostics.Process] $process = $null) {
    for ($index = 0; $index -lt $attempts; $index += 1) {
        if ($process -and $process.HasExited) {
            throw "Process exited before endpoint became ready: $uri (exit $($process.ExitCode))"
        }
        try {
            return Invoke-RestMethod $uri -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Endpoint did not become ready: $uri"
}

function Start-TestApi {
    return Start-Process -FilePath $apiExecutable -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
}

function Wait-SaveCommand([object] $command, [int] $attempts = 160) {
    $current = $command
    for ($index = 0; $index -lt $attempts; $index += 1) {
        if ($current.state -notin @("accepted", "dispatched")) {
            return $current
        }
        Start-Sleep -Milliseconds 100
        $statusUri = if ([string]$current.statusUrl -match "^https?://") {
            [string]$current.statusUrl
        }
        else {
            "$baseUri$($current.statusUrl)"
        }
        $current = Invoke-RestMethod $statusUri -TimeoutSec 2
    }
    throw "Save command did not reach a terminal state: $($current | ConvertTo-Json -Depth 10 -Compress)"
}

function Invoke-JsonPostResponse(
    [string] $uri,
    [string] $idempotencyKey,
    [hashtable] $body) {
    $response = Invoke-WebRequest -Method Post -Uri $uri `
        -Headers @{ "Idempotency-Key" = $idempotencyKey } `
        -ContentType "application/json" `
        -Body ($body | ConvertTo-Json -Depth 6 -Compress) -TimeoutSec 3 `
        -UseBasicParsing
    return [pscustomobject]@{
        statusCode = [int]$response.StatusCode
        body = $response.Content | ConvertFrom-Json
    }
}

function Invoke-JsonPost(
    [string] $uri,
    [string] $idempotencyKey,
    [hashtable] $body) {
    return (Invoke-JsonPostResponse $uri $idempotencyKey $body).body
}

function Assert-Conflict(
    [string] $uri,
    [string] $idempotencyKey,
    [hashtable] $body,
    [string] $failure) {
    $conflict = $false
    try {
        Invoke-JsonPost $uri $idempotencyKey $body | Out-Null
    }
    catch {
        $conflict = [int]$_.Exception.Response.StatusCode -eq 409
    }
    if (-not $conflict) {
        throw $failure
    }
}

function Assert-BadWrite(
    [string] $uri,
    [string] $idempotencyKey,
    [hashtable] $body,
    [string] $failure) {
    $badRequest = $false
    try {
        Invoke-JsonPost $uri $idempotencyKey $body | Out-Null
    }
    catch {
        $badRequest = [int]$_.Exception.Response.StatusCode -eq 400
    }
    if (-not $badRequest) {
        throw $failure
    }
}

function Assert-HttpStatus(
    [string] $method,
    [string] $uri,
    [int[]] $expected,
    [string] $failure) {
    try {
        Invoke-WebRequest -Method $method -Uri $uri -TimeoutSec 2 `
            -UseBasicParsing | Out-Null
    }
    catch {
        if ([int]$_.Exception.Response.StatusCode -in $expected) {
            return
        }
    }
    throw $failure
}

function Assert-NoPathLeak([object] $value, [string] $context) {
    $serialized = $value | ConvertTo-Json -Depth 20 -Compress
    $uniqueRootName = [IO.Path]::GetFileName($testRoot)
    if ($serialized -like "*$uniqueRootName*" -or
        $serialized -match '"(?:sourcePath|dataPath|manifestPath|directory|backupRoot|worldRoot)"\s*:') {
        throw "$context exposed a server filesystem path."
    }
}

function Write-FixtureFile([string] $path, [string] $content) {
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($content))
}

function Get-Sha256Hex([string] $path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash($stream)
        return ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Remove-TestTree {
    $separators = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd($separators)
    $fullTarget = [IO.Path]::GetFullPath($testRoot)
    $leaf = [IO.Path]::GetFileName($fullTarget)
    if (-not $fullTarget.StartsWith(
            $fullTemp + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith("pal-control-save-backup-smoke-", [StringComparison]::Ordinal)) {
        throw "Refusing to remove an unexpected test directory: $fullTarget"
    }
    Remove-Item -LiteralPath $fullTarget -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    & dotnet build $project --no-restore --output $buildDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Control API build failed."
    }

    New-Item -ItemType Directory -Force -Path @(
        $configRoot,
        $playersRoot,
        $backupRoot,
        $dataDirectory
    ) | Out-Null
    Write-FixtureFile (Join-Path $configRoot "GameUserSettings.ini") @"
[/Script/Pal.PalGameLocalSettings]
DedicatedServerName=$worldGuid
"@
    Write-FixtureFile (Join-Path $configRoot "PalWorldSettings.ini") @"
[/Script/Pal.PalGameWorldSettings]
OptionSettings=(ServerName="Fake Palworld",ServerDescription="save smoke")
"@
    Write-FixtureFile (Join-Path $installRoot "PalServer.exe") "disposable test executable marker"
    Write-FixtureFile (Join-Path $worldRoot "Level.sav") "stable-world-level-v1"
    Write-FixtureFile (Join-Path $worldRoot "LevelMeta.sav") "stable-world-metadata-v1"
    Write-FixtureFile (Join-Path $worldRoot "LocalData.sav") "stable-local-data-v1"
    Write-FixtureFile (Join-Path $playersRoot "00000000000000000000000000000001.sav") `
        "stable-player-one-v1"

    $nativeSeed = Join-Path $worldRoot "backup\world\2026.07.11-10.00.00"
    New-Item -ItemType Directory -Force -Path (Join-Path $nativeSeed "Players") | Out-Null
    Copy-Item -LiteralPath (Join-Path $worldRoot "Level.sav") -Destination $nativeSeed
    Copy-Item -LiteralPath (Join-Path $worldRoot "LevelMeta.sav") -Destination $nativeSeed
    Copy-Item -LiteralPath (Join-Path $playersRoot "00000000000000000000000000000001.sav") `
        -Destination (Join-Path $nativeSeed "Players")

    $fake = Start-Process -FilePath "python" -ArgumentList @(
        $fakeScript,
        "--port", $fakePort,
        "--save-root", $worldRoot,
        "--world-guid", $worldGuid
    ) -PassThru -WindowStyle Hidden -RedirectStandardOutput $fakeStdout `
        -RedirectStandardError $fakeStderr
    Wait-ForEndpoint "http://127.0.0.1:$fakePort/__state" 50 $fake | Out-Null

    $env:Urls = $baseUri
    $env:Palworld__InstallRoot = $installRoot
    $env:Palworld__OfficialRestApi__BaseUrl = "http://127.0.0.1:$fakePort/v1/api/"
    $env:Palworld__OfficialRestApi__Username = "admin"
    $env:Palworld__OfficialRestApi__Password = "test-password"
    $env:Palworld__OfficialRestApi__TimeoutSeconds = "1"
    $env:Palworld__Bridge__PipeName = "pal-control.save-backup-smoke.unavailable"
    $env:CommandPersistence__DataDirectory = $dataDirectory
    $env:ExtractionMode__Persistence__DataDirectory = Join-Path $dataDirectory "extraction"
    $env:SaveManagement__BackupRoot = $backupRoot
    $env:SaveManagement__RequireRunningProcess = "false"
    $env:SaveManagement__SnapshotTimeoutSeconds = "2"
    $env:SaveManagement__StabilitySampleMilliseconds = "100"
    $env:SaveManagement__StabilityRequiredSamples = "2"
    $env:SaveManagement__MinimumFreeSpaceBytes = "16777216"

    $api = Start-TestApi
    Wait-ForEndpoint "$baseUri/health/live" 100 $api | Out-Null

    $statusUri = "$baseUri/api/v1/servers/local/saves/status"
    $backupUri = "$baseUri/api/v1/servers/local/backups"
    $flushUri = "$baseUri/api/v1/servers/local/saves/flush"
    $status = Invoke-RestMethod $statusUri -TimeoutSec 3
    if (-not $status.ready -or
        $status.worldGuid -ne $worldGuid -or
        $status.worldName -ne "Fake Palworld" -or
        $status.save.fileCount -lt 4 -or
        $status.save.playerFileCount -ne 1 -or
        $status.save.totalBytes -le 0 -or
        $status.nativeBackups.count -lt 1 -or
        $status.nativeBackups.totalBytes -le 0 -or
        $status.managedBackups.count -ne 0) {
        throw "Initial save status was incorrect: $($status | ConvertTo-Json -Depth 10 -Compress)"
    }
    Assert-NoPathLeak $status "Save status"

    $nativeBackups = Invoke-RestMethod "$backupUri`?kind=native" -TimeoutSec 3
    if (@($nativeBackups.items).Count -lt 1 -or
        @($nativeBackups.items | Where-Object kind -ne "native").Count -ne 0 -or
        @($nativeBackups.items | Where-Object consistency -ne "native").Count -ne 0) {
        throw "Native backup listing was incorrect."
    }
    Assert-NoPathLeak $nativeBackups "Native backup listing"

    $flushKey = "save-flush-smoke-0001"
    $flushBody = @{ reason = "Integration test world flush" }
    Assert-BadWrite $flushUri "save-flush-invalid-0001" @{ reason = "x" } `
        "An invalid flush reason did not return HTTP 400."
    $flushSubmission = Invoke-JsonPostResponse $flushUri $flushKey $flushBody
    if ($flushSubmission.statusCode -ne 202) {
        throw "A new flush command did not return HTTP 202."
    }
    $flush = Wait-SaveCommand $flushSubmission.body
    if ($flush.type -ne "flush" -or
        $flush.state -ne "succeeded" -or
        $flush.stage -ne "completed" -or
        $flush.result.httpStatus -ne 200) {
        throw "World flush did not succeed: $($flush | ConvertTo-Json -Depth 10 -Compress)"
    }
    $fakeAfterFlush = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($fakeAfterFlush.saveCount -ne 1) {
        throw "Official REST save was not called exactly once for the flush."
    }
    $flushReplayResponse = Invoke-JsonPostResponse $flushUri $flushKey $flushBody
    if ($flushReplayResponse.statusCode -ne 200) {
        throw "A flush idempotency replay did not return HTTP 200."
    }
    $flushReplay = $flushReplayResponse.body
    if ($flushReplay.commandId -ne $flush.commandId -or $flushReplay.state -ne "succeeded") {
        throw "Flush idempotency replay did not return the original command."
    }
    $fakeAfterFlushReplay = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($fakeAfterFlushReplay.saveCount -ne 1) {
        throw "Flush idempotency replay called official REST again."
    }
    Assert-Conflict $flushUri $flushKey `
        @{ reason = "Different payload for the same key" } `
        "A flush idempotency key was accepted with a different request payload."

    $createKey = "save-backup-smoke-0001"
    $createBody = @{
        label = "Before integration upgrade"
        reason = "Verify stable snapshot backup workflow"
    }
    Assert-BadWrite $backupUri "save-backup-invalid-0001" `
        @{ label = ""; reason = $createBody.reason } `
        "An invalid backup label did not return HTTP 400."
    $createSubmission = Invoke-JsonPostResponse $backupUri $createKey $createBody
    if ($createSubmission.statusCode -ne 202) {
        throw "A new backup command did not return HTTP 202."
    }
    $created = Wait-SaveCommand $createSubmission.body
    if ($created.type -ne "create-backup" -or
        $created.state -ne "succeeded" -or
        $created.stage -ne "completed" -or
        [string]::IsNullOrWhiteSpace([string]$created.backupId) -or
        $created.result.backup.integrity -ne "verified" -or
        $created.result.backup.consistency -ne "stable" -or
        [string]$created.result.backup.manifestSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Managed backup did not complete with verified integrity: $($created | ConvertTo-Json -Depth 12 -Compress)"
    }
    $fakeAfterBackup = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($fakeAfterBackup.saveCount -ne 2) {
        throw "Managed backup did not invoke one additional official REST save."
    }
    $createReplayResponse = Invoke-JsonPostResponse $backupUri $createKey $createBody
    if ($createReplayResponse.statusCode -ne 200) {
        throw "A backup idempotency replay did not return HTTP 200."
    }
    $createReplay = $createReplayResponse.body
    if ($createReplay.commandId -ne $created.commandId -or
        $createReplay.backupId -ne $created.backupId) {
        throw "Managed backup idempotency replay did not return the original command."
    }
    if ((Invoke-RestMethod "http://127.0.0.1:$fakePort/__state").saveCount -ne 2) {
        throw "Managed backup idempotency replay called official REST again."
    }
    Assert-Conflict $backupUri $createKey `
        @{ label = "Different label"; reason = $createBody.reason } `
        "A backup idempotency key was accepted with a different request payload."

    $managed = Invoke-RestMethod "$backupUri`?kind=managed" -TimeoutSec 3
    $detail = Invoke-RestMethod "$backupUri/$($created.backupId)" -TimeoutSec 3
    if (@($managed.items).Count -ne 1 -or
        $detail.backupId -ne $created.backupId -or
        $detail.fileCount -lt 3 -or
        $detail.totalBytes -le 0) {
        throw "Managed backup list or detail was incorrect."
    }
    Assert-NoPathLeak @($created, $managed, $detail) "Managed backup API"
    Assert-HttpStatus "Post" "$backupUri/$($created.backupId)/restore" @(404, 405) `
        "The v1 API unexpectedly exposed backup restore."
    Assert-HttpStatus "Delete" "$backupUri/$($created.backupId)" @(404, 405) `
        "The v1 API unexpectedly exposed backup deletion."
    Assert-HttpStatus "Post" "$backupUri/upload" @(404, 405) `
        "The v1 API unexpectedly exposed backup upload."

    $managedDirectory = Join-Path $backupRoot "local\$($created.backupId)"
    $manifestPath = Join-Path $managedDirectory "manifest.json"
    $verificationPath = Join-Path $managedDirectory "verification.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Managed backup manifest was not published."
    }
    if (-not (Test-Path -LiteralPath $verificationPath -PathType Leaf)) {
        throw "Managed backup verification sidecar was not published."
    }
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    if ($manifestText -like "*$([IO.Path]::GetFileName($testRoot))*" -or
        $manifestText -match '(?:[A-Za-z]:\\|\.\.[/\\])') {
        throw "Managed manifest contains an absolute or parent-relative path."
    }
    $copiedLevel = Join-Path $managedDirectory "data\Level.sav"
    if (-not (Test-Path -LiteralPath $copiedLevel -PathType Leaf)) {
        throw "Managed backup did not contain Level.sav."
    }
    $copiedLevelHash = Get-Sha256Hex $copiedLevel
    if ($manifestText.ToLowerInvariant() -notlike "*$copiedLevelHash*") {
        throw "Managed manifest does not contain the copied Level.sav SHA-256."
    }

    $verifyUri = "$backupUri/$($created.backupId)/verify"
    Assert-BadWrite $verifyUri "save-verify-invalid-0001" @{ reason = "x" } `
        "An invalid verification reason did not return HTTP 400."
    $verifySubmission = Invoke-JsonPostResponse $verifyUri `
        "save-verify-smoke-0001" @{ reason = "Scheduled integrity verification" }
    if ($verifySubmission.statusCode -ne 202) {
        throw "A new verification command did not return HTTP 202."
    }
    $verified = Wait-SaveCommand $verifySubmission.body
    if ($verified.type -ne "verify-backup" -or
        $verified.state -ne "succeeded" -or
        $verified.result.backup.integrity -ne "verified") {
        throw "Untampered managed backup verification did not succeed."
    }
    Assert-Conflict $verifyUri "save-verify-smoke-0001" `
        @{ reason = "Different verification payload" } `
        "A verification idempotency key was accepted with a different request payload."

    [IO.File]::AppendAllText($copiedLevel, "tampered")
    $tampered = Wait-SaveCommand (Invoke-JsonPost $verifyUri `
        "save-verify-smoke-0002" @{ reason = "Detect deliberate integration-test tamper" })
    if ($tampered.state -ne "failed" -or
        $tampered.stage -ne "completed" -or
        $tampered.error.code -ne "BACKUP_INTEGRITY_FAILED" -or
        $tampered.result.backup.integrity -ne "failed") {
        throw "Tampered backup was not reported as an integrity failure: $($tampered | ConvertTo-Json -Depth 12 -Compress)"
    }
    $managedAfterTamper = Invoke-RestMethod "$backupUri`?kind=managed" -TimeoutSec 3
    $listedAfterTamper = @($managedAfterTamper.items) |
        Where-Object backupId -eq $created.backupId |
        Select-Object -First 1
    $detailAfterTamper = Invoke-RestMethod "$backupUri/$($created.backupId)" -TimeoutSec 3
    $statusAfterTamper = Invoke-RestMethod $statusUri -TimeoutSec 3
    if ($null -eq $listedAfterTamper -or
        $listedAfterTamper.integrity -ne "failed" -or
        $detailAfterTamper.integrity -ne "failed") {
        throw "Managed backup list or detail did not retain the failed integrity state."
    }
    if ($statusAfterTamper.managedBackups.verifiedCount -ne 0) {
        throw "Save status still counted the tampered managed backup as verified."
    }
    Assert-NoPathLeak @($managedAfterTamper, $detailAfterTamper, $statusAfterTamper) `
        "Tampered managed backup API"
    $anchorBeforeManifestTamper = (Get-Content -Raw -LiteralPath $verificationPath |
        ConvertFrom-Json).manifestSha256
    [IO.File]::AppendAllText($manifestPath, [Environment]::NewLine)
    $manifestTampered = Wait-SaveCommand (Invoke-JsonPost $verifyUri `
        "save-verify-smoke-0003" @{ reason = "Reject a modified manifest anchor" })
    $anchorAfterManifestTamper = (Get-Content -Raw -LiteralPath $verificationPath |
        ConvertFrom-Json).manifestSha256
    if ($manifestTampered.state -ne "failed" -or
        $manifestTampered.result.backup.integrity -ne "failed" -or
        $anchorAfterManifestTamper -ne $anchorBeforeManifestTamper) {
        throw "A modified manifest was re-anchored instead of remaining an integrity failure."
    }
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$fakePort/__save/mode" `
        -ContentType "application/json" -Body '{"mode":"no-snapshot"}' -TimeoutSec 2 | Out-Null
    $noSnapshot = Wait-SaveCommand (Invoke-JsonPost $backupUri `
        "save-backup-no-snapshot-0001" @{
            label = "Must not publish"
            reason = "Exercise missing post-save snapshot handling"
        })
    if ($noSnapshot.state -ne "uncertain" -or $noSnapshot.stage -ne "completed") {
        throw "Missing post-save snapshot was not marked uncertain: $($noSnapshot | ConvertTo-Json -Depth 10 -Compress)"
    }
    if (@((Invoke-RestMethod "$backupUri`?kind=managed").items).Count -ne 1) {
        throw "A managed backup was published without a new stable native snapshot."
    }

    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$fakePort/__save/mode" `
        -ContentType "application/json" -Body '{"mode":"uncertain"}' -TimeoutSec 2 | Out-Null
    $uncertainKey = "save-flush-uncertain-0001"
    $uncertainBody = @{ reason = "Exercise official REST lost response" }
    $uncertain = Wait-SaveCommand (Invoke-JsonPost $flushUri $uncertainKey $uncertainBody)
    if ($uncertain.state -ne "uncertain" -or $uncertain.stage -ne "completed") {
        throw "Lost official REST response was not marked uncertain."
    }
    $saveCountBeforeRestart = (Invoke-RestMethod `
        "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2).saveCount

    $audit = Invoke-RestMethod "$baseUri/api/v1/audit/save-commands?limit=200" -TimeoutSec 3
    $createStates = @(
        $audit.items |
            Where-Object commandId -eq $created.commandId |
            Select-Object -ExpandProperty state
    )
    foreach ($requiredState in @("accepted", "dispatched", "succeeded")) {
        if ($requiredState -notin $createStates) {
            throw "Managed backup audit is missing state '$requiredState'."
        }
    }
    Assert-NoPathLeak $audit "Save command audit"

    Stop-Process -Id $api.Id -Force
    Wait-Process -Id $api.Id -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    $api = Start-TestApi
    Wait-ForEndpoint "$baseUri/health/live" 100 $api | Out-Null

    $replayAfterRestart = Invoke-JsonPost $backupUri $createKey $createBody
    if ($replayAfterRestart.commandId -ne $created.commandId -or
        $replayAfterRestart.state -ne "succeeded") {
        throw "Restart-safe backup idempotency did not return the original command."
    }
    $uncertainAfterRestart = Invoke-JsonPost $flushUri $uncertainKey $uncertainBody
    if ($uncertainAfterRestart.commandId -ne $uncertain.commandId -or
        $uncertainAfterRestart.state -ne "uncertain") {
        throw "Restart-safe uncertain command replay did not return the original outcome."
    }
    Start-Sleep -Milliseconds 300
    $fakeAfterRestart = Invoke-RestMethod "http://127.0.0.1:$fakePort/__state" -TimeoutSec 2
    if ($fakeAfterRestart.saveCount -ne $saveCountBeforeRestart) {
        throw "Restart replay resent a completed or uncertain official REST save."
    }

    [pscustomobject]@{
        ready = $status.ready
        activeFiles = $status.save.fileCount
        playerFiles = $status.save.playerFileCount
        nativeBackups = @($nativeBackups.items).Count
        flushState = $flush.state
        backupId = $created.backupId
        backupState = $created.state
        manifestVerified = $true
        verifyState = $verified.state
        tamperState = $tampered.state
        noSnapshotState = $noSnapshot.state
        uncertainState = $uncertain.state
        auditStates = $createStates -join ","
        restartReplayState = $replayAfterRestart.state
        pathLeaked = $false
        officialSaveCount = $fakeAfterRestart.saveCount
    } | ConvertTo-Json -Compress
}
catch {
    if (Test-Path -LiteralPath $stdout) {
        Write-Host (Get-Content -Raw -LiteralPath $stdout)
    }
    if (Test-Path -LiteralPath $stderr) {
        Write-Host (Get-Content -Raw -LiteralPath $stderr)
    }
    if (Test-Path -LiteralPath $fakeStdout) {
        Write-Host (Get-Content -Raw -LiteralPath $fakeStdout)
    }
    if (Test-Path -LiteralPath $fakeStderr) {
        Write-Host (Get-Content -Raw -LiteralPath $fakeStderr)
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
    Remove-TestTree
}
