[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$ControlApiUrl = "http://127.0.0.1:5180",
    [string]$ServerId = "local",
    [string]$RulesVersion = "",
    [string]$TargetWorldId = "",
    [Guid]$OperationId = [Guid]::Empty,
    [switch]$Execute,
    [switch]$PlanOnly,
    [Security.SecureString]$AdminApiKey,
    [string]$AdminApiKeyFile = "",
    [scriptblock]$AdminTotpProvider,
    [string]$Reason = "Controlled weekly world rollover",
    [string]$OfficialRestBaseUrl = "http://127.0.0.1:8212/v1/api/",
    [PSCredential]$OfficialRestCredential,
    [string]$OfficialRestCredentialFile = "",
    [string]$InstallRoot = "",
    [string]$GuardedStartScript = "",
    [string]$StateDirectory = "",
    [int]$DrainTimeoutSeconds = 90,
    [int]$CommandTimeoutSeconds = 300,
    [int]$StartupTimeoutSeconds = 120,
    [int]$ApiTimeoutSeconds = 20,
    [int]$PollIntervalMilliseconds = 1000,
    [ValidateSet("Keep", "Archive", "Delete")]
    [string]$PreviousWorldPolicy = "Keep",
    [switch]$AllowDeletePreviousWorld,
    [string]$ArchiveRoot = "",
    [switch]$EnableTestHooks,
    [scriptblock]$ExternalActionAdapter,
    [ValidateSet("", "Preflight", "Drain", "GameBackup", "EconomyBackup", "Stop", "NewWorld", "Probe", "Commit", "Reopen")]
    [string]$FaultAfterActionStep = "",
    [ValidateSet("", "Preflight", "Drain", "GameBackup", "EconomyBackup", "Stop", "NewWorld", "Probe", "Commit", "Reopen")]
    [string]$FaultAfterSubmitStep = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:OrderedSteps = @(
    "Preflight",
    "Drain",
    "GameBackup",
    "EconomyBackup",
    "Stop",
    "NewWorld",
    "Probe",
    "Commit",
    "Reopen"
)
$script:ApiBase = $null
$script:ClientState = $null
$script:ClientStatePath = $null
$script:AdminSecret = $null
$script:RolloverLock = $null

function Assert-LoopbackUrl([string]$Value, [string]$Name) {
    try {
        $uri = [Uri]$Value
    }
    catch {
        throw "$Name must be a valid absolute loopback HTTP URL."
    }
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "http" -or -not $uri.IsLoopback) {
        throw "$Name must be an absolute loopback HTTP URL."
    }
    return $uri
}

function Assert-BoundedText([string]$Value, [string]$Name, [int]$Minimum, [int]$Maximum) {
    $hasControlCharacter = @(
        $Value.ToCharArray() | Where-Object { [char]::IsControl($_) }
    ).Count -ne 0
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value.Trim().Length -lt $Minimum -or
        $Value.Trim().Length -gt $Maximum -or
        $hasControlCharacter) {
        throw "$Name must contain $Minimum to $Maximum non-control characters."
    }
}

function Assert-WorldId([string]$Value, [string]$Name) {
    if ($Value -notmatch '^[A-Fa-f0-9]{32}$') {
        throw "$Name must be a complete 32-character hexadecimal Palworld world id."
    }
    return $Value.ToUpperInvariant()
}

function Assert-ChildPath([string]$Candidate, [string]$Root, [string]$Name) {
    $fullCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not $fullCandidate.StartsWith(
            $fullRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escaped its intended root."
    }
    return $fullCandidate
}

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
    if (-not $item.PSIsContainer -and
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        $broadSids = @("S-1-1-0", "S-1-5-11", "S-1-5-32-545")
        try {
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
                    throw "The credential file grants read access to a broad Windows principal."
                }
            }
        }
        catch [System.Management.Automation.ItemNotFoundException] {
            throw
        }
        return $resolved
    }
    throw "The credential path must be a regular, non-reparse-point file."
}

function Initialize-AdminSecret {
    if ($null -ne $AdminApiKey -and -not [string]::IsNullOrWhiteSpace($AdminApiKeyFile)) {
        throw "Use either -AdminApiKey or -AdminApiKeyFile, never both."
    }
    if ($null -ne $AdminApiKey) {
        $script:AdminSecret = $AdminApiKey
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($AdminApiKeyFile)) {
        $path = Assert-PrivateSecretFile $AdminApiKeyFile
        $plain = [IO.File]::ReadAllText($path).Trim()
        try {
            Assert-BoundedText $plain "Admin API key" 16 512
            $script:AdminSecret = ConvertTo-SecureString $plain -AsPlainText -Force
        }
        finally {
            $plain = $null
        }
        return
    }
    $script:AdminSecret = Read-Host "Control API administrator key" -AsSecureString
}

function Get-AdminHeaders([bool]$HighRisk) {
    $apiKey = Get-PlainText $script:AdminSecret
    try {
        $headers = @{
            "X-Pal-Admin-Key" = $apiKey
        }
        if ($HighRisk) {
            $secureTotp = if ($null -ne $AdminTotpProvider) {
                & $AdminTotpProvider
            }
            else {
                Read-Host "Current 6-digit administrator TOTP" -AsSecureString
            }
            if ($secureTotp -isnot [Security.SecureString]) {
                throw "AdminTotpProvider must return a SecureString."
            }
            $totp = Get-PlainText $secureTotp
            try {
                if ($totp -notmatch '^\d{6}$') {
                    throw "Administrator TOTP must contain exactly six digits."
                }
                $headers["X-Pal-Admin-Totp"] = $totp
                $headers["X-Pal-Admin-Reason"] = $Reason.Trim()
            }
            finally {
                $totp = $null
                $secureTotp = $null
            }
        }
        return $headers
    }
    finally {
        $apiKey = $null
    }
}

function Get-HttpStatusCode([object]$ErrorRecord) {
    try {
        if ($null -ne $ErrorRecord.Exception.Response.StatusCode) {
            return [int]$ErrorRecord.Exception.Response.StatusCode
        }
    }
    catch {
    }
    return $null
}

function Invoke-ControlApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body = $null,
        [bool]$HighRisk = $false,
        [string]$IdempotencyKey = "",
        [bool]$AllowNotFound = $false
    )
    $headers = Get-AdminHeaders $HighRisk
    if (-not [string]::IsNullOrWhiteSpace($IdempotencyKey)) {
        $headers["Idempotency-Key"] = $IdempotencyKey
    }
    $parameters = @{
        Method = $Method
        Uri = $script:ApiBase + $Path
        Headers = $headers
        TimeoutSec = $ApiTimeoutSeconds
        ErrorAction = "Stop"
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 16 -Compress
    }
    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        if ($AllowNotFound -and (Get-HttpStatusCode $_) -eq 404) {
            return $null
        }
        throw
    }
    finally {
        foreach ($name in @($headers.Keys)) {
            $headers[$name] = $null
        }
        $headers.Clear()
    }
}

function ConvertTo-MutableValue([object]$Value) {
    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $copy[[string]$key] = ConvertTo-MutableValue $Value[$key]
        }
        return $copy
    }
    if ($Value -is [Management.Automation.PSCustomObject]) {
        $copy = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $copy[$property.Name] = ConvertTo-MutableValue $property.Value
        }
        return $copy
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value | ForEach-Object { ConvertTo-MutableValue $_ })
    }
    return $Value
}

function Write-AtomicUtf8File([string]$Path, [string]$Content) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $tempPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($fullPath), [Guid]::NewGuid().ToString("N"))
    $backupPath = Join-Path $directory (
        ".{0}.{1}.bak" -f [IO.Path]::GetFileName($fullPath), [Guid]::NewGuid().ToString("N"))
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
        if (Test-Path -LiteralPath $fullPath) {
            $existing = Get-Item -LiteralPath $fullPath -Force
            if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to replace a reparse-point state file."
            }
            [IO.File]::Replace($tempPath, $fullPath, $backupPath)
            [IO.File]::Delete($backupPath)
        }
        else {
            [IO.File]::Move($tempPath, $fullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Save-ClientState {
    if ($null -eq $script:ClientState -or [string]::IsNullOrWhiteSpace($script:ClientStatePath)) {
        return
    }
    $script:ClientState.updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    Write-AtomicUtf8File `
        $script:ClientStatePath `
        ($script:ClientState | ConvertTo-Json -Depth 24)
}

function Initialize-ClientState([object]$Operation) {
    if (Test-Path -LiteralPath $script:ClientStatePath) {
        $loaded = ConvertTo-MutableValue (
            Get-Content -Raw -Encoding UTF8 -LiteralPath $script:ClientStatePath |
                ConvertFrom-Json)
        if ([int]$loaded.schemaVersion -ne 1) {
            throw "The rollover client journal schema is unsupported."
        }
        if (-not [string]::Equals(
                [string]$loaded.operationId,
                [string]$Operation.operationId,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The local client journal belongs to another operation; archive it after reconciliation."
        }
        if (-not [string]::Equals(
                [string]$loaded.targetWorldId,
                [string]$Operation.targetWorldId,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$loaded.rulesVersion,
                [string]$Operation.rulesVersion,
                [StringComparison]::Ordinal)) {
            throw "The local client journal conflicts with the server-frozen rollover payload."
        }
        $script:ClientState = $loaded
        return
    }
    $script:ClientState = [ordered]@{
        schemaVersion = 1
        operationId = [string]$Operation.operationId
        serverId = [string]$Operation.serverId
        fromSeasonId = [string]$Operation.fromSeasonId
        fromWorldId = [string]$Operation.fromWorldId
        targetWorldId = [string]$Operation.targetWorldId
        rulesVersion = [string]$Operation.rulesVersion
        completed = $false
        actions = [ordered]@{}
        steps = [ordered]@{}
        createdAt = [DateTimeOffset]::UtcNow.ToString("O")
        updatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Save-ClientState
}

function Get-ActionState([string]$Step) {
    if (-not $script:ClientState.actions.Contains($Step)) {
        $script:ClientState.actions[$Step] = [ordered]@{}
        Save-ClientState
    }
    return $script:ClientState.actions[$Step]
}

function Get-RecordedEvidence([string]$Step, [string]$StepKey) {
    if (-not $script:ClientState.steps.Contains($Step)) {
        return $null
    }
    $record = $script:ClientState.steps[$Step]
    if (-not [string]::Equals(
            [string]$record.stepKey,
            $StepKey,
            [StringComparison]::Ordinal)) {
        throw "The local evidence for $Step uses a different server step key."
    }
    return $record.evidence
}

function Record-Evidence([string]$Step, [string]$StepKey, [object]$Evidence) {
    $script:ClientState.steps[$Step] = [ordered]@{
        stepKey = $StepKey
        evidence = ConvertTo-MutableValue $Evidence
        recordedAt = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Save-ClientState
}

function Get-Sha256([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DerivedIdempotencyKey([string]$StepKey, [string]$Purpose) {
    return "rollover-{0}-{1}" -f $Purpose, (Get-Sha256 $StepKey).Substring(0, 40)
}

function New-StepEvidence(
    [object]$Operation,
    [string]$Step,
    [string]$StepKey,
    [string]$EvidenceType,
    [string]$EvidenceReference,
    [string]$EvidenceHash = "",
    [string]$ObservedWorldId = "",
    [bool]$AllGatesPassed = $false) {
    if ([string]::IsNullOrWhiteSpace($EvidenceHash)) {
        $canonical = "{0}|{1}|{2}|{3}|{4}|{5}" -f `
            $Operation.operationId,
            $Step,
            $StepKey,
            $EvidenceType,
            $EvidenceReference,
            $ObservedWorldId
        $EvidenceHash = Get-Sha256 $canonical
    }
    return [ordered]@{
        verified = $true
        evidenceType = $EvidenceType
        evidenceReference = $EvidenceReference
        evidenceHash = $EvidenceHash
        observedWorldId = if ([string]::IsNullOrWhiteSpace($ObservedWorldId)) { $null } else { $ObservedWorldId }
        blockingTransactions = 0
        allGatesPassed = $AllGatesPassed
        blockerCodes = @()
    }
}

function Get-RolloverOperation([Guid]$Id) {
    return Invoke-ControlApi `
        -Method GET `
        -Path ("/admin/weekly-rollover/operations/{0}" -f $Id.ToString("D"))
}

function Get-ActiveRollover {
    return Invoke-ControlApi `
        -Method GET `
        -Path ("/admin/weekly-rollover/operations/active?serverId={0}" -f
            [Uri]::EscapeDataString($ServerId)) `
        -AllowNotFound $true
}

function Get-RolloverPreflight {
    return Invoke-ControlApi -Method GET -Path "/extraction/admin/rollover/preflight"
}

function Get-RolloverReadiness {
    return Invoke-ControlApi -Method GET -Path "/extraction/admin/rollover/readiness"
}

function Assert-NoDurableBlockers([object]$Readiness) {
    $orders = @($Readiness.blockingOrders)
    $runs = @($Readiness.blockingRuns)
    if ($orders.Count -ne 0 -or $runs.Count -ne 0) {
        throw ("Rollover stopped: {0} unresolved order(s) and {1} settlement run(s) remain." -f
            $orders.Count,
            $runs.Count)
    }
}

function Assert-PreflightMatches([object]$Preflight, [object]$Operation, [bool]$MustStart) {
    if ($MustStart -and -not [bool]$Preflight.canStartWorldSwitch) {
        throw ("Rollover preflight rejected execution: {0}" -f $Preflight.reason)
    }
    if (-not [string]::Equals(
            [string]$Preflight.currentSeasonId,
            [string]$Operation.fromSeasonId,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$Preflight.actualWorldId,
            [string]$Operation.fromWorldId,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Rollover stopped: active season or physical world no longer matches the frozen source."
    }
}

function Wait-Until([scriptblock]$Probe, [int]$TimeoutSeconds, [string]$FailureMessage) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Probe
        if ($null -ne $value) {
            return $value
        }
        Start-Sleep -Milliseconds $PollIntervalMilliseconds
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Wait-SaveCommand([string]$CommandId) {
    return Wait-Until -TimeoutSeconds $CommandTimeoutSeconds `
        -FailureMessage "Save command $CommandId did not reach a terminal state before timeout; rerun to poll the same command id." `
        -Probe {
            $command = Invoke-ControlApi `
                -Method GET `
                -Path ("/save-commands/{0}" -f $CommandId)
            $state = [string]$command.state
            if ($state -eq "succeeded") {
                return $command
            }
            if ($state -eq "failed" -or $state -eq "uncertain") {
                $code = if ($null -ne $command.error) { $command.error.code } else { "unknown" }
                throw "Save command $CommandId ended in $state ($code); rollover remains stopped."
            }
            return $null
        }
}

function Assert-BackupFreshAndMatching([object]$Backup, [object]$Operation) {
    if (-not [string]::Equals([string]$Backup.kind, "managed", [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Backup.integrity, "verified", [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$Backup.worldGuid,
            [string]$Operation.fromWorldId,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Backup.manifestSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Managed game backup is unverified, hashless, or belongs to another world."
    }
    $created = [DateTimeOffset]::Parse([string]$Backup.createdAt)
    $operationCreated = [DateTimeOffset]::Parse([string]$Operation.createdAt)
    if ($created -lt $operationCreated -or
        $created -lt [DateTimeOffset]::UtcNow.AddMinutes(-15) -or
        $created -gt [DateTimeOffset]::UtcNow.AddMinutes(2)) {
        throw "Managed game backup is outside the controlled rollover RPO window."
    }
}

function Invoke-GameBackupAction([object]$Operation, [string]$StepKey) {
    $action = Get-ActionState "GameBackup"
    if (-not $action.Contains("createCommandId")) {
        try {
            $command = Invoke-ControlApi `
                -Method POST `
                -Path ("/servers/{0}/backups" -f [Uri]::EscapeDataString($ServerId)) `
                -Body @{
                    reason = $Reason.Trim()
                    label = "weekly-{0}" -f ([string]$Operation.operationId).Replace("-", "")
                } `
                -IdempotencyKey $StepKey
        }
        catch {
            $latest = Get-RolloverOperation ([Guid]$Operation.operationId)
            if ([string]$latest.operation.currentStep -ne "GameBackup") {
                return $null
            }
            throw "Game-backup enqueue outcome is unknown. Server operation is still at GameBackup; rerun to replay its deterministic key after this status check."
        }
        $action.createCommandId = [string]$command.commandId
        $action.backupId = [string]$command.backupId
        Save-ClientState
    }
    $created = Wait-SaveCommand ([string]$action.createCommandId)
    if ([string]::IsNullOrWhiteSpace([string]$action.backupId)) {
        $action.backupId = [string]$created.backupId
        Save-ClientState
    }
    $backup = Invoke-ControlApi `
        -Method GET `
        -Path ("/servers/{0}/backups/{1}" -f
            [Uri]::EscapeDataString($ServerId),
            [Uri]::EscapeDataString([string]$action.backupId))
    Assert-BackupFreshAndMatching $backup $Operation

    if (-not $action.Contains("verifyCommandId")) {
        try {
            $verify = Invoke-ControlApi `
                -Method POST `
                -Path ("/servers/{0}/backups/{1}/verify" -f
                    [Uri]::EscapeDataString($ServerId),
                    [Uri]::EscapeDataString([string]$action.backupId)) `
                -Body @{ reason = $Reason.Trim() } `
                -IdempotencyKey (Get-DerivedIdempotencyKey $StepKey "game-verify")
        }
        catch {
            $latest = Get-RolloverOperation ([Guid]$Operation.operationId)
            if ([string]$latest.operation.currentStep -ne "GameBackup") {
                return $null
            }
            throw "Game-backup verification enqueue outcome is unknown. Rerun after the operation-status check; no new backup will be created."
        }
        $action.verifyCommandId = [string]$verify.commandId
        Save-ClientState
    }
    Wait-SaveCommand ([string]$action.verifyCommandId) | Out-Null
    $verified = Invoke-ControlApi `
        -Method GET `
        -Path ("/servers/{0}/backups/{1}" -f
            [Uri]::EscapeDataString($ServerId),
            [Uri]::EscapeDataString([string]$action.backupId))
    Assert-BackupFreshAndMatching $verified $Operation
    return New-StepEvidence `
        $Operation `
        "GameBackup" `
        $StepKey `
        "managed-backup" `
        ([string]$action.backupId) `
        ([string]$verified.manifestSha256).ToLowerInvariant()
}

function Assert-EconomyManifest([object]$Manifest, [object]$Operation) {
    if (-not [string]::Equals(
            [string]$Manifest.serverId,
            [string]$Operation.serverId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$Manifest.worldId,
            [string]$Operation.fromWorldId,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Manifest.contentHash -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Economy snapshot identity, world, or content hash is invalid."
    }
    $created = [DateTimeOffset]::Parse([string]$Manifest.createdAt)
    if ($created -lt [DateTimeOffset]::Parse([string]$Operation.createdAt) -or
        $created -lt [DateTimeOffset]::UtcNow.AddMinutes(-[int]$Manifest.rpoMinutes) -or
        $created -gt [DateTimeOffset]::UtcNow.AddMinutes(2)) {
        throw "Economy snapshot is outside its manifest RPO window."
    }
    $unexpected = @($Manifest.pendingTransactions | Where-Object {
        -not ([string]::Equals([string]$_.kind, "rollover", [StringComparison]::Ordinal) -and
            [string]::Equals(
                [string]$_.id,
                [string]$Operation.operationId,
                [StringComparison]::OrdinalIgnoreCase))
    })
    if ($unexpected.Count -ne 0) {
        throw "Economy snapshot contains unresolved or uncertain transactions other than this rollover."
    }
}

function Assert-EconomyStage([object]$Stage, [object]$Manifest) {
    $requiredFlags = @(
        "hashesValid",
        "sqliteIntegrityValid",
        "sqliteSchemaValid",
        "foreignKeysValid",
        "economyReplayValid",
        "worldIdValid",
        "activeSeasonWorldValid",
        "ledgerProjectionValid",
        "economyForcedClosed",
        "commandReplayValid",
        "commandIdempotencyValid",
        "pendingStateMatchesManifest"
    )
    foreach ($flag in $requiredFlags) {
        if (-not [bool]$Stage.$flag) {
            throw "Economy staging verification failed required flag '$flag'."
        }
    }
    if ([int]$Stage.blockingOrderCount -ne 0 -or [int]$Stage.pendingCommandCount -ne 0 -or
        -not [string]::Equals(
            [string]$Stage.contentHash,
            [string]$Manifest.contentHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Economy staging contains blocking orders/commands or a mismatched content hash."
    }
}

function Invoke-EconomyBackupAction([object]$Operation, [string]$StepKey) {
    $action = Get-ActionState "EconomyBackup"
    if (-not $action.Contains("backupId")) {
        try {
            $snapshot = Invoke-ControlApi `
                -Method POST `
                -Path "/admin/economy-continuity/snapshots" `
                -Body @{
                    serverId = [string]$Operation.serverId
                    worldId = [string]$Operation.fromWorldId
                    idempotencyKey = $StepKey
                } `
                -HighRisk $true
        }
        catch {
            $latest = Get-RolloverOperation ([Guid]$Operation.operationId)
            if ([string]$latest.operation.currentStep -ne "EconomyBackup") {
                return $null
            }
            throw "Economy-snapshot outcome is unknown. Operation remains at EconomyBackup; rerun to replay the deterministic snapshot key after this status check."
        }
        $action.backupId = [string]$snapshot.backupId
        Save-ClientState
    }
    $manifest = Invoke-ControlApi `
        -Method GET `
        -Path ("/admin/economy-continuity/snapshots/{0}/{1}/verify" -f
            [Uri]::EscapeDataString([string]$Operation.serverId),
            [Uri]::EscapeDataString([string]$action.backupId))
    Assert-EconomyManifest $manifest $Operation

    if (-not $action.Contains("stageVerification")) {
        try {
            $stage = Invoke-ControlApi `
                -Method POST `
                -Path ("/admin/economy-continuity/snapshots/{0}/{1}/stage" -f
                    [Uri]::EscapeDataString([string]$Operation.serverId),
                    [Uri]::EscapeDataString([string]$action.backupId)) `
                -Body @{ expectedWorldId = [string]$Operation.fromWorldId } `
                -HighRisk $true
        }
        catch {
            $latest = Get-RolloverOperation ([Guid]$Operation.operationId)
            if ([string]$latest.operation.currentStep -ne "EconomyBackup") {
                return $null
            }
            throw "Economy staging outcome is unknown. Operation remains at EconomyBackup; rerun only reuses this backup id and server-published staging verification."
        }
        Assert-EconomyStage $stage $manifest
        $action.stageVerification = ConvertTo-MutableValue $stage
        Save-ClientState
    }
    else {
        Assert-EconomyStage $action.stageVerification $manifest
    }

    $after = Invoke-ControlApi `
        -Method GET `
        -Path ("/admin/economy-continuity/snapshots/{0}/{1}/post-snapshot" -f
            [Uri]::EscapeDataString([string]$Operation.serverId),
            [Uri]::EscapeDataString([string]$action.backupId))
    if (@($after.items).Count -ne 0) {
        throw "Transactions changed after the economy snapshot; reconcile them and create a new controlled rollover."
    }
    return New-StepEvidence `
        $Operation `
        "EconomyBackup" `
        $StepKey `
        "economy-snapshot-stage" `
        ([string]$action.backupId) `
        ([string]$manifest.contentHash).ToLowerInvariant()
}

function Get-OfficialCredential {
    if ($null -ne $OfficialRestCredential -and
        -not [string]::IsNullOrWhiteSpace($OfficialRestCredentialFile)) {
        throw "Use either -OfficialRestCredential or -OfficialRestCredentialFile, never both."
    }
    if ($null -ne $OfficialRestCredential) {
        return $OfficialRestCredential
    }
    if (-not [string]::IsNullOrWhiteSpace($OfficialRestCredentialFile)) {
        $path = Assert-PrivateSecretFile $OfficialRestCredentialFile
        $credential = Import-Clixml -LiteralPath $path
        if ($credential -isnot [PSCredential]) {
            throw "OfficialRestCredentialFile must contain one DPAPI-protected PSCredential export."
        }
        return $credential
    }
    return Get-Credential -UserName "admin" -Message "Palworld official REST credentials"
}

function Get-PalServerProcesses {
    return @(Get-Process PalServer, PalServer-Win64-Shipping-Cmd -ErrorAction SilentlyContinue)
}

function Invoke-OfficialRest([string]$RelativePath, [object]$Body, [PSCredential]$Credential) {
    $officialUri = Assert-LoopbackUrl $OfficialRestBaseUrl "OfficialRestBaseUrl"
    $password = Get-PlainText $Credential.Password
    try {
        $pair = $Credential.UserName + ":" + $password
        $authorization = "Basic " + [Convert]::ToBase64String(
            [Text.Encoding]::ASCII.GetBytes($pair))
        $headers = @{ Authorization = $authorization }
        try {
            return Invoke-RestMethod `
                -Method POST `
                -Uri ([Uri]::new($officialUri, $RelativePath)) `
                -Headers $headers `
                -ContentType "application/json" `
                -Body ($Body | ConvertTo-Json -Depth 4 -Compress) `
                -TimeoutSec $ApiTimeoutSeconds `
                -ErrorAction Stop
        }
        finally {
            $headers.Authorization = $null
            $headers.Clear()
            $authorization = $null
        }
    }
    finally {
        $password = $null
        $pair = $null
    }
}

function Invoke-ProductionStop([object]$Context) {
    if ((Get-PalServerProcesses).Count -eq 0) {
        return [pscustomobject]@{ success = $true; alreadyStopped = $true }
    }
    $credential = Get-OfficialCredential
    $requestFailed = $null
    try {
        Invoke-OfficialRest "save" @{} $credential | Out-Null
        Invoke-OfficialRest "shutdown" @{
            waittime = 5
            message = "Controlled weekly world rollover in progress."
        } $credential | Out-Null
    }
    catch {
        $requestFailed = $_
    }
    $stopped = Wait-Until -TimeoutSeconds 45 `
        -FailureMessage "PalServer did not finish graceful shutdown; maintenance remains closed." `
        -Probe {
            if ((Get-PalServerProcesses).Count -eq 0) {
                return $true
            }
            return $null
        }
    if (-not $stopped) {
        throw "PalServer stop could not be verified."
    }
    if ($null -ne $requestFailed) {
        Write-Warning "Official REST response was lost, but process status proves PalServer stopped."
    }
    return [pscustomobject]@{ success = $true; alreadyStopped = $false }
}

function Get-ProductionPaths {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        throw "Production Stop/NewWorld actions require an explicit -InstallRoot."
    }
    $root = (Resolve-Path -LiteralPath $InstallRoot -ErrorAction Stop).Path
    $saveRoot = Assert-ChildPath (Join-Path $root "Pal\Saved\SaveGames\0") $root "save root"
    $settingsPath = Assert-ChildPath (
        (Join-Path $root "Pal\Saved\Config\WindowsServer\GameUserSettings.ini")) `
        $root `
        "settings path"
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "GameUserSettings.ini does not exist."
    }
    if (-not (Test-Path -LiteralPath $saveRoot -PathType Container)) {
        throw "Palworld save root does not exist."
    }
    $start = $GuardedStartScript
    if ([string]::IsNullOrWhiteSpace($start)) {
        $start = [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "..\..\deploy\windows\start-palserver-guarded.ps1"))
    }
    $start = (Resolve-Path -LiteralPath $start -ErrorAction Stop).Path
    return [pscustomobject]@{
        root = $root
        saveRoot = $saveRoot
        settingsPath = $settingsPath
        startScript = $start
    }
}

function Get-ConfiguredWorldId([string]$SettingsPath) {
    $settings = [IO.File]::ReadAllText($SettingsPath)
    $match = [regex]::Match(
        $settings,
        "(?m)^DedicatedServerName=(?<id>[A-Fa-f0-9]{32})\s*$")
    if (-not $match.Success) {
        throw "DedicatedServerName is missing or is not a complete world id."
    }
    return [pscustomobject]@{
        content = $settings
        worldId = $match.Groups["id"].Value.ToUpperInvariant()
    }
}

function Invoke-ProductionNewWorld([object]$Context) {
    $paths = Get-ProductionPaths
    $configured = Get-ConfiguredWorldId $paths.settingsPath
    $source = [string]$Context.operation.fromWorldId
    $target = [string]$Context.operation.targetWorldId
    if ($configured.worldId -ne $source -and $configured.worldId -ne $target) {
        throw "GameUserSettings.ini belongs to neither the frozen source nor target world."
    }
    if ($configured.worldId -eq $source) {
        if ((Get-PalServerProcesses).Count -ne 0) {
            throw "PalServer must be stopped before writing the target world id."
        }
        $targetPath = Assert-ChildPath (
            (Join-Path $paths.saveRoot $target)) $paths.saveRoot "target world"
        if (Test-Path -LiteralPath $targetPath) {
            throw "The frozen target world directory already exists before first switch."
        }
        $updated = [regex]::Replace(
            $configured.content,
            "(?m)^DedicatedServerName=[A-Fa-f0-9]{32}\s*$",
            "DedicatedServerName=$target",
            1)
        Write-AtomicUtf8File $paths.settingsPath $updated
    }
    if ((Get-PalServerProcesses).Count -eq 0) {
        & $paths.startScript -InstallRoot $paths.root | Out-Null
    }
    return [pscustomobject]@{
        success = $true
        configuredWorldId = $target
        previousWorldRetained = $true
    }
}

function Invoke-ProductionProbe([object]$Context) {
    $target = [string]$Context.operation.targetWorldId
    return Wait-Until -TimeoutSeconds $StartupTimeoutSeconds `
        -FailureMessage "The target world did not pass UDP, PalDefender, and physical-world probes." `
        -Probe {
            $gameReady = [bool](Get-NetUDPEndpoint -LocalPort 8211 -ErrorAction SilentlyContinue)
            if (-not $gameReady) {
                return $null
            }
            try {
                $palDefender = Invoke-ControlApi `
                    -Method GET `
                    -Path ("/servers/{0}/paldefender/status" -f
                        [Uri]::EscapeDataString($ServerId))
                $preflight = Get-RolloverPreflight
                if ([bool]$palDefender.connected -and
                    [string]::Equals(
                        [string]$preflight.actualWorldId,
                        $target,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    return [pscustomobject]@{
                        success = $true
                        actualWorldId = $target
                        palDefenderConnected = $true
                    }
                }
            }
            catch {
            }
            return $null
        }
}

function Invoke-ExternalAction([string]$Action, [object]$Context) {
    $result = if ($null -ne $ExternalActionAdapter) {
        & $ExternalActionAdapter $Action $Context
    }
    else {
        switch ($Action) {
            "Stop" { Invoke-ProductionStop $Context }
            "NewWorld" { Invoke-ProductionNewWorld $Context }
            "Probe" { Invoke-ProductionProbe $Context }
            default { throw "No production external action exists for '$Action'." }
        }
    }
    if ($null -eq $result -or -not [bool]$result.success) {
        throw "External action '$Action' did not return verified success."
    }
    return $result
}

function Test-JobCompleted([object]$Job) {
    return ([string]$Job.state -eq "Completed" -or [string]$Job.state -eq "2")
}

function Invoke-CommitAction([object]$Operation, [string]$StepKey) {
    $action = Get-ActionState "Commit"
    if (-not $action.Contains("expiryJobId")) {
        try {
            $expiry = Invoke-ControlApi `
                -Method POST `
                -Path "/admin/season-settlement-jobs/voucher-expiry" `
                -Body @{
                    seasonId = [string]$Operation.fromSeasonId
                    rulesVersion = [string]$Operation.rulesVersion
                } `
                -HighRisk $true
        }
        catch {
            Get-RolloverOperation ([Guid]$Operation.operationId) | Out-Null
            throw "Voucher-expiry prepare outcome is unknown or version-conflicted; rerun only after operation status was read."
        }
        $action.expiryJobId = [string]$expiry.jobId
        Save-ClientState
    }
    $job = Invoke-ControlApi `
        -Method GET `
        -Path ("/admin/season-settlement-jobs/{0}" -f $action.expiryJobId)
    if (-not (Test-JobCompleted $job)) {
        try {
            $job = Invoke-ControlApi `
                -Method POST `
                -Path ("/admin/season-settlement-jobs/{0}/run" -f $action.expiryJobId) `
                -HighRisk $true
        }
        catch {
            $job = Invoke-ControlApi `
                -Method GET `
                -Path ("/admin/season-settlement-jobs/{0}" -f $action.expiryJobId)
            if (-not (Test-JobCompleted $job)) {
                throw "Voucher-expiry run outcome is not completed; operation remains at Commit."
            }
        }
    }
    if (-not (Test-JobCompleted $job) -or
        -not [string]::Equals(
            [string]$job.rulesVersion,
            [string]$Operation.rulesVersion,
            [StringComparison]::Ordinal)) {
        throw "Version-matched voucher expiry did not complete."
    }

    $preflight = Get-RolloverPreflight
    if (-not [string]::Equals(
            [string]$preflight.actualWorldId,
            [string]$Operation.targetWorldId,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Commit stopped: the physical world is not the frozen target."
    }
    $alreadyCommitted = (
        -not [string]::Equals(
            [string]$preflight.currentSeasonId,
            [string]$Operation.fromSeasonId,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$preflight.currentSeasonWorldId,
            [string]$Operation.targetWorldId,
            [StringComparison]::OrdinalIgnoreCase))
    if (-not $alreadyCommitted) {
        if (-not [string]::Equals(
                [string]$preflight.currentSeasonId,
                [string]$Operation.fromSeasonId,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Commit stopped: active season changed to an unexpected identity."
        }
        try {
            $commit = Invoke-ControlApi `
                -Method POST `
                -Path "/extraction/admin/rollover/commit" `
                -Body @{ worldId = [string]$Operation.targetWorldId } `
                -HighRisk $true
            if (-not [string]::Equals(
                    [string]$commit.worldId,
                    [string]$Operation.targetWorldId,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Season commit returned another world id."
            }
        }
        catch {
            $afterUnknownCommit = Get-RolloverPreflight
            $committedAfterTimeout = (
                -not [string]::Equals(
                    [string]$afterUnknownCommit.currentSeasonId,
                    [string]$Operation.fromSeasonId,
                    [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals(
                    [string]$afterUnknownCommit.currentSeasonWorldId,
                    [string]$Operation.targetWorldId,
                    [StringComparison]::OrdinalIgnoreCase))
            if (-not $committedAfterTimeout) {
                throw "Season commit outcome is unresolved. Status does not prove target-season commit; automatic old-world rollback is forbidden."
            }
        }
    }
    $verified = Get-RolloverPreflight
    if ([string]::Equals(
            [string]$verified.currentSeasonId,
            [string]$Operation.fromSeasonId,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$verified.currentSeasonWorldId,
            [string]$Operation.targetWorldId,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target season commit could not be verified from server state."
    }
    return New-StepEvidence `
        $Operation `
        "Commit" `
        $StepKey `
        "season-commit" `
        ("season:{0}" -f [string]$verified.currentSeasonId) `
        "" `
        ([string]$Operation.targetWorldId)
}

function Confirm-RecordedEvidence(
    [string]$Step,
    [object]$Evidence,
    [object]$Operation) {
    switch ($Step) {
        "Preflight" {
            Assert-PreflightMatches (Get-RolloverPreflight) $Operation $true
            Assert-NoDurableBlockers (Get-RolloverReadiness)
        }
        "Drain" {
            $readiness = Get-RolloverReadiness
            Assert-NoDurableBlockers $readiness
            if (-not [bool]$readiness.readyForWorldSwitch) {
                throw "Recorded drain evidence is no longer true."
            }
        }
        "GameBackup" {
            $backup = Invoke-ControlApi `
                -Method GET `
                -Path ("/servers/{0}/backups/{1}" -f
                    [Uri]::EscapeDataString($ServerId),
                    [Uri]::EscapeDataString([string]$Evidence.evidenceReference))
            Assert-BackupFreshAndMatching $backup $Operation
            if (-not [string]::Equals(
                    [string]$backup.manifestSha256,
                    [string]$Evidence.evidenceHash,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Recorded game-backup evidence hash changed."
            }
        }
        "EconomyBackup" {
            $manifest = Invoke-ControlApi `
                -Method GET `
                -Path ("/admin/economy-continuity/snapshots/{0}/{1}/verify" -f
                    [Uri]::EscapeDataString($ServerId),
                    [Uri]::EscapeDataString([string]$Evidence.evidenceReference))
            Assert-EconomyManifest $manifest $Operation
            Assert-EconomyStage $script:ClientState.actions.EconomyBackup.stageVerification $manifest
            $after = Invoke-ControlApi `
                -Method GET `
                -Path ("/admin/economy-continuity/snapshots/{0}/{1}/post-snapshot" -f
                    [Uri]::EscapeDataString($ServerId),
                    [Uri]::EscapeDataString([string]$Evidence.evidenceReference))
            if (@($after.items).Count -ne 0) {
                throw "Post-snapshot transactions appeared after recorded economy staging."
            }
        }
        "Stop" {
            if ($null -eq $ExternalActionAdapter -and (Get-PalServerProcesses).Count -ne 0) {
                throw "Recorded stop evidence is no longer true; do not advance automatically."
            }
        }
        "NewWorld" {
            if ($null -eq $ExternalActionAdapter) {
                $paths = Get-ProductionPaths
                $configured = Get-ConfiguredWorldId $paths.settingsPath
                if ($configured.worldId -ne [string]$Operation.targetWorldId) {
                    throw "Recorded new-world evidence conflicts with GameUserSettings.ini."
                }
            }
        }
        "Probe" {
            $preflight = Get-RolloverPreflight
            if (-not [string]::Equals(
                    [string]$preflight.actualWorldId,
                    [string]$Operation.targetWorldId,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Recorded probe evidence no longer matches the physical target world."
            }
        }
        "Commit" {
            $preflight = Get-RolloverPreflight
            if ([string]::Equals(
                    [string]$preflight.currentSeasonId,
                    [string]$Operation.fromSeasonId,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [string]$preflight.currentSeasonWorldId,
                    [string]$Operation.targetWorldId,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Recorded commit evidence no longer matches the active target season."
            }
        }
    }
    return $true
}

function Invoke-StepAction([string]$Step, [object]$Wrapper) {
    $operation = $Wrapper.operation
    $stepKey = [string]$Wrapper.requiredStepKey
    $recorded = Get-RecordedEvidence $Step $stepKey
    if ($null -ne $recorded) {
        Confirm-RecordedEvidence $Step $recorded $operation | Out-Null
        return $recorded
    }

    $evidence = switch ($Step) {
        "Preflight" {
            $preflight = Get-RolloverPreflight
            Assert-PreflightMatches $preflight $operation $true
            $readiness = Get-RolloverReadiness
            Assert-NoDurableBlockers $readiness
            New-StepEvidence `
                $operation $Step $stepKey "server-preflight" `
                ("operation:{0}:preflight" -f $operation.operationId)
        }
        "Drain" {
            $readiness = Get-RolloverReadiness
            Assert-NoDurableBlockers $readiness
            if (-not [bool]$readiness.maintenance.maintenance) {
                try {
                    Invoke-ControlApi `
                        -Method POST `
                        -Path "/extraction/admin/rollover/maintenance" `
                        -Body @{ maintenance = $true; reason = $Reason.Trim() } `
                        -HighRisk $true `
                        -IdempotencyKey (Get-DerivedIdempotencyKey `
                            $stepKey "maintenance-enable") | Out-Null
                }
                catch {
                    $afterMaintenance = Get-RolloverReadiness
                    if (-not [bool]$afterMaintenance.maintenance.maintenance) {
                        throw "Maintenance request outcome is not proven; operation remains at Drain."
                    }
                }
            }
            $drained = Wait-Until -TimeoutSeconds $DrainTimeoutSeconds `
                -FailureMessage "Economic writes did not drain before timeout." `
                -Probe {
                    $current = Get-RolloverReadiness
                    Assert-NoDurableBlockers $current
                    if ([bool]$current.readyForWorldSwitch) {
                        return $current
                    }
                    return $null
                }
            New-StepEvidence `
                $operation $Step $stepKey "economy-drained" `
                ("operation:{0}:drain" -f $operation.operationId)
        }
        "GameBackup" { Invoke-GameBackupAction $operation $stepKey }
        "EconomyBackup" { Invoke-EconomyBackupAction $operation $stepKey }
        "Stop" {
            $context = [pscustomobject]@{ operation = $operation; stepKey = $stepKey }
            Invoke-ExternalAction "Stop" $context | Out-Null
            New-StepEvidence `
                $operation $Step $stepKey "palworld-stopped" `
                ("operation:{0}:stopped" -f $operation.operationId)
        }
        "NewWorld" {
            $context = [pscustomobject]@{ operation = $operation; stepKey = $stepKey }
            Invoke-ExternalAction "NewWorld" $context | Out-Null
            New-StepEvidence `
                $operation $Step $stepKey "target-world-started" `
                ("world:{0}" -f $operation.targetWorldId)
        }
        "Probe" {
            $context = [pscustomobject]@{ operation = $operation; stepKey = $stepKey }
            Invoke-ExternalAction "Probe" $context | Out-Null
            $preflight = Get-RolloverPreflight
            if (-not [string]::Equals(
                    [string]$preflight.actualWorldId,
                    [string]$operation.targetWorldId,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Probe stopped: Control API resolves another physical world."
            }
            New-StepEvidence `
                $operation $Step $stepKey "target-world-probe" `
                ("world:{0}:probe" -f $operation.targetWorldId) `
                "" `
                ([string]$operation.targetWorldId)
        }
        "Commit" { Invoke-CommitAction $operation $stepKey }
        "Reopen" {
            New-StepEvidence `
                $operation $Step $stepKey "economy-gates-revalidated" `
                ("operation:{0}:reopen" -f $operation.operationId) `
                "" `
                "" `
                $true
        }
        default { throw "Unsupported rollover step '$Step'." }
    }
    if ($null -eq $evidence) {
        throw "Step '$Step' did not produce verified evidence."
    }
    Record-Evidence $Step $stepKey $evidence
    if ([string]::Equals($FaultAfterActionStep, $Step, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Injected client crash after $Step action and durable local evidence."
    }
    return $evidence
}

function Test-CompletedStep([object]$Operation, [string]$Step, [object]$Evidence, [string]$StepKey) {
    $completed = @($Operation.completedSteps | Where-Object {
        [string]::Equals([string]$_.step, $Step, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($completed.Count -ne 1) {
        return $false
    }
    return [string]::Equals(
            [string]$completed[0].stepKey,
            $StepKey,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$completed[0].evidenceHash,
            [string]$Evidence.evidenceHash,
            [StringComparison]::OrdinalIgnoreCase)
}

function Submit-Step([object]$Wrapper, [string]$Step, [object]$Evidence) {
    $operation = $Wrapper.operation
    $stepKey = [string]$Wrapper.requiredStepKey
    try {
        $response = Invoke-ControlApi `
            -Method POST `
            -Path ("/admin/weekly-rollover/operations/{0}/steps/{1}" -f
                $operation.operationId,
                $Step) `
            -Body @{ stepKey = $stepKey; evidence = $Evidence } `
            -HighRisk $true
        $next = $response.operation
    }
    catch {
        $next = Get-RolloverOperation ([Guid]$operation.operationId)
        if (-not (Test-CompletedStep $next.operation $Step $Evidence $stepKey)) {
            throw "Step $Step response was lost and server state does not prove the exact evidence commit. Rerun from this operation state; no blind action retry was attempted."
        }
        Write-Warning "Step $Step response was lost; exact server evidence proves it committed. Continuing from operation state."
    }
    if (-not (Test-CompletedStep $next.operation $Step $Evidence $stepKey)) {
        throw "Server did not persist the exact $Step key/evidence envelope."
    }
    if ([string]::Equals($FaultAfterSubmitStep, $Step, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Injected client crash after server committed $Step."
    }
    return $next
}

function Ensure-ReopenFinalized([object]$Wrapper) {
    $readiness = Get-RolloverReadiness
    if (-not [bool]$readiness.maintenance.maintenance) {
        return $Wrapper
    }
    $operation = $Wrapper.operation
    $record = @($operation.completedSteps | Where-Object {
        [string]::Equals([string]$_.step, "Reopen", [StringComparison]::OrdinalIgnoreCase)
    })
    if ($record.Count -ne 1) {
        throw "Operation is completed but has no unique Reopen evidence; maintenance remains closed."
    }
    $evidence = New-StepEvidence `
        $operation `
        "Reopen" `
        ([string]$record[0].stepKey) `
        "economy-gates-revalidated" `
        ("operation:{0}:reopen" -f $operation.operationId) `
        "" `
        "" `
        $true
    if (-not [string]::Equals(
            [string]$evidence.evidenceHash,
            [string]$record[0].evidenceHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cannot reconstruct exact Reopen evidence; maintenance remains safely closed."
    }
    Invoke-ControlApi `
        -Method POST `
        -Path ("/admin/weekly-rollover/operations/{0}/steps/Reopen" -f $operation.operationId) `
        -Body @{ stepKey = [string]$record[0].stepKey; evidence = $evidence } `
        -HighRisk $true | Out-Null
    $after = Get-RolloverReadiness
    if ([bool]$after.maintenance.maintenance) {
        throw "Reopen evidence replay did not open the maintenance gate."
    }
    return Get-RolloverOperation ([Guid]$operation.operationId)
}

function Get-StepArtifactReference([object]$Operation, [string]$Step) {
    if ($script:ClientState.actions.Contains($Step) -and
        $script:ClientState.actions[$Step].Contains("backupId")) {
        return [string]$script:ClientState.actions[$Step].backupId
    }
    $record = @($Operation.completedSteps | Where-Object {
        [string]::Equals([string]$_.step, $Step, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($record.Count -eq 1) {
        return [string]$record[0].evidenceReference
    }
    return $null
}

function New-RolloverPlan([object]$Active, [object]$Preflight, [object]$Readiness) {
    $proposed = $TargetWorldId
    if ($null -ne $Active) {
        $proposed = [string]$Active.operation.targetWorldId
    }
    elseif ([string]::IsNullOrWhiteSpace($proposed)) {
        $proposed = ([Guid]::NewGuid().ToString("N")).ToUpperInvariant()
    }
    return [pscustomobject]@{
        planOnly = $true
        executeRequested = [bool]$Execute
        serverId = $ServerId
        activeOperationId = if ($null -ne $Active) { $Active.operation.operationId } else { $null }
        currentStep = if ($null -ne $Active) { $Active.operation.currentStep } else { $null }
        currentWorldId = $Preflight.actualWorldId
        currentSeasonId = $Preflight.currentSeasonId
        currentSeasonEndsAt = $Preflight.currentSeasonEndsAt
        targetSeasonCode = $Preflight.targetSeasonCode
        proposedWorldId = $proposed
        rulesVersion = if ($null -ne $Active) { $Active.operation.rulesVersion } else { $RulesVersion }
        previousWorldPolicy = "Keep"
        blockingOrders = @($Readiness.blockingOrders).Count
        blockingRuns = @($Readiness.blockingRuns).Count
        activeOperations = [int]$Readiness.activeOperations
        maintenance = [bool]$Readiness.maintenance.maintenance
        canEnterRollover = [bool]$Preflight.canStartWorldSwitch -and
            @($Readiness.blockingOrders).Count -eq 0 -and
            @($Readiness.blockingRuns).Count -eq 0
        timingReason = $Preflight.reason
        requiredExecuteArguments = if ($null -eq $Active) {
            "-Execute -TargetWorldId $proposed -RulesVersion <frozen-version>"
        }
        else {
            "-Execute -OperationId $($Active.operation.operationId)"
        }
    }
}

if ($PlanOnly -and $Execute) {
    throw "-PlanOnly and -Execute are mutually exclusive."
}
if ($PreviousWorldPolicy -ne "Keep" -or $AllowDeletePreviousWorld -or
    -not [string]::IsNullOrWhiteSpace($ArchiveRoot)) {
    throw "Archive/Delete rollover parameters are retired. This client never moves or deletes an old world; use PreviousWorldPolicy=Keep only."
}
$testHooksRequested = $null -ne $ExternalActionAdapter -or
    -not [string]::IsNullOrWhiteSpace($FaultAfterActionStep) -or
    -not [string]::IsNullOrWhiteSpace($FaultAfterSubmitStep)
if (($testHooksRequested -or $EnableTestHooks) -and
    (-not $EnableTestHooks -or $env:PAL_CONTROL_ROLLOVER_TEST_HOOKS -ne "1")) {
    throw "External action/fault hooks are test-only and require both -EnableTestHooks and PAL_CONTROL_ROLLOVER_TEST_HOOKS=1."
}
Assert-BoundedText $ServerId "ServerId" 1 64
Assert-BoundedText $Reason "Reason" 3 512
if (-not [string]::IsNullOrWhiteSpace($RulesVersion)) {
    Assert-BoundedText $RulesVersion "RulesVersion" 1 128
}
if (-not [string]::IsNullOrWhiteSpace($TargetWorldId)) {
    $TargetWorldId = Assert-WorldId $TargetWorldId "TargetWorldId"
}
if ($DrainTimeoutSeconds -lt 5 -or $CommandTimeoutSeconds -lt 10 -or
    $StartupTimeoutSeconds -lt 10 -or $ApiTimeoutSeconds -lt 2 -or
    $PollIntervalMilliseconds -lt 10 -or $PollIntervalMilliseconds -gt 10000) {
    throw "Timeout and polling parameters are outside safe bounds."
}

$controlUri = Assert-LoopbackUrl $ControlApiUrl "ControlApiUrl"
$script:ApiBase = $controlUri.AbsoluteUri.TrimEnd("/") + "/api/v1"
Initialize-AdminSecret

$health = Invoke-RestMethod `
    -Uri ($controlUri.AbsoluteUri.TrimEnd("/") + "/health/live") `
    -TimeoutSec ([Math]::Min($ApiTimeoutSeconds, 5)) `
    -ErrorAction Stop
if ([string]$health.status -ne "ok") {
    throw "Control API liveness probe failed."
}

$active = if ($OperationId -ne [Guid]::Empty) {
    Get-RolloverOperation $OperationId
}
else {
    Get-ActiveRollover
}
$preflight = Get-RolloverPreflight
$readiness = Get-RolloverReadiness

if ($null -ne $active) {
    if (-not [string]::Equals(
            [string]$active.operation.serverId,
            $ServerId,
            [StringComparison]::Ordinal)) {
        throw "The requested operation belongs to another server."
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetWorldId) -and
        -not [string]::Equals(
            [string]$active.operation.targetWorldId,
            $TargetWorldId,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "TargetWorldId conflicts with the server-frozen active operation."
    }
    if (-not [string]::IsNullOrWhiteSpace($RulesVersion) -and
        -not [string]::Equals(
            [string]$active.operation.rulesVersion,
            $RulesVersion,
            [StringComparison]::Ordinal)) {
        throw "RulesVersion conflicts with the server-frozen active operation."
    }
}

$plan = New-RolloverPlan $active $preflight $readiness
$isPlan = (-not $Execute) -or $PlanOnly -or $WhatIfPreference
if ($isPlan) {
    $plan
    return
}

if ($null -eq $active) {
    if ([string]::IsNullOrWhiteSpace($RulesVersion) -or
        [string]::IsNullOrWhiteSpace($TargetWorldId)) {
        throw "A new execution requires the exact -RulesVersion and -TargetWorldId emitted by a reviewed plan."
    }
    if (-not [bool]$preflight.canStartWorldSwitch) {
        throw ("Rollover preflight rejected execution: {0}" -f $preflight.reason)
    }
    Assert-NoDurableBlockers $readiness
    if ([string]::Equals(
            [string]$preflight.actualWorldId,
            $TargetWorldId,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "TargetWorldId must differ from the current physical world."
    }
}

$targetForConfirmation = if ($null -ne $active) {
    [string]$active.operation.targetWorldId
}
else {
    $TargetWorldId
}
if (-not $PSCmdlet.ShouldProcess(
        "$ServerId -> $targetForConfirmation",
        "execute persistent weekly rollover with verified game/economy backups")) {
    $plan
    return
}

if ([string]::IsNullOrWhiteSpace($StateDirectory)) {
    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $StateDirectory = Join-Path $programData "PalControl\rollover-client"
}
$stateRoot = [IO.Path]::GetFullPath($StateDirectory)
[IO.Directory]::CreateDirectory($stateRoot) | Out-Null
$stateItem = Get-Item -LiteralPath $stateRoot -Force
if (($stateItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "StateDirectory cannot be a reparse point."
}
$safeServerFile = if ($ServerId -match '^[A-Za-z0-9._-]{1,64}$') {
    $ServerId
}
else {
    Get-Sha256 $ServerId
}
$script:ClientStatePath = Assert-ChildPath (
    (Join-Path $stateRoot ("{0}.json" -f $safeServerFile))) $stateRoot "client journal"
$lockPath = Assert-ChildPath (
    (Join-Path $stateRoot ("{0}.lock" -f $safeServerFile))) $stateRoot "client lock"
try {
    $script:RolloverLock = [IO.FileStream]::new(
        $lockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None,
        1,
        [IO.FileOptions]::WriteThrough)
}
catch {
    throw "Another controlled rollover client owns the local server lock."
}

try {
    if ($null -eq $active) {
        try {
            $active = Invoke-ControlApi `
                -Method POST `
                -Path "/admin/weekly-rollover/operations" `
                -Body @{
                    serverId = $ServerId
                    fromSeasonId = [string]$preflight.currentSeasonId
                    fromWorldId = [string]$preflight.actualWorldId
                    targetWorldId = $TargetWorldId
                    rulesVersion = $RulesVersion.Trim()
                } `
                -HighRisk $true
        }
        catch {
            $recovered = Get-ActiveRollover
            if ($null -eq $recovered -or
                -not [string]::Equals(
                    [string]$recovered.operation.targetWorldId,
                    $TargetWorldId,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [string]$recovered.operation.rulesVersion,
                    $RulesVersion.Trim(),
                    [StringComparison]::Ordinal)) {
                throw "Operation creation response was lost and active state does not prove the requested frozen payload."
            }
            Write-Warning "Operation creation response was lost; recovered the exact active operation from server state."
            $active = $recovered
        }
    }
    Initialize-ClientState $active.operation

    while ([string]$active.operation.currentStep -ne "Completed") {
        $step = [string]$active.operation.currentStep
        if ($script:OrderedSteps -notcontains $step) {
            throw "Server returned unsupported rollover step '$step'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$active.requiredStepKey)) {
            throw "Server did not return the deterministic key for step '$step'."
        }
        $evidence = Invoke-StepAction $step $active
        $active = Submit-Step $active $step $evidence
    }

    $active = Ensure-ReopenFinalized $active
    $finalReadiness = Get-RolloverReadiness
    if ([bool]$finalReadiness.maintenance.maintenance) {
        throw "Persistent operation completed but economy maintenance remains enabled."
    }
    $capabilities = Invoke-ControlApi -Method GET -Path "/extraction/capabilities"
    if (-not [bool]$capabilities.writes.purchase.enabled -or
        -not [bool]$capabilities.writes.resourceExchange.enabled) {
        throw "Rollover completed but one or more economy features fail their live safety gate."
    }
    $script:ClientState.completed = $true
    $script:ClientState.completedAt = [DateTimeOffset]::UtcNow.ToString("O")
    Save-ClientState
    [pscustomobject]@{
        completed = $true
        operationId = $active.operation.operationId
        previousWorldId = $active.operation.fromWorldId
        newWorldId = $active.operation.targetWorldId
        rulesVersion = $active.operation.rulesVersion
        previousWorldPolicy = "Keep"
        gameBackupId = Get-StepArtifactReference $active.operation "GameBackup"
        economyBackupId = Get-StepArtifactReference $active.operation "EconomyBackup"
        journal = $script:ClientStatePath
    }
}
catch {
    Write-Warning (
        "Controlled rollover stopped at server state. Economy maintenance is intentionally " +
        "left unchanged; rerun with the same operation after reconciling the reported blocker. " +
        "Never switch back to the old world after Commit without separate human approval.")
    throw
}
finally {
    if ($null -ne $script:RolloverLock) {
        $script:RolloverLock.Dispose()
        $script:RolloverLock = $null
    }
}
