$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot "tools\world-restore\PalControl.WorldRestore.csproj"
$assets = Join-Path (Split-Path -Parent $project) "obj\project.assets.json"
if (-not (Test-Path -LiteralPath $assets -PathType Leaf)) {
    & dotnet restore $project --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "World restore tool restore failed with exit code $LASTEXITCODE."
    }
}
& dotnet build $project --configuration Release --no-restore --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "World restore tool build failed with exit code $LASTEXITCODE."
}
$tool = Join-Path (Split-Path -Parent $project) `
    "bin\Release\net10.0\PalControl.WorldRestore.dll"

$fixtureRoots = [Collections.Generic.List[string]]::new()
$junctions = [Collections.Generic.List[string]]::new()
$childProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$fixtureProcess = $null

function Get-Sha256Hex([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream)) `
                -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-FixtureFileSnapshot([string] $Root) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    return @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Force -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($fullRoot.Length)
            $relativePath = $relativePath.TrimStart(
                [char[]]@('\', '/')).Replace('\', '/')
            "{0}|{1}|{2}|{3}" -f `
                $relativePath,
                $_.Length,
                $_.LastWriteTimeUtc.Ticks,
                (Get-Sha256Hex $_.FullName)
        })
}

function Write-Utf8Json([string] $Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Write-Utf8Text([string] $Path, [string] $Value) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-WorldRestoreTool([string[]] $Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& dotnet $tool @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "World restore tool failed unexpectedly: $($output -join [Environment]::NewLine)"
    }
    try {
        return (($output | ForEach-Object ToString) -join [Environment]::NewLine) |
            ConvertFrom-Json
    }
    catch {
        throw "World restore tool did not return JSON: $($output -join [Environment]::NewLine)"
    }
}

function Invoke-WorldRestoreFailure([string[]] $Arguments, [string] $Description) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& dotnet $tool @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -eq 0) {
        throw "$Description unexpectedly succeeded: $($output -join [Environment]::NewLine)"
    }
    return (($output | ForEach-Object ToString) -join [Environment]::NewLine)
}

function Start-WorldRestoreChild(
    [string[]] $Arguments,
    [string] $FixtureRoot,
    [string] $PauseState = "",
    [int] $HoldLockMilliseconds = 0) {
    $stdout = Join-Path $FixtureRoot `
        ("child-{0}.stdout" -f [Guid]::NewGuid().ToString("N"))
    $stderr = Join-Path $FixtureRoot `
        ("child-{0}.stderr" -f [Guid]::NewGuid().ToString("N"))
    $env:PALCONTROL_WORLD_RESTORE_TEST_ROOT = $FixtureRoot
    if (-not [string]::IsNullOrWhiteSpace($PauseState)) {
        $env:PALCONTROL_WORLD_RESTORE_TEST_PAUSE_AT = $PauseState
    }
    if ($HoldLockMilliseconds -gt 0) {
        $env:PALCONTROL_WORLD_RESTORE_TEST_HOLD_LOCK_MS = [string]$HoldLockMilliseconds
    }
    try {
        $process = Start-Process -FilePath "dotnet" `
            -ArgumentList (@($tool) + $Arguments) `
            -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $process | Add-Member -NotePropertyName FixtureStdout -NotePropertyValue $stdout
        $process | Add-Member -NotePropertyName FixtureStderr -NotePropertyValue $stderr
        $childProcesses.Add($process)
        return $process
    }
    finally {
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_PAUSE_AT -ErrorAction SilentlyContinue
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_HOLD_LOCK_MS -ErrorAction SilentlyContinue
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_ROOT -ErrorAction SilentlyContinue
    }
}

function New-WorldRestoreFixture([string] $Name) {
    $root = Join-Path $env:TEMP `
        ("palcontrol-world-restore-smoke-{0}-{1}" -f $Name, [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root | Out-Null
    $fixtureRoots.Add($root)
    $install = Join-Path $root "PalServer"
    New-Item -ItemType Directory -Path $install | Out-Null
    $palServer = Join-Path $install "PalServer.exe"
    Copy-Item -LiteralPath (Get-Process -Id $PID).Path -Destination $palServer

    $worldGuid = [Guid]::NewGuid().ToString("N").ToUpperInvariant()
    $settings = Join-Path $install `
        "Pal\Saved\Config\WindowsServer\GameUserSettings.ini"
    Write-Utf8Text $settings "[/Script/Pal.PalGameLocalSettings]`nDedicatedServerName=$worldGuid`n"
    $active = Join-Path $install "Pal\Saved\SaveGames\0\$worldGuid"
    Write-Utf8Text (Join-Path $active "Level.sav") "old-active-level-$Name"
    Write-Utf8Text (Join-Path $active "Players\old-player.sav") "old-player-$Name"
    New-Item -ItemType Directory -Path (Join-Path $active "EmptyWorldDirectory") | Out-Null

    $backupId = [Guid]::NewGuid().ToString("N")
    $backup = Join-Path $root "managed-backups\local\$backupId"
    $data = Join-Path $backup "data"
    $level = Join-Path $data "Level.sav"
    $player = Join-Path $data "Players\restored-player.sav"
    Write-Utf8Text $level "restored-level-$Name"
    Write-Utf8Text $player "restored-player-$Name"
    $timestamp = [DateTimeOffset]::UtcNow.AddMinutes(-2).ToString("O")
    $files = @(
        [ordered]@{
            relativePath = "Level.sav"
            length = [IO.FileInfo]::new($level).Length
            lastModifiedAt = $timestamp
            sha256 = Get-Sha256Hex $level
        },
        [ordered]@{
            relativePath = "Players/restored-player.sav"
            length = [IO.FileInfo]::new($player).Length
            lastModifiedAt = $timestamp
            sha256 = Get-Sha256Hex $player
        }
    )
    $manifestPath = Join-Path $backup "manifest.json"
    $manifest = [ordered]@{
        schemaVersion = 1
        backupId = $backupId
        serverId = "local"
        label = "Synthetic restore fixture"
        worldGuid = $worldGuid
        gameVersion = "fixture-1.0"
        createdAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString("O")
        actor = "fixture-admin"
        reason = "Exercise offline world restoration without real saves"
        integrity = "verified"
        consistency = "stable"
        files = $files
    }
    Write-Utf8Json $manifestPath $manifest
    $verificationPath = Join-Path $backup "verification.json"
    Write-Utf8Json $verificationPath ([ordered]@{
        schemaVersion = 1
        backupId = $backupId
        integrity = "verified"
        verifiedAt = [DateTimeOffset]::UtcNow.ToString("O")
        manifestSha256 = Get-Sha256Hex $manifestPath
    })
    $evidence = Join-Path $root "evidence"
    New-Item -ItemType Directory -Path $evidence | Out-Null
    return [pscustomobject]@{
        Name = $Name
        Root = $root
        InstallRoot = $install
        PalServerExecutable = $palServer
        SettingsFile = $settings
        WorldGuid = $worldGuid
        ActiveWorld = $active
        BackupId = $backupId
        Backup = $backup
        Data = $data
        ManifestPath = $manifestPath
        VerificationPath = $verificationPath
        Evidence = $evidence
        OldLevel = "old-active-level-$Name"
        RestoredLevel = "restored-level-$Name"
    }
}

function New-RestorePlan($Fixture, [string] $Pin = $authority.Pin) {
    return Invoke-WorldRestoreTool @(
        "plan",
        "--backup-dir", $Fixture.Backup,
        "--active-world-dir", $Fixture.ActiveWorld,
        "--server-id", "local",
        "--world-guid", $Fixture.WorldGuid,
        "--settings-file", $Fixture.SettingsFile,
        "--palserver-executable", $Fixture.PalServerExecutable,
        "--evidence-dir", $Fixture.Evidence,
        "--trust-store-sha256", $Pin)
}

function New-Approvals(
    $Fixture,
    $Plan,
    [string] $Pin = $authority.Pin,
    [string] $Trust = $authority.Trust) {
    $approvalA = Join-Path $Fixture.Evidence "approval-a.json"
    $approvalB = Join-Path $Fixture.Evidence "approval-b.json"
    Invoke-WorldRestoreTool @(
        "approve", "--plan-file", $Plan.planPath,
        "--subject", "ops-a",
        "--reason", "Approve verified synthetic world restore",
        "--private-key-file", $authority.PrivateA,
        "--output-file", $approvalA,
        "--trust-store-sha256", $Pin,
        "--valid-for-minutes", "10") | Out-Null
    Invoke-WorldRestoreTool @(
        "approve", "--plan-file", $Plan.planPath,
        "--subject", "ops-b",
        "--reason", "Independently approve synthetic world restore",
        "--private-key-file", $authority.PrivateB,
        "--output-file", $approvalB,
        "--trust-store-sha256", $Pin,
        "--valid-for-minutes", "10") | Out-Null
    return [pscustomobject]@{
        Trust = $Trust
        Pin = $Pin
        PrivateA = $authority.PrivateA
        ApprovalA = $approvalA
        ApprovalB = $approvalB
    }
}

function New-ExternalApproverAuthority {
    $root = Join-Path $env:TEMP `
        ("palcontrol-world-restore-smoke-authority-{0}" -f [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root | Out-Null
    $fixtureRoots.Add($root)
    $privateA = Join-Path $root "ops-a-private.pem"
    $publicA = Join-Path $root "ops-a-public.pem"
    $privateB = Join-Path $root "ops-b-private.pem"
    $publicB = Join-Path $root "ops-b-public.pem"
    Invoke-WorldRestoreTool @(
        "keygen", "--private-key-file", $privateA, "--public-key-file", $publicA) | Out-Null
    Invoke-WorldRestoreTool @(
        "keygen", "--private-key-file", $privateB, "--public-key-file", $publicB) | Out-Null
    $trust = Join-Path $root "externally-approved-trusted-approvers.json"
    Write-Utf8Json $trust ([ordered]@{
        schemaVersion = 1
        keys = @(
            [ordered]@{
                subject = "ops-a"
                algorithm = "ecdsa-p256-sha256"
                publicKeyPem = [IO.File]::ReadAllText($publicA)
            },
            [ordered]@{
                subject = "ops-b"
                algorithm = "ecdsa-p256-sha256"
                publicKeyPem = [IO.File]::ReadAllText($publicB)
            }
        )
    })
    return [pscustomobject]@{
        Root = $root
        Trust = $trust
        Pin = Get-Sha256Hex $trust
        PrivateA = $privateA
        PrivateB = $privateB
        PublicA = $publicA
        PublicB = $publicB
    }
}

function New-RecoveryApprovals(
    $Fixture,
    $Plan,
    [string] $Name = "recovery",
    [string] $SubjectA = "ops-a",
    [string] $SubjectB = "ops-b",
    [int] $AgeA = 0) {
    $approvalA = Join-Path $Fixture.Evidence "$Name-a.json"
    $approvalB = Join-Path $Fixture.Evidence "$Name-b.json"
    $keyA = if ($SubjectA -eq "ops-b") { $authority.PrivateB } else { $authority.PrivateA }
    $keyB = if ($SubjectB -eq "ops-b") { $authority.PrivateB } else { $authority.PrivateA }
    if ($AgeA -gt 0) {
        $env:PALCONTROL_WORLD_RESTORE_TEST_ROOT = $Fixture.Root
        $env:PALCONTROL_WORLD_RESTORE_TEST_RECOVERY_APPROVAL_AGE_MINUTES = [string]$AgeA
    }
    try {
        Invoke-WorldRestoreTool @(
            "approve-recovery", "--plan-file", $Plan.planPath,
            "--subject", $SubjectA,
            "--reason", "Approve exact durable recovery journal as first reviewer",
            "--private-key-file", $keyA,
            "--output-file", $approvalA,
            "--trust-store-sha256", $authority.Pin,
            "--valid-for-minutes", "10") | Out-Null
    }
    finally {
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_RECOVERY_APPROVAL_AGE_MINUTES `
            -ErrorAction SilentlyContinue
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_ROOT -ErrorAction SilentlyContinue
    }
    Invoke-WorldRestoreTool @(
        "approve-recovery", "--plan-file", $Plan.planPath,
        "--subject", $SubjectB,
        "--reason", "Independently approve exact durable recovery journal",
        "--private-key-file", $keyB,
        "--output-file", $approvalB,
        "--trust-store-sha256", $authority.Pin,
        "--valid-for-minutes", "10") | Out-Null
    return [pscustomobject]@{
        ApprovalA = $approvalA
        ApprovalB = $approvalB
    }
}

function Invoke-Recovery($Plan, $Approvals, [string] $Pin = $authority.Pin) {
    return Invoke-WorldRestoreTool @(
        "recover", "--plan-file", $Plan.planPath,
        "--trust-store-sha256", $Pin,
        "--approval-file", $Approvals.ApprovalA,
        "--approval-file", $Approvals.ApprovalB)
}

$authority = New-ExternalApproverAuthority

try {
    # A plan cannot silently derive its trust anchor from a local trust file.
    # The only accepted root is an explicit externally published lowercase hash.
    $missingPin = New-WorldRestoreFixture "missing-external-pin"
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $missingPin.Backup,
        "--active-world-dir", $missingPin.ActiveWorld,
        "--server-id", "local", "--world-guid", $missingPin.WorldGuid,
        "--settings-file", $missingPin.SettingsFile,
        "--palserver-executable", $missingPin.PalServerExecutable,
        "--evidence-dir", $missingPin.Evidence) "plan without external trust-store pin"
    Assert-True ($errorText -match "Required option '--trust-store-sha256' is missing") `
        "Plan creation derived or omitted its external trust-store pin."
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $missingPin.Backup,
        "--active-world-dir", $missingPin.ActiveWorld,
        "--server-id", "local", "--world-guid", $missingPin.WorldGuid,
        "--settings-file", $missingPin.SettingsFile,
        "--palserver-executable", $missingPin.PalServerExecutable,
        "--evidence-dir", $missingPin.Evidence,
        "--trust-store-sha256", ($authority.Pin.ToUpperInvariant())) `
        "plan with a non-canonical external pin"
    Assert-True ($errorText -match "64 lowercase hex") `
        "A non-canonical external trust-store pin was accepted."

    # Even an externally pinned byte sequence is rejected if a nested key uses
    # duplicate JSON properties; System.Text.Json last-wins behavior is not a
    # valid trust-root parser policy.
    $duplicateTrustFixture = New-WorldRestoreFixture "duplicate-trust-property"
    $duplicateTrust = Join-Path $duplicateTrustFixture.Evidence "duplicate-trust-store.json"
    $duplicateTrustText = [IO.File]::ReadAllText($authority.Trust) -replace `
        '"subject"\s*:\s*"ops-a"',
        '"subject":"ops-a","subject":"ops-a"'
    [IO.File]::WriteAllText(
        $duplicateTrust,
        $duplicateTrustText,
        [Text.UTF8Encoding]::new($false))
    $duplicateTrustPin = Get-Sha256Hex $duplicateTrust
    $duplicateTrustPlan = New-RestorePlan $duplicateTrustFixture $duplicateTrustPin
    $duplicateTrustApprovals = New-Approvals `
        $duplicateTrustFixture $duplicateTrustPlan $duplicateTrustPin $duplicateTrust
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $duplicateTrustPlan.planPath, "--execute",
        "--trust-store", $duplicateTrust,
        "--trust-store-sha256", $duplicateTrustPin,
        "--approval-file", $duplicateTrustApprovals.ApprovalA,
        "--approval-file", $duplicateTrustApprovals.ApprovalB) `
        "nested duplicate trust-store property"
    Assert-True ($errorText -match "missing, duplicate, or unknown fields") `
        "A nested duplicate trust-store property reached last-wins deserialization."

    # The verification sidecar anchors the exact manifest bytes; harmless-looking
    # whitespace changes cannot silently create a new trust root.
    $anchor = New-WorldRestoreFixture "anchor"
    [IO.File]::AppendAllText($anchor.ManifestPath, [Environment]::NewLine)
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $anchor.Backup,
        "--active-world-dir", $anchor.ActiveWorld,
        "--server-id", "local", "--world-guid", $anchor.WorldGuid,
        "--settings-file", $anchor.SettingsFile,
        "--palserver-executable", $anchor.PalServerExecutable,
        "--evidence-dir", $anchor.Evidence,
        "--trust-store-sha256", $authority.Pin) "modified manifest anchor"
    Assert-True ($errorText -match "manifest or verification anchor is ineligible") `
        "A modified manifest was accepted without its independent verification anchor."

    # Trust-chain tamper must fail without producing a plan.
    $tamper = New-WorldRestoreFixture "tamper"
    [IO.File]::AppendAllText((Join-Path $tamper.Data "Level.sav"), "tampered")
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $tamper.Backup,
        "--active-world-dir", $tamper.ActiveWorld,
        "--server-id", "local", "--world-guid", $tamper.WorldGuid,
        "--settings-file", $tamper.SettingsFile,
        "--palserver-executable", $tamper.PalServerExecutable,
        "--evidence-dir", $tamper.Evidence,
        "--trust-store-sha256", $authority.Pin) "tampered managed backup"
    Assert-True ($errorText -match "managed backup data") `
        "Tampered managed backup did not fail its file hash/length inventory."

    # Re-anchoring an unsafe manifest must still fail before any copy.
    $escape = New-WorldRestoreFixture "escape"
    $escapeManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $escape.ManifestPath |
        ConvertFrom-Json
    $escapeManifest.files[0].relativePath = "../escape.sav"
    Write-Utf8Json $escape.ManifestPath $escapeManifest
    $escapeVerification = Get-Content -Raw -Encoding UTF8 -LiteralPath $escape.VerificationPath |
        ConvertFrom-Json
    $escapeVerification.manifestSha256 = Get-Sha256Hex $escape.ManifestPath
    Write-Utf8Json $escape.VerificationPath $escapeVerification
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $escape.Backup,
        "--active-world-dir", $escape.ActiveWorld,
        "--server-id", "local", "--world-guid", $escape.WorldGuid,
        "--settings-file", $escape.SettingsFile,
        "--palserver-executable", $escape.PalServerExecutable,
        "--evidence-dir", $escape.Evidence,
        "--trust-store-sha256", $authority.Pin) "path-escaping manifest"
    Assert-True ($errorText -match "(?i)unsafe.*(?:relative|manifest path)") `
        "A parent-relative manifest entry was not rejected."

    # Undeclared filesystem material is rejected, including empty extras.
    $extra = New-WorldRestoreFixture "extra"
    Write-Utf8Text (Join-Path $extra.Data "undeclared.sav") "undeclared"
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $extra.Backup,
        "--active-world-dir", $extra.ActiveWorld,
        "--server-id", "local", "--world-guid", $extra.WorldGuid,
        "--settings-file", $extra.SettingsFile,
        "--palserver-executable", $extra.PalServerExecutable,
        "--evidence-dir", $extra.Evidence,
        "--trust-store-sha256", $authority.Pin) "backup with an extra file"
    Assert-True ($errorText -match "file count") `
        "An undeclared backup file was not rejected."

    # Explicit server and world identities must match the managed manifest.
    $identity = New-WorldRestoreFixture "identity"
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $identity.Backup,
        "--active-world-dir", $identity.ActiveWorld,
        "--server-id", "another-server", "--world-guid", $identity.WorldGuid,
        "--settings-file", $identity.SettingsFile,
        "--palserver-executable", $identity.PalServerExecutable,
        "--evidence-dir", $identity.Evidence,
        "--trust-store-sha256", $authority.Pin) "server identity mismatch"
    Assert-True ($errorText -match "manifest or verification anchor is ineligible") `
        "A managed backup was accepted for another server identity."
    $otherWorldGuid = [Guid]::NewGuid().ToString("N").ToUpperInvariant()
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $identity.Backup,
        "--active-world-dir", $identity.ActiveWorld,
        "--server-id", "local", "--world-guid", $otherWorldGuid,
        "--settings-file", $identity.SettingsFile,
        "--palserver-executable", $identity.PalServerExecutable,
        "--evidence-dir", $identity.Evidence,
        "--trust-store-sha256", $authority.Pin) "world identity mismatch"
    Assert-True ($errorText -match "another world GUID") `
        "A managed backup was accepted for another world identity."

    # Windows junctions are available without touching any real path.
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        $reparse = New-WorldRestoreFixture "reparse"
        $target = Join-Path $reparse.Root "junction-target"
        New-Item -ItemType Directory -Path $target | Out-Null
        $junction = Join-Path $reparse.Data "linked"
        New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
        $junctions.Add($junction)
        $errorText = Invoke-WorldRestoreFailure @(
            "plan", "--backup-dir", $reparse.Backup,
            "--active-world-dir", $reparse.ActiveWorld,
            "--server-id", "local", "--world-guid", $reparse.WorldGuid,
            "--settings-file", $reparse.SettingsFile,
            "--palserver-executable", $reparse.PalServerExecutable,
            "--evidence-dir", $reparse.Evidence,
            "--trust-store-sha256", $authority.Pin) "backup containing a junction"
        Assert-True ($errorText -match "Reparse point") `
            "A junction inside managed backup data was not rejected."
        Remove-Item -LiteralPath $junction -Force
        [void]$junctions.Remove($junction)
    }

    $runningAtPlan = New-WorldRestoreFixture "running-at-plan"
    $fixtureProcess = Start-Process -FilePath $runningAtPlan.PalServerExecutable `
        -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 30") `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
    $errorText = Invoke-WorldRestoreFailure @(
        "plan", "--backup-dir", $runningAtPlan.Backup,
        "--active-world-dir", $runningAtPlan.ActiveWorld,
        "--server-id", "local", "--world-guid", $runningAtPlan.WorldGuid,
        "--settings-file", $runningAtPlan.SettingsFile,
        "--palserver-executable", $runningAtPlan.PalServerExecutable,
        "--evidence-dir", $runningAtPlan.Evidence,
        "--trust-store-sha256", $authority.Pin) "plan while PalServer is running"
    Assert-True ($errorText -match "still running") `
        "Plan creation did not require a stopped PalServer inventory."
    Stop-Process -Id $fixtureProcess.Id -Force
    $fixtureProcess.WaitForExit()
    $fixtureProcess = $null

    # The lock is a cross-process, same-volume gate. A second plan for the same
    # active world must fail while the first child owns it.
    $concurrent = New-WorldRestoreFixture "concurrent"
    $planArguments = @(
        "plan", "--backup-dir", $concurrent.Backup,
        "--active-world-dir", $concurrent.ActiveWorld,
        "--server-id", "local", "--world-guid", $concurrent.WorldGuid,
        "--settings-file", $concurrent.SettingsFile,
        "--palserver-executable", $concurrent.PalServerExecutable,
        "--evidence-dir", $concurrent.Evidence,
        "--trust-store-sha256", $authority.Pin)
    $lockHolder = Start-WorldRestoreChild `
        $planArguments $concurrent.Root "" 3000
    $lockFiles = @()
    for ($attempt = 0; $attempt -lt 50 -and $lockFiles.Count -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 50
        $lockFiles = @(Get-ChildItem -LiteralPath (Split-Path -Parent $concurrent.ActiveWorld) `
            -Filter ".palcontrol-world-restore-*.lock" -File -ErrorAction SilentlyContinue)
    }
    Assert-True ($lockFiles.Count -eq 1) `
        "First plan child did not acquire its active-world lock."
    $errorText = Invoke-WorldRestoreFailure $planArguments "concurrent restore plan"
    Assert-True ($errorText -match "exclusive active-world lock") `
        "A second process acquired the same active-world restore lock."
    $lockHolder.WaitForExit()
    $lockHolder.Refresh()
    $lockHolderResult = [IO.File]::ReadAllText($lockHolder.FixtureStdout) |
        ConvertFrom-Json
    Assert-True ($lockHolderResult.status -eq "verified") `
        "The first lock-holding plan child did not finish successfully."

    # A published plan owns a pre-created deterministic lock file. Read-only
    # status must fail closed if that lease anchor has disappeared; it must not
    # recreate the file as the old OpenOrCreate implementation did.
    $missingStatusLock = New-WorldRestoreFixture "missing-status-lock"
    $missingStatusLockPlan = New-RestorePlan $missingStatusLock
    $missingStatusLockEvidence = Get-Content -Raw -Encoding UTF8 `
        -LiteralPath $missingStatusLockPlan.planPath | ConvertFrom-Json
    Remove-Item -LiteralPath $missingStatusLockEvidence.lockFile -Force
    $errorText = Invoke-WorldRestoreFailure @(
        "status", "--plan-file", $missingStatusLockPlan.planPath) `
        "status with a missing planned lock file"
    Assert-True ($errorText -match "planned restore lock file is missing") `
        "Read-only status recreated or accepted a missing planned lock file."
    Assert-True (-not (Test-Path -LiteralPath $missingStatusLockEvidence.lockFile)) `
        "Read-only status persistently recreated its missing lease anchor."

    # An executing apply holds FileShare.None. Status must take a shared,
    # read-only lease on the existing file and therefore refuse to race the
    # mutating operation rather than observing a moving topology.
    $concurrentExecute = New-WorldRestoreFixture "status-during-execute"
    $concurrentExecutePlan = New-RestorePlan $concurrentExecute
    $concurrentExecuteApproval = New-Approvals $concurrentExecute $concurrentExecutePlan
    $concurrentExecuteArguments = @(
        "apply", "--plan-file", $concurrentExecutePlan.planPath, "--execute",
        "--trust-store", $concurrentExecuteApproval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $concurrentExecuteApproval.ApprovalA,
        "--approval-file", $concurrentExecuteApproval.ApprovalB)
    $concurrentExecuteEvidence = Get-Content -Raw -Encoding UTF8 `
        -LiteralPath $concurrentExecutePlan.planPath | ConvertFrom-Json
    $executeLockHolder = Start-WorldRestoreChild `
        $concurrentExecuteArguments $concurrentExecute.Root "" 5000
    $exclusiveExecuteLeaseObserved = $false
    for ($attempt = 0; $attempt -lt 100 -and -not $exclusiveExecuteLeaseObserved; $attempt++) {
        Start-Sleep -Milliseconds 50
        try {
            $probe = [IO.File]::Open(
                $concurrentExecuteEvidence.lockFile,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite)
            $probe.Dispose()
        }
        catch [IO.IOException] {
            $exclusiveExecuteLeaseObserved = $true
        }
    }
    Assert-True $exclusiveExecuteLeaseObserved `
        "Executing apply did not acquire its exclusive active-world lock."
    $errorText = Invoke-WorldRestoreFailure @(
        "status", "--plan-file", $concurrentExecutePlan.planPath) `
        "status while execute holds the active-world lock"
    Assert-True ($errorText -match "exclusive active-world lock") `
        "Status did not refuse a concurrent executing apply."
    $executeLockHolder.WaitForExit()
    $executeLockHolder.Refresh()
    $executeLockHolderStdout = [IO.File]::ReadAllText($executeLockHolder.FixtureStdout)
    $executeLockHolderStderr = [IO.File]::ReadAllText($executeLockHolder.FixtureStderr)
    try {
        $executeLockHolderResult = $executeLockHolderStdout | ConvertFrom-Json
    }
    catch {
        throw ("Lock-holding execute child returned invalid output after the status " +
            "contention test. stdout=$executeLockHolderStdout " +
            "stderr=$executeLockHolderStderr")
    }
    Assert-True ($executeLockHolderResult.executed -and
        $executeLockHolderResult.mode -eq "executed") `
        "Lock-holding execute child did not complete after status was refused."

    # A different 256-bit curve is not P-256. This catches regressions that
    # validate only KeySize and omit the named-curve OID.
    $curve = New-WorldRestoreFixture "curve-oid"
    $curvePlan = New-RestorePlan $curve
    $secp256k1 = Join-Path $curve.Evidence "secp256k1-private.pem"
    # Deterministic wrong-curve fixture, assembled only at runtime so the
    # repository never contains a PEM private-key block that secret scanners
    # or operators could mistake for deployable key material.
    $wrongCurveBody = @(
        "MIGEAgEAMBAGByqGSM49AgEGBSuBBAAKBG0wawIBAQQgQQVXebrKCWjVqk8410Iq",
        "nLDMKZEEbJwwwSPRvgYO0CShRANCAARx9ESzuUKH9grX+KQngCLo9yaCedk+r6M4",
        "qdiUkjeCJOvLkmOPkpm9dE4D5GiwHqMGcDKTEJXoFRZl/9iVQxt/"
    ) -join "`n"
    $privateKeyLabel = "PRIVATE" + " KEY"
    Write-Utf8Text $secp256k1 (
        "-----BEGIN $privateKeyLabel-----`n" +
        $wrongCurveBody + "`n" +
        "-----END $privateKeyLabel-----")
    $errorText = Invoke-WorldRestoreFailure @(
        "approve", "--plan-file", $curvePlan.planPath,
        "--subject", "wrong-curve",
        "--reason", "Reject a same-size non-P256 approval key",
        "--private-key-file", $secp256k1,
        "--output-file", (Join-Path $curve.Evidence "wrong-curve-approval.json"),
        "--trust-store-sha256", $authority.Pin) `
        "same-size non-P256 approval key"
    Assert-True ($errorText -match "(?i)(P-256|curve OID)") `
        "A secp256k1 key was not explicitly rejected by the P-256 curve gate."

    $emptyTamper = New-WorldRestoreFixture "empty-directory-tamper"
    $emptyTamperPlan = New-RestorePlan $emptyTamper
    New-Item -ItemType Directory -Path `
        (Join-Path $emptyTamperPlan.stagingDirectory "undeclared-empty") | Out-Null
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $emptyTamperPlan.planPath) `
        "staging with an undeclared empty directory"
    Assert-True ($errorText -match "missing or extra directories") `
        "An undeclared empty directory was omitted from full inventory validation."

    $frozenWorld = New-WorldRestoreFixture "frozen-original-world"
    $frozenPlan = New-RestorePlan $frozenWorld
    [IO.File]::AppendAllText((Join-Path $frozenWorld.ActiveWorld "Level.sav"), "changed-after-plan")
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $frozenPlan.planPath) `
        "active world changed after stopped-world plan"
    Assert-True ($errorText -match "inventory frozen in the plan") `
        "Apply accepted an active world different from the approved original inventory."

    $success = New-WorldRestoreFixture "success"
    $plan = New-RestorePlan $success
    Assert-True ($plan.mode -eq "plan-only" -and -not $plan.executed) `
        "Plan command was not explicitly plan-only."
    Assert-True (([IO.File]::ReadAllText((Join-Path $success.ActiveWorld "Level.sav"))) -eq $success.OldLevel) `
        "Plan command modified the active world."
    Assert-True (Test-Path -LiteralPath $plan.stagingDirectory -PathType Container) `
        "Plan command did not publish an isolated staging directory."
    Assert-True (([IO.File]::ReadAllText((Join-Path $plan.stagingDirectory "Level.sav"))) -eq $success.RestoredLevel) `
        "Staging directory does not contain verified managed backup bytes."
    Assert-True ((Get-Sha256Hex $plan.reportPath) -eq $plan.reportSha256) `
        "Canonical plan report SHA-256 was incorrect."
    Assert-True (([IO.File]::ReadAllBytes($plan.reportPath))[-1] -eq [byte][char]'}') `
        "Canonical plan report contains a trailing non-canonical newline."
    $planEvidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $plan.planPath |
        ConvertFrom-Json
    Assert-True ($planEvidence.schemaVersion -eq 3 -and
        $planEvidence.approverTrustStoreSha256 -eq $authority.Pin -and
        $planEvidence.checks.trustStoreExternallyPinned -and
        $planEvidence.checks.palServerStoppedAtPlan -and
        $planEvidence.originalInventory.inventorySha256.Length -eq 64 -and
        $planEvidence.planProcessGate.processNames.Count -eq 3) `
        "Plan evidence did not bind the externally published trust-store pin."

    $applyPlanOnly = Invoke-WorldRestoreTool @("apply", "--plan-file", $plan.planPath)
    Assert-True ($applyPlanOnly.mode -eq "plan-only" -and -not $applyPlanOnly.executed) `
        "Apply without --execute did not remain plan-only."
    Assert-True (([IO.File]::ReadAllText((Join-Path $success.ActiveWorld "Level.sav"))) -eq $success.OldLevel) `
        "Plan-only apply modified the active world."

    $wrongPin = "0" * 64
    if ($wrongPin -eq $authority.Pin) {
        $wrongPin = "1" * 64
    }
    $errorText = Invoke-WorldRestoreFailure @(
        "approve", "--plan-file", $plan.planPath,
        "--subject", "ops-a",
        "--reason", "Missing external pin must reject approval",
        "--private-key-file", $authority.PrivateA,
        "--output-file", (Join-Path $success.Evidence "missing-pin-approval.json")) `
        "approval without external trust-store pin"
    Assert-True ($errorText -match "Required option '--trust-store-sha256' is missing") `
        "Approval creation silently inherited rather than requiring the external pin."
    $errorText = Invoke-WorldRestoreFailure @(
        "approve", "--plan-file", $plan.planPath,
        "--subject", "ops-a",
        "--reason", "Wrong external pin must reject approval",
        "--private-key-file", $authority.PrivateA,
        "--output-file", (Join-Path $success.Evidence "wrong-pin-approval.json"),
        "--trust-store-sha256", $wrongPin) `
        "approval with a different external trust-store pin"
    Assert-True ($errorText -match "does not match the restore plan") `
        "Approval creation accepted a trust-store pin different from the plan."

    $approval = New-Approvals $success $plan
    $approvalEvidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $approval.ApprovalA |
        ConvertFrom-Json
    Assert-True ($approvalEvidence.schemaVersion -eq 3 -and
        $approvalEvidence.trustStoreSha256 -eq $authority.Pin -and
        $approvalEvidence.purpose -eq "execute" -and
        $approvalEvidence.originalInventory.inventorySha256 -eq `
            $planEvidence.originalInventory.inventorySha256) `
        "Signed approval payload omitted the external trust-store pin."

    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB) "execute without external trust-store pin"
    Assert-True ($errorText -match "requires the externally published") `
        "Execute silently derived its trust-store pin from the supplied trust file."
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--trust-store-sha256", $wrongPin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB) "execute with wrong external trust-store pin"
    Assert-True ($errorText -match "does not match the restore plan") `
        "Execute accepted a trust-store pin different from the plan."

    $swappedTrust = Join-Path $success.Evidence "swapped-trust-store.json"
    Copy-Item -LiteralPath $approval.Trust -Destination $swappedTrust
    [IO.File]::AppendAllText($swappedTrust, [Environment]::NewLine)
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $swappedTrust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB) "execute with changed trust-store bytes"
    Assert-True ($errorText -match "does not match the externally published") `
        "Execute accepted changed trust-store bytes under the external pin."

    $rogueAuthority = New-ExternalApproverAuthority
    $errorText = Invoke-WorldRestoreFailure @(
        "approve", "--plan-file", $plan.planPath,
        "--subject", "ops-a",
        "--reason", "Self-issued replacement authority must fail",
        "--private-key-file", $rogueAuthority.PrivateA,
        "--output-file", (Join-Path $success.Evidence "rogue-authority-approval.json"),
        "--trust-store-sha256", $rogueAuthority.Pin) `
        "self-issued replacement authority"
    Assert-True ($errorText -match "does not match the restore plan") `
        "A locally generated replacement authority could self-approve the plan."
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $rogueAuthority.Trust,
        "--trust-store-sha256", $rogueAuthority.Pin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB) "execute with self-issued replacement authority"
    Assert-True ($errorText -match "does not match the restore plan") `
        "Execute accepted a locally generated replacement trust authority."

    $legacyApproval = Join-Path $success.Evidence "legacy-unpinned-approval.json"
    $legacyObject = Get-Content -Raw -Encoding UTF8 -LiteralPath $approval.ApprovalA |
        ConvertFrom-Json
    $legacyObject.PSObject.Properties.Remove("trustStoreSha256")
    $legacyJson = $legacyObject | ConvertTo-Json -Depth 20 -Compress
    [IO.File]::WriteAllText($legacyApproval, $legacyJson, [Text.UTF8Encoding]::new($false))
    $legacyHash = Get-Sha256Hex $legacyApproval
    [IO.File]::WriteAllText(
        $legacyApproval + ".sha256",
        "$legacyHash  $([IO.Path]::GetFileName($legacyApproval))`n",
        [Text.UTF8Encoding]::new($false))
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $legacyApproval,
        "--approval-file", $approval.ApprovalB) "legacy approval without trust pin binding"
    Assert-True ($errorText -match "exact canonical representation") `
        "A legacy approval without trust-store pin binding was accepted."
    $sameSubject = Join-Path $success.Evidence "approval-a-duplicate-subject.json"
    Invoke-WorldRestoreTool @(
        "approve", "--plan-file", $plan.planPath,
        "--subject", "ops-a",
        "--reason", "Second signature from the same subject must fail",
        "--private-key-file", $approval.PrivateA,
        "--output-file", $sameSubject,
        "--trust-store-sha256", $authority.Pin) | Out-Null
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $sameSubject) "same-subject dual approval"
    Assert-True ($errorText -match "expired, duplicated, untrusted") `
        "Two approvals from one subject were accepted."

    # A synthetic renamed host process proves the targeted stop gate.
    $fixtureProcess = Start-Process -FilePath $success.PalServerExecutable `
        -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 30") `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
    Assert-True ((Get-Process -Id $fixtureProcess.Id).ProcessName -eq "PalServer") `
        "Synthetic stop-gate fixture did not run as PalServer."
    $errorText = Invoke-WorldRestoreFailure @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB) "running PalServer gate"
    Assert-True ($errorText -match "still running") `
        "Execution was not refused while the configured synthetic PalServer ran."
    Assert-True (([IO.File]::ReadAllText((Join-Path $success.ActiveWorld "Level.sav"))) -eq $success.OldLevel) `
        "Running-process rejection modified the active world."
    Stop-Process -Id $fixtureProcess.Id -Force
    $fixtureProcess.WaitForExit()
    $fixtureProcess = $null

    foreach ($shippingName in @(
        "PalServer-Win64-Shipping-Cmd",
        "PalServer-Win64-Shipping")) {
        $shippingExecutable = Join-Path $success.InstallRoot `
            "Pal\Binaries\Win64\$shippingName.exe"
        $shippingParent = Split-Path -Parent $shippingExecutable
        New-Item -ItemType Directory -Path $shippingParent -Force | Out-Null
        Copy-Item -LiteralPath $success.PalServerExecutable -Destination $shippingExecutable
        $fixtureProcess = Start-Process -FilePath $shippingExecutable `
            -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 30") `
            -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 500
        Assert-True ((Get-Process -Id $fixtureProcess.Id).ProcessName -eq $shippingName) `
            "Synthetic $shippingName fixture did not retain the expected process name."
        $errorText = Invoke-WorldRestoreFailure @(
            "apply", "--plan-file", $plan.planPath, "--execute",
            "--trust-store", $approval.Trust,
            "--trust-store-sha256", $authority.Pin,
            "--approval-file", $approval.ApprovalA,
            "--approval-file", $approval.ApprovalB) "running $shippingName gate"
        Assert-True ($errorText -match "still running") `
            "Execution was not refused while $shippingName ran from the planned installation."
        Stop-Process -Id $fixtureProcess.Id -Force
        $fixtureProcess.WaitForExit()
        $fixtureProcess = $null
    }

    $result = Invoke-WorldRestoreTool @(
        "apply", "--plan-file", $plan.planPath, "--execute",
        "--trust-store", $approval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $approval.ApprovalA,
        "--approval-file", $approval.ApprovalB)
    Assert-True ($result.executed -and $result.mode -eq "executed") `
        "Approved stopped-server restore did not execute."
    Assert-True (([IO.File]::ReadAllText((Join-Path $success.ActiveWorld "Level.sav"))) -eq $success.RestoredLevel) `
        "Successful switch did not activate the staged backup."
    Assert-True (([IO.File]::ReadAllText((Join-Path $result.rollbackDirectory "Level.sav"))) -eq $success.OldLevel) `
        "Cold rollback copy does not contain the old world."
    Assert-True (([IO.File]::ReadAllText((Join-Path $result.retiredWorldDirectory "Level.sav"))) -eq $success.OldLevel) `
        "Successful switch did not retain the old world directory."
    Assert-True ((Test-Path -LiteralPath (Join-Path $result.rollbackDirectory "EmptyWorldDirectory") -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $result.retiredWorldDirectory "EmptyWorldDirectory") -PathType Container)) `
        "Full rollback/retired directory inventory did not preserve an empty original directory."
    Assert-True (([IO.File]::ReadAllText((Join-Path $success.Data "Level.sav"))) -eq $success.RestoredLevel) `
        "Successful switch modified the managed backup."
    Assert-True ((Get-Sha256Hex $result.reportPath) -eq $result.reportSha256) `
        "Canonical execution report SHA-256 was incorrect."
    $resultEvidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $result.reportPath |
        ConvertFrom-Json
    Assert-True ($resultEvidence.approvals.Count -eq 2 -and
        $resultEvidence.approvals[0].approvalSha256.Length -eq 64 -and
        $resultEvidence.approvals[0].keyFingerprintSha256.Length -eq 64 -and
        $resultEvidence.trustStoreSha256 -eq $authority.Pin) `
        "Result evidence omitted approval hashes, key fingerprints, or trust-store hash."
    Assert-True ($resultEvidence.rollbackInventory.inventorySha256.Length -eq 64 -and
        $resultEvidence.retiredInventory.inventorySha256.Length -eq 64 -and
        $resultEvidence.processGates.Count -ge 4 -and
        $resultEvidence.processGates[0].processNames.Count -eq 3) `
        "Result evidence omitted rollback/retired inventory or process-gate proof."
    Assert-True ((Test-Path -LiteralPath $resultEvidence.authorizationSnapshotDirectory -PathType Container) -and
        $resultEvidence.trustStoreFile.StartsWith(
            $resultEvidence.authorizationSnapshotDirectory,
            [StringComparison]::OrdinalIgnoreCase) -and
        $resultEvidence.trustStoreFile -ne $approval.Trust -and
        $resultEvidence.approvals[0].approvalFile -ne $approval.ApprovalA -and
        (Get-Sha256Hex $resultEvidence.trustStoreFile) -eq $authority.Pin) `
        "Result did not carry immutable operation-scoped authorization snapshot paths and bytes."
    $lockBytesBeforeStatus = [Convert]::ToBase64String(
        [IO.File]::ReadAllBytes($planEvidence.lockFile))
    $lockMtimeBeforeStatus = [IO.File]::GetLastWriteTimeUtc(
        $planEvidence.lockFile).Ticks
    $filesBeforeStatus = @(Get-FixtureFileSnapshot $success.Root)
    $successStatus = Invoke-WorldRestoreTool @(
        "status", "--plan-file", $plan.planPath)
    Assert-True ($successStatus.journalState -eq "committed" -and
        $successStatus.journalOutcome -eq "restored") `
        "Successful restore journal was not durably committed as restored."
    $lockBytesAfterStatus = [Convert]::ToBase64String(
        [IO.File]::ReadAllBytes($planEvidence.lockFile))
    $lockMtimeAfterStatus = [IO.File]::GetLastWriteTimeUtc(
        $planEvidence.lockFile).Ticks
    $filesAfterStatus = @(Get-FixtureFileSnapshot $success.Root)
    Assert-True ($lockBytesAfterStatus -ceq $lockBytesBeforeStatus) `
        "Read-only status changed the persistent lock-file bytes."
    Assert-True ($lockMtimeAfterStatus -eq $lockMtimeBeforeStatus) `
        "Read-only status changed the persistent lock-file modification time."
    $statusFileDifference = @(Compare-Object `
        -ReferenceObject $filesBeforeStatus `
        -DifferenceObject $filesAfterStatus -CaseSensitive)
    Assert-True ($statusFileDifference.Count -eq 0) `
        "Read-only status changed the fixture's complete file set or file contents/metadata."
    $successJournal = Get-Content -Raw -Encoding UTF8 -LiteralPath $successStatus.journalPath |
        ConvertFrom-Json
    Assert-True ($successJournal.schemaVersion -eq 3 -and
        $successJournal.trustStoreSha256 -eq $authority.Pin) `
        "Committed journal changed or omitted the plan's external trust-store pin."

    # Fault after the candidate rename exercises automatic preservation and
    # verified restoration of the original directory without deleting either tree.
    $fault = New-WorldRestoreFixture "fault"
    $faultPlan = New-RestorePlan $fault
    $faultApproval = New-Approvals $fault $faultPlan
    $env:PALCONTROL_WORLD_RESTORE_TEST_ROOT = $fault.Root
    $env:PALCONTROL_WORLD_RESTORE_TEST_FAULT = "after-stage-move"
    try {
        $errorText = Invoke-WorldRestoreFailure @(
            "apply", "--plan-file", $faultPlan.planPath, "--execute",
            "--trust-store", $faultApproval.Trust,
            "--trust-store-sha256", $authority.Pin,
            "--approval-file", $faultApproval.ApprovalA,
            "--approval-file", $faultApproval.ApprovalB) "injected switch fault"
    }
    finally {
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_FAULT -ErrorAction SilentlyContinue
        Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_ROOT -ErrorAction SilentlyContinue
    }
    Assert-True ($errorText -match "automatically restored and verified") `
        "Injected switch failure did not report verified automatic recovery."
    Assert-True (([IO.File]::ReadAllText((Join-Path $fault.ActiveWorld "Level.sav"))) -eq $fault.OldLevel) `
        "Injected switch failure did not restore the original active world."
    $faultReports = @(Get-ChildItem -LiteralPath $fault.Evidence `
        -Filter "world-restore-*-failure.json" -File)
    Assert-True ($faultReports.Count -eq 1) `
        "Fault path did not publish exactly one canonical failure report."
    $faultReport = $faultReports[0]
    $faultEvidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $faultReport.FullName |
        ConvertFrom-Json
    Assert-True $faultEvidence.oldWorldRecovered `
        "Failure evidence does not prove recovery of the old world."
    Assert-True (Test-Path -LiteralPath $faultEvidence.rollbackDirectory -PathType Container) `
        "Fault path did not retain the verified cold rollback copy."
    Assert-True (Test-Path -LiteralPath $faultEvidence.failedCandidateDirectory -PathType Container) `
        "Fault path did not preserve the failed candidate world."
    Assert-True (([IO.File]::ReadAllText((Join-Path $faultEvidence.failedCandidateDirectory "Level.sav"))) -eq $fault.RestoredLevel) `
        "Preserved failed candidate does not contain the staged backup."
    Assert-True (Test-Path -LiteralPath $fault.Backup -PathType Container) `
        "Fault recovery removed the managed backup."

    Assert-True ($faultEvidence.approvals.Count -eq 2 -and
        $faultEvidence.trustStoreSha256 -eq $authority.Pin -and
        $faultEvidence.rollbackInventory.inventorySha256.Length -eq 64 -and
        $faultEvidence.retiredInventory.inventorySha256.Length -eq 64 -and
        $faultEvidence.authorizationSnapshotDirectory.Length -gt 0 -and
        $faultEvidence.recoveryApprovals.Count -eq 0 -and
        $faultEvidence.processGates.Count -ge 1 -and
        $faultEvidence.phase -eq "committed/recovered") `
        "Failure evidence omitted approval/trust/inventory/process/phase proof."

    # Real child-process FailFast points prove that recovery does not depend on
    # catch blocks or in-memory booleans. Exercise both gaps between the moves
    # and after the candidate is active but before result publication.
    foreach ($crashState in @("old-retired", "candidate-active")) {
        $crash = New-WorldRestoreFixture ("crash-" + $crashState)
        $crashPlan = New-RestorePlan $crash
        $temporaryExecutionTrust = Join-Path $crash.Root "temporary-execution-trust.json"
        Copy-Item -LiteralPath $authority.Trust -Destination $temporaryExecutionTrust
        $crashApproval = New-Approvals `
            $crash $crashPlan $authority.Pin $temporaryExecutionTrust
        $crashArguments = @(
            "apply", "--plan-file", $crashPlan.planPath, "--execute",
            "--trust-store", $crashApproval.Trust,
            "--trust-store-sha256", $authority.Pin,
            "--approval-file", $crashApproval.ApprovalA,
            "--approval-file", $crashApproval.ApprovalB)
        $crashProcess = Start-WorldRestoreChild `
            $crashArguments $crash.Root $crashState 0
        $journalCandidate = Join-Path (Split-Path -Parent $crash.ActiveWorld) `
            (".palcontrol-world-restore-{0}-journal.json" -f `
                (Get-Content -Raw -Encoding UTF8 -LiteralPath $crashPlan.planPath |
                    ConvertFrom-Json).operationId)
        $observedState = ""
        for ($attempt = 0; $attempt -lt 100 -and $observedState -ne $crashState; $attempt++) {
            Start-Sleep -Milliseconds 50
            if (Test-Path -LiteralPath $journalCandidate -PathType Leaf) {
                try {
                    $observedState = (Get-Content -Raw -Encoding UTF8 `
                        -LiteralPath $journalCandidate | ConvertFrom-Json).state
                }
                catch {
                    $observedState = ""
                }
            }
        }
        Assert-True ($observedState -eq $crashState) `
            "Child did not pause after durable journal state $crashState."
        Stop-Process -Id $crashProcess.Id -Force
        Assert-True ($crashProcess.WaitForExit(10000)) `
            "Forced child process did not terminate after Stop-Process -Force."
        $crashProcess.Refresh()

        # Execution authorization must now be self-contained in immutable
        # operation snapshots. Rotate/delete every temporary source.
        Remove-Item -LiteralPath $crashApproval.ApprovalA -Force
        Remove-Item -LiteralPath ($crashApproval.ApprovalA + ".sha256") -Force
        Remove-Item -LiteralPath $crashApproval.ApprovalB -Force
        Remove-Item -LiteralPath ($crashApproval.ApprovalB + ".sha256") -Force
        [IO.File]::WriteAllText(
            $temporaryExecutionTrust,
            '{"schemaVersion":1,"keys":[]}',
            [Text.UTF8Encoding]::new($false))

        $pending = Invoke-WorldRestoreTool @(
            "status", "--plan-file", $crashPlan.planPath)
        Assert-True ($pending.status -eq "recovery-required" -and
            $pending.journalState -eq $crashState -and
            $pending.journalOutcome -eq "pending") `
            "Crash journal did not expose durable pending state $crashState."
        if ($crashState -eq "old-retired") {
            Assert-True (-not (Test-Path -LiteralPath $crash.ActiveWorld) -and
                (Test-Path -LiteralPath $pending.retiredWorldDirectory -PathType Container)) `
                "Read-only status moved the old-retired filesystem topology."
        }
        else {
            Assert-True (([IO.File]::ReadAllText((Join-Path $crash.ActiveWorld "Level.sav"))) -eq $crash.RestoredLevel) `
                "Read-only status changed the candidate-active filesystem topology."
        }
        $errorText = Invoke-WorldRestoreFailure @(
            "apply", "--plan-file", $crashPlan.planPath, "--execute",
            "--trust-store-sha256", $authority.Pin) `
            "apply attempted recovery after process crash"
        Assert-True ($errorText -match "dedicated recover command") `
            "Apply moved or attempted to authorize a pending crash journal without fresh recovery approvals."
        $pendingJournalBytes = [IO.File]::ReadAllBytes($pending.journalPath)
        $pendingJournal = Get-Content -Raw -Encoding UTF8 -LiteralPath $pending.journalPath |
            ConvertFrom-Json
        Assert-True ($pendingJournal.authorizationSnapshotDirectory.Length -gt 0 -and
            $pendingJournal.approvals.Count -eq 2 -and
            $pendingJournal.approvals[0].approvalFile -ne $crashApproval.ApprovalA -and
            (Test-Path -LiteralPath $pendingJournal.trustStoreFile -PathType Leaf)) `
            "Journal did not exclusively reference durable execution authorization snapshots."

        if ($crashState -eq "old-retired") {
            $oldExecutionApprovals = [pscustomobject]@{
                ApprovalA = $pendingJournal.approvals[0].approvalFile
                ApprovalB = $pendingJournal.approvals[1].approvalFile
            }
            $errorText = Invoke-WorldRestoreFailure @(
                "recover", "--plan-file", $crashPlan.planPath,
                "--approval-file", $oldExecutionApprovals.ApprovalA,
                "--approval-file", $oldExecutionApprovals.ApprovalB) `
                "manual recovery without external pin"
            Assert-True ($errorText -match "Required option '--trust-store-sha256' is missing") `
                "Manual recovery omitted its external pin requirement."
            $errorText = Invoke-WorldRestoreFailure @(
                "recover", "--plan-file", $crashPlan.planPath,
                "--trust-store-sha256", $wrongPin,
                "--approval-file", $oldExecutionApprovals.ApprovalA,
                "--approval-file", $oldExecutionApprovals.ApprovalB) `
                "manual recovery with wrong external pin"
            Assert-True ($errorText -match "does not match the restore plan") `
                "Manual recovery accepted a wrong external pin."
            $errorText = Invoke-WorldRestoreFailure @(
                "recover", "--plan-file", $crashPlan.planPath,
                "--trust-store-sha256", $authority.Pin,
                "--approval-file", $oldExecutionApprovals.ApprovalA,
                "--approval-file", $oldExecutionApprovals.ApprovalB) `
                "execution approvals reused as recovery approvals"
            Assert-True ($errorText -match "wrong-purpose") `
                "Execution-purpose approvals were accepted for manual recovery."

            $sameRecovery = New-RecoveryApprovals `
                $crash $crashPlan "same-recovery" "ops-a" "ops-a"
            $errorText = Invoke-WorldRestoreFailure @(
                "recover", "--plan-file", $crashPlan.planPath,
                "--trust-store-sha256", $authority.Pin,
                "--approval-file", $sameRecovery.ApprovalA,
                "--approval-file", $sameRecovery.ApprovalB) `
                "same-subject recovery approvals"
            Assert-True ($errorText -match "duplicated") `
                "Two recovery approvals from one subject were accepted."

            $expiredRecovery = New-RecoveryApprovals `
                $crash $crashPlan "expired-recovery" "ops-a" "ops-b" 20
            $errorText = Invoke-WorldRestoreFailure @(
                "recover", "--plan-file", $crashPlan.planPath,
                "--trust-store-sha256", $authority.Pin,
                "--approval-file", $expiredRecovery.ApprovalA,
                "--approval-file", $expiredRecovery.ApprovalB) `
                "expired recovery approval"
            Assert-True ($errorText -match "expired") `
                "An expired but correctly signed recovery approval was accepted."

            $journalText = [Text.Encoding]::UTF8.GetString($pendingJournalBytes)
            $originalFileHash = [string]$pendingJournal.originalInventory[0].sha256
            $replacementHash = if ($originalFileHash -eq ("0" * 64)) {
                "1" * 64
            }
            else {
                "0" * 64
            }
            [IO.File]::WriteAllText(
                $pending.journalPath,
                $journalText.Replace($originalFileHash, $replacementHash),
                [Text.UTF8Encoding]::new($false))
            $errorText = Invoke-WorldRestoreFailure @(
                "status", "--plan-file", $crashPlan.planPath) `
                "forged journal original inventory"
            Assert-True ($errorText -match "invalid or bound to another plan") `
                "A forged journal OriginalInventory was accepted."
            [IO.File]::WriteAllBytes($pending.journalPath, $pendingJournalBytes)
        }

        $recoveryApprovals = New-RecoveryApprovals $crash $crashPlan
        $currentJournalHash = Get-Sha256Hex $pending.journalPath
        $recoveryApprovalBody = Get-Content -Raw -Encoding UTF8 `
            -LiteralPath $recoveryApprovals.ApprovalA | ConvertFrom-Json
        Assert-True ($recoveryApprovalBody.purpose -eq "recover" -and
            $recoveryApprovalBody.journalSha256 -eq $currentJournalHash -and
            $recoveryApprovalBody.journalState -eq $crashState -and
            $recoveryApprovalBody.journalOutcome -eq "pending" -and
            $recoveryApprovalBody.originalInventory.inventorySha256 -eq `
                $pendingJournal.originalInventorySummary.inventorySha256 -and
            $recoveryApprovalBody.candidateInventory.inventorySha256 -eq `
                $pendingJournal.candidateInventorySummary.inventorySha256) `
            "Recovery approval did not sign the exact current journal state and both inventories."
        $recovered = Invoke-Recovery $crashPlan $recoveryApprovals
        Assert-True ($recovered.journalState -eq "committed" -and
            $recovered.journalOutcome -eq "recovered") `
            "Explicit recovery did not commit the recovered journal."
        Assert-True (([IO.File]::ReadAllText((Join-Path $crash.ActiveWorld "Level.sav"))) -eq $crash.OldLevel) `
            "Crash recovery did not restore the exact original active inventory."
        Assert-True (Test-Path -LiteralPath (Join-Path $crash.ActiveWorld "EmptyWorldDirectory") -PathType Container) `
            "Crash recovery omitted an empty directory from the original inventory."
        $recoveryJournal = Get-Content -Raw -Encoding UTF8 -LiteralPath $recovered.journalPath |
            ConvertFrom-Json
        Assert-True ($recoveryJournal.schemaVersion -eq 3 -and
            $recoveryJournal.trustStoreSha256 -eq $authority.Pin) `
            "Recovery journal diverged from the external trust-store pin."
        if ($crashState -eq "candidate-active") {
            Assert-True (Test-Path -LiteralPath $recoveryJournal.failedCandidateDirectory -PathType Container) `
                "Candidate-active recovery did not preserve the failed candidate."
            Assert-True (([IO.File]::ReadAllText((Join-Path $recoveryJournal.failedCandidateDirectory "Level.sav"))) -eq $crash.RestoredLevel) `
                "Preserved failed candidate does not match the candidate inventory."
        }
        else {
            Assert-True (Test-Path -LiteralPath $recovered.stagingDirectory -PathType Container) `
                "Old-retired recovery did not preserve the staged candidate."
        }
        Assert-True ($recoveryJournal.recoveryApprovals.Count -eq 2 -and
            $recoveryJournal.recoveryAuthorizationBaseJournalSha256.Length -eq 64 -and
            $recoveryJournal.recoveryApprovals[0].approvalFile -ne $recoveryApprovals.ApprovalA -and
            (Test-Path -LiteralPath $recoveryJournal.recoveryApprovals[0].approvalFile -PathType Leaf)) `
            "Manual recovery did not durably snapshot its current dual authorization."
        Remove-Item -LiteralPath $recoveryApprovals.ApprovalA -Force
        Remove-Item -LiteralPath ($recoveryApprovals.ApprovalA + ".sha256") -Force
        Remove-Item -LiteralPath $recoveryApprovals.ApprovalB -Force
        Remove-Item -LiteralPath ($recoveryApprovals.ApprovalB + ".sha256") -Force
        $recoveredStatus = Invoke-WorldRestoreTool @(
            "status", "--plan-file", $crashPlan.planPath)
        Assert-True ($recoveredStatus.journalState -eq "committed" -and
            $recoveredStatus.journalOutcome -eq "recovered") `
            "Read-only status did not report the committed recovery."
    }

    # If result+sidecar are durable but the final journal transition is not,
    # recovery must validate and finish the commit rather than roll back a
    # restore that already has complete public evidence.
    $published = New-WorldRestoreFixture "crash-result-published"
    $publishedPlan = New-RestorePlan $published
    $publishedApproval = New-Approvals $published $publishedPlan
    $publishedProcess = Start-WorldRestoreChild @(
        "apply", "--plan-file", $publishedPlan.planPath, "--execute",
        "--trust-store", $publishedApproval.Trust,
        "--trust-store-sha256", $authority.Pin,
        "--approval-file", $publishedApproval.ApprovalA,
        "--approval-file", $publishedApproval.ApprovalB) `
        $published.Root "result-published" 0
    $publishedPlanBody = Get-Content -Raw -Encoding UTF8 -LiteralPath $publishedPlan.planPath |
        ConvertFrom-Json
    $publishedJournalPath = Join-Path (Split-Path -Parent $published.ActiveWorld) `
        (".palcontrol-world-restore-{0}-journal.json" -f $publishedPlanBody.operationId)
    $publishedResultPath = Join-Path $published.Evidence `
        ("world-restore-{0}-result.json" -f $publishedPlanBody.operationId)
    $publishedReady = $false
    for ($attempt = 0; $attempt -lt 100 -and -not $publishedReady; $attempt++) {
        Start-Sleep -Milliseconds 50
        $publishedReady = (Test-Path -LiteralPath $publishedResultPath -PathType Leaf) -and
            (Test-Path -LiteralPath ($publishedResultPath + ".sha256") -PathType Leaf)
    }
    Assert-True $publishedReady `
        "Child did not pause after durable result publication."
    Stop-Process -Id $publishedProcess.Id -Force
    Assert-True ($publishedProcess.WaitForExit(10000)) `
        "Published-result child did not terminate after force stop."
    $publishedRecoveryApprovals = New-RecoveryApprovals $published $publishedPlan
    $publishedRecovered = Invoke-Recovery $publishedPlan $publishedRecoveryApprovals
    Assert-True ($publishedRecovered.journalState -eq "committed" -and
        $publishedRecovered.journalOutcome -eq "restored") `
        "Recovery rolled back or rejected an exact already-published result."
    Assert-True (([IO.File]::ReadAllText((Join-Path $published.ActiveWorld "Level.sav"))) -eq $published.RestoredLevel) `
        "Published-result recovery did not retain the verified candidate world."

    Write-Host "PASS: stopped-world plans freeze both inventories; execution authorization is durably snapshotted; status takes a shared read-only lease without persistent writes and refuses concurrent execute; real child crashes require fresh, current, distinct journal-bound recovery approvals; forged/expired/old/same-subject authorization fails closed while every tree is preserved."
}
finally {
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_FAULT -ErrorAction SilentlyContinue
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_CRASH -ErrorAction SilentlyContinue
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_PAUSE_AT -ErrorAction SilentlyContinue
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_HOLD_LOCK_MS -ErrorAction SilentlyContinue
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_RECOVERY_APPROVAL_AGE_MINUTES `
        -ErrorAction SilentlyContinue
    Remove-Item Env:PALCONTROL_WORLD_RESTORE_TEST_ROOT -ErrorAction SilentlyContinue
    foreach ($child in $childProcesses) {
        if (-not $child.HasExited) {
            Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($junction in $junctions) {
        if (Test-Path -LiteralPath $junction) {
            Remove-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
        }
    }
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($root in $fixtureRoots) {
        $full = [IO.Path]::GetFullPath($root)
        if ($full.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($full).StartsWith(
                "palcontrol-world-restore-smoke-",
                [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
