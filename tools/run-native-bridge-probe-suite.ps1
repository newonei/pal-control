[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedPalServerExecutablePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^S-1-[0-9]+(?:-[0-9]+)+$')]
    [string]$ExpectedPalServerProcessSid,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ExpectedPalServerProcessId,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$ExpectedPalServerProcessCreationTimeUtcFileTime,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(0, 128)]
    [int]$ExpectedOnlinePlayerCount = 0,

    [ValidateRange(3, 30)]
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$probeScript = Join-Path $PSScriptRoot "native-bridge-probe.ps1"
if (-not (Test-Path -LiteralPath $probeScript -PathType Leaf)) {
    throw "Native bridge probe script is missing: $probeScript"
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$outputRoot = [IO.Path]::GetPathRoot($outputPath)
if ($outputRoot -notmatch '^[A-Za-z]:\\$') {
    throw "OutputDirectory must be on a local fixed Windows drive."
}
$drive = [IO.DriveInfo]::new($outputRoot)
if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) {
    throw "OutputDirectory must be on a ready local fixed Windows drive."
}
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
$privateBuildRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot ".agent-build"))
$privateBuildPrefix = $privateBuildRoot.TrimEnd('\') + '\'
if ($outputPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    -not $outputPath.StartsWith(
        $privateBuildPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Repository-local probe output is allowed only below ignored .agent-build."
}
if (Test-Path -LiteralPath $outputPath) {
    throw "OutputDirectory already exists: $outputPath"
}

function Get-PrivateAclSidValues {
    $operatorSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return @('S-1-5-18', 'S-1-5-32-544', $operatorSid) |
        Select-Object -Unique
}

function Assert-NoReparseAncestor {
    param([Parameter(Mandatory = $true)][string]$Path)

    $cursor = Split-Path -Parent $Path
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "OutputDirectory ancestor is a reparse point: $cursor"
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            break
        }
        $next = $parent.FullName
        if ([StringComparer]::OrdinalIgnoreCase.Equals($next, $cursor)) {
            break
        }
        $cursor = $next
    }
}

function Assert-PrivatePathAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $allowed = @(Get-PrivateAclSidValues)
    $acl = Get-Acl -LiteralPath $Path
    try {
        $ownerSid = [Security.Principal.SecurityIdentifier]::new(
            [string]$acl.Owner).Value
    }
    catch {
        $ownerSid = [Security.Principal.NTAccount]::new(
            [string]$acl.Owner).Translate(
                [Security.Principal.SecurityIdentifier]).Value
    }
    if ($ownerSid -notin $allowed) {
        throw "Probe evidence path has an unexpected owner: $Path"
    }
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $sid -notin $allowed) {
            throw "Probe evidence path has an unexpected ACL principal: $Path"
        }
    }
    foreach ($sid in $allowed) {
        if (@($acl.Access | Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]).Value -ceq $sid -and
            ($_.FileSystemRights -band
                [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl
        }).Count -lt 1) {
            throw "Probe evidence path omits a required private ACL principal: $Path"
        }
    }
}

Assert-NoReparseAncestor -Path $outputPath
$outputParent = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "OutputDirectory parent must already exist with a protected private ACL."
}
$parentItem = Get-Item -LiteralPath $outputParent -Force
if (($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputDirectory parent must not be a reparse point."
}
$parentAcl = Get-Acl -LiteralPath $outputParent
if (-not $parentAcl.AreAccessRulesProtected) {
    throw "OutputDirectory parent must have ACL inheritance disabled."
}
Assert-PrivatePathAcl -Path $outputParent
$directorySecurity = [Security.AccessControl.DirectorySecurity]::new()
$directorySecurity.SetAccessRuleProtection($true, $false)
$directorySecurity.SetOwner(
    [Security.Principal.WindowsIdentity]::GetCurrent().User)
$inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
foreach ($sidValue in (Get-PrivateAclSidValues)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new($sidValue),
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    [void]$directorySecurity.AddAccessRule($rule)
}
[void][IO.Directory]::CreateDirectory($outputPath, $directorySecurity)
$outputItem = Get-Item -LiteralPath $outputPath -Force
if (($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputDirectory was created as a reparse point."
}
$outputAcl = Get-Acl -LiteralPath $outputPath
if (-not $outputAcl.AreAccessRulesProtected) {
    throw "OutputDirectory ACL inheritance was not disabled."
}
Assert-PrivatePathAcl -Path $outputPath

$lockPath = Join-Path $repositoryRoot `
    "mods\pal-control-native\dependencies.lock.json"
$identityPaths = @($lockPath, $probeScript, $PSCommandPath)
$identityStreams = [System.Collections.Generic.List[System.IO.FileStream]]::new()
foreach ($path in $identityPaths) {
    $identityStreams.Add([IO.FileStream]::new(
        $path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read))
}
$sourceIdentityStart = [ordered]@{
    dependencyLockSha256 = (Get-FileHash -LiteralPath $lockPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    probeScriptSha256 = (Get-FileHash -LiteralPath $probeScript `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    suiteScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}

$operations = @(
    "players.schema",
    "players.probe",
    "players.progression.schema",
    "players.progression.probe",
    "inventory.schema",
    "inventory.probe",
    "pals.schema",
    "pals.probe",
    "pals.skills.catalog",
    "announcements.overlay.probe",
    "announcements.banner.probe",
    "ui.notifications.probe"
)
$requiresOnlinePlayer = @(
    "players.probe",
    "players.progression.probe",
    "inventory.probe"
)

function Write-PrivateText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyString()][Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText(
        $Path,
        $Content + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Test-JsonInteger {
    param([object]$Value)

    return $Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]
}

function Test-RetryablePreDispatchFailure {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    # A retry is safe only when the probe explicitly attests that no command
    # dispatch was attempted. Unknown/ambiguous/post-dispatch failures are final.
    $stage = [string]$ErrorRecord.Exception.Data["NativeProbeDispatchState"]
    if ($stage -cne "not-dispatched") {
        return $false
    }
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if ($exception -is [TimeoutException] -or
            $exception -is [IO.IOException]) {
            return $true
        }
        $exception = $exception.InnerException
    }
    return $false
}

$startedAt = [DateTimeOffset]::UtcNow
$results = New-Object System.Collections.Generic.List[object]
foreach ($operation in $operations) {
    $rawJson = $null
    $parsed = $null
    $failureMessage = $null
    $failureStage = $null
    $attempts = 0
    while ($attempts -lt 3 -and $null -eq $rawJson) {
        $attempts++
        try {
            $output = @(& $probeScript `
                -Operation $operation `
                -ExpectedPalServerExecutablePath $ExpectedPalServerExecutablePath `
                -ExpectedPalServerProcessSid $ExpectedPalServerProcessSid `
                -ExpectedPalServerProcessId $ExpectedPalServerProcessId `
                -ExpectedPalServerProcessCreationTimeUtcFileTime `
                    $ExpectedPalServerProcessCreationTimeUtcFileTime `
                -TimeoutSeconds $TimeoutSeconds `
                -AllowNoOnlinePlayer `
                -RawJson)
            $jsonLines = @($output | ForEach-Object { [string]$_ } | Where-Object {
                $_.TrimStart().StartsWith('{')
            })
            if ($jsonLines.Count -ne 1) {
                throw "Probe completed without exactly one JSON result line."
            }
            $candidateRawJson = $jsonLines[0]
            $candidateParsed = $candidateRawJson | ConvertFrom-Json
            if ($null -eq $candidateParsed.serverIdentity -or
                -not (Test-JsonInteger $candidateParsed.serverIdentity.processId) -or
                [int64]$candidateParsed.serverIdentity.processId -ne
                    $ExpectedPalServerProcessId -or
                -not (Test-JsonInteger `
                    $candidateParsed.serverIdentity.creationTimeUtcFileTime) -or
                [int64]$candidateParsed.serverIdentity.creationTimeUtcFileTime -ne
                    $ExpectedPalServerProcessCreationTimeUtcFileTime -or
                $null -eq $candidateParsed.hello -or
                $null -eq $candidateParsed.result) {
                throw "Probe JSON omitted the exact verified server process identity."
            }
            if ($requiresOnlinePlayer -ccontains $operation -and
                [string]$candidateParsed.result.state -ceq "succeeded" -and
                ($candidateParsed.livePlayerSet -isnot [pscustomobject] -or
                 -not (Test-JsonInteger $candidateParsed.livePlayerSet.count) -or
                 [long]$candidateParsed.livePlayerSet.count -le 0 -or
                 $candidateParsed.livePlayerSet.sha256 -isnot [string] -or
                 $candidateParsed.livePlayerSet.sha256 -notmatch '^[0-9a-f]{64}$')) {
                throw "Probe JSON omitted valid privacy-safe live-player set evidence."
            }
            # Publish the candidate only after parsing and identity validation.
            # Otherwise a malformed result could survive the catch block merely
            # because its raw JSON string had already been assigned.
            $rawJson = $candidateRawJson
            $parsed = $candidateParsed
        }
        catch {
            $failureMessage = $_.Exception.Message
            $failureStage = [string]$_.Exception.Data["NativeProbeDispatchState"]
            if (-not (Test-RetryablePreDispatchFailure -ErrorRecord $_) -or
                $attempts -ge 3) {
                break
            }
            Start-Sleep -Seconds 1
        }
    }

    if ($null -ne $rawJson) {
        $errorCode = if ($null -ne $parsed.result.error) {
            [string]$parsed.result.error.code
        }
        else {
            $null
        }
        $classification = if ([string]$parsed.result.state -ceq "succeeded") {
            "succeeded"
        }
        elseif ([string]$parsed.result.state -ceq "failed" -and
            $requiresOnlinePlayer -ccontains $operation -and
            $errorCode -ceq "NO_ONLINE_PLAYER") {
            "expected-no-online-player"
        }
        else {
            "failed"
        }
        Write-PrivateText `
            -Path (Join-Path $outputPath "$operation.json") `
            -Content $rawJson
        $results.Add([ordered]@{
            operation = $operation
            outcome = $classification
            attempts = $attempts
            resultState = [string]$parsed.result.state
            modVersion = [string]$parsed.hello.modVersion
            protocolVersion = [string]$parsed.hello.protocolVersion
            runtimeIdentityVerified = [bool]$parsed.hello.runtimeIdentityVerified
            processIdentityVerified = $true
            writeEnabled = [bool]$parsed.hello.writeEnabled
            errorCode = $errorCode
            livePlayerSetCount = if ($classification -ceq "succeeded" -and
                $requiresOnlinePlayer -ccontains $operation) {
                [long]$parsed.livePlayerSet.count
            }
            else {
                $null
            }
            livePlayerSetSha256 = if ($classification -ceq "succeeded" -and
                $requiresOnlinePlayer -ccontains $operation) {
                [string]$parsed.livePlayerSet.sha256
            }
            else {
                $null
            }
            rejectionReason = if ($classification -ceq "expected-no-online-player") {
                "NO_ONLINE_PLAYER"
            }
            else {
                $null
            }
        })
    }
    else {
        Write-PrivateText `
            -Path (Join-Path $outputPath "$operation.rejected.txt") `
            -Content $failureMessage
        $results.Add([ordered]@{
            operation = $operation
            outcome = "failed"
            attempts = $attempts
            resultState = $null
            modVersion = $null
            protocolVersion = $null
            runtimeIdentityVerified = $null
            processIdentityVerified = $null
            writeEnabled = $null
            errorCode = $null
            livePlayerSetCount = $null
            livePlayerSetSha256 = $null
            failureStage = $failureStage
            rejectionReason = $failureMessage
        })
    }
    Start-Sleep -Milliseconds 750
}

$failedCount = @($results | Where-Object outcome -ceq "failed").Count
$succeededCount = @($results | Where-Object outcome -ceq "succeeded").Count
$expectedNoOnlinePlayerCount = @($results | Where-Object {
    $_.outcome -ceq "expected-no-online-player"
}).Count
$executionComplete = $results.Count -eq $operations.Count
$allOperationsSucceeded = $executionComplete -and
    $succeededCount -eq $operations.Count
$liveProbeResults = @($results | Where-Object {
    $requiresOnlinePlayer -ccontains $_.operation
})
$livePlayerCoverageComplete = $executionComplete -and
    $ExpectedOnlinePlayerCount -gt 0 -and
    @($liveProbeResults | Where-Object {
        $requiresOnlinePlayer -ccontains $_.operation -and
        $_.outcome -ceq "succeeded"
    }).Count -eq $requiresOnlinePlayer.Count -and
    @($liveProbeResults | Where-Object {
        $_.livePlayerSetCount -ne $ExpectedOnlinePlayerCount
    }).Count -eq 0 -and
    @($liveProbeResults.livePlayerSetSha256 |
        Sort-Object -CaseSensitive -Unique).Count -eq 1

function Get-FileEvidenceRecord {
    param([Parameter(Mandatory = $true)][IO.FileInfo]$File)

    Assert-PrivatePathAcl -Path $File.FullName
    return [ordered]@{
        name = $File.Name
        length = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).
            Hash.ToLowerInvariant()
    }
}

$rawEvidence = @(Get-ChildItem -LiteralPath $outputPath -File -Force |
    Sort-Object Name |
    ForEach-Object { Get-FileEvidenceRecord -File $_ })
$sourceIdentityEnd = [ordered]@{
    dependencyLockSha256 = (Get-FileHash -LiteralPath $lockPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    probeScriptSha256 = (Get-FileHash -LiteralPath $probeScript `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    suiteScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($name in $sourceIdentityStart.Keys) {
    if ($sourceIdentityStart[$name] -cne $sourceIdentityEnd[$name]) {
        throw "Probe evidence identity changed while the suite was running: $name"
    }
}
$gitCommit = @(& git -C $repositoryRoot rev-parse HEAD 2>$null)[0]
if ($LASTEXITCODE -ne 0 -or $gitCommit -notmatch '^[0-9a-f]{40}$') {
    throw "The probe suite could not bind evidence to the repository commit."
}
$gitStatus = @(& git -C $repositoryRoot status --porcelain=v1 2>$null)
if ($LASTEXITCODE -ne 0) {
    throw "The probe suite could not determine repository worktree state."
}
$summary = [ordered]@{
    schemaVersion = 2
    startedAt = $startedAt.ToString("O")
    completedAt = [DateTimeOffset]::UtcNow.ToString("O")
    expectedOperationCount = $operations.Count
    succeededCount = $succeededCount
    expectedNoOnlinePlayerCount = $expectedNoOnlinePlayerCount
    expectedOnlinePlayerCount = $ExpectedOnlinePlayerCount
    failedCount = $failedCount
    executionComplete = $executionComplete
    allOperationsSucceeded = $allOperationsSucceeded
    livePlayerCoverageComplete = $livePlayerCoverageComplete
    independentReviewComplete = $false
    acceptanceEligible = $false
    serverProcessId = $ExpectedPalServerProcessId
    serverProcessCreationTimeUtcFileTime =
        $ExpectedPalServerProcessCreationTimeUtcFileTime
    sourceIdentity = [ordered]@{
        gitCommit = $gitCommit
        worktreeDirty = $gitStatus.Count -ne 0
        dependencyLockSha256 = $sourceIdentityStart.dependencyLockSha256
        probeScriptSha256 = $sourceIdentityStart.probeScriptSha256
        suiteScriptSha256 = $sourceIdentityStart.suiteScriptSha256
    }
    rawEvidence = $rawEvidence
    evidenceManifestFile = "manifest.json"
    outputDirectory = $outputPath
    results = $results.ToArray()
}
$summaryJson = $summary | ConvertTo-Json -Depth 10
Write-PrivateText `
    -Path (Join-Path $outputPath "summary.json") `
    -Content $summaryJson
$evidenceFiles = @(Get-ChildItem -LiteralPath $outputPath -File -Force |
    Sort-Object Name |
    ForEach-Object { Get-FileEvidenceRecord -File $_ })
$manifest = [ordered]@{
    schemaVersion = 1
    createdAt = [DateTimeOffset]::UtcNow.ToString("O")
    serverProcessId = $ExpectedPalServerProcessId
    serverProcessCreationTimeUtcFileTime =
        $ExpectedPalServerProcessCreationTimeUtcFileTime
    files = $evidenceFiles
}
Write-PrivateText `
    -Path (Join-Path $outputPath "manifest.json") `
    -Content ($manifest | ConvertTo-Json -Depth 10)
Assert-PrivatePathAcl -Path (Join-Path $outputPath "manifest.json")
foreach ($stream in $identityStreams) {
    $stream.Dispose()
}

# Emit only the bounded summary. Raw payloads may contain player or Pal identifiers
# and remain in the private/ignored output directory.
$summary | ConvertTo-Json -Depth 10
if ($failedCount -ne 0 -or
    ($ExpectedOnlinePlayerCount -gt 0 -and -not $livePlayerCoverageComplete)) {
    exit 1
}
