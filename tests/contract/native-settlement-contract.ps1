$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$schemaPath = Join-Path $repositoryRoot "packages\contracts\bridge\message.schema.json"
$adapterPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionNativeInventoryAdapter.cs"
$settlementPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionSettlementService.cs"
$safetyGatePath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\EconomySafetyGate.cs"
$developmentRconPolicyPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\DevelopmentRconSettlementPolicy.cs"
$rconModelsPath = Join-Path $repositoryRoot `
    "services\control-api\Extraction\RconModels.cs"
$storePath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionRunStore.cs"
$endpointsPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeEndpoints.cs"
$openApiPath = Join-Path $repositoryRoot `
    "packages\contracts\openapi\control-api.yaml"
$productionConfigPath = Join-Path $repositoryRoot `
    "deploy\windows\appsettings.Production.example.json"
$nativeRoot = Join-Path $repositoryRoot "mods\pal-control-native"
$nativeLockPath = Join-Path $nativeRoot "dependencies.lock.json"
$nativeCMakePath = Join-Path $nativeRoot "CMakeLists.txt"
$nativeDllMainPath = Join-Path $nativeRoot "Source\PalControl\Private\dllmain.cpp"
$nativeBridgePath = Join-Path $nativeRoot `
    "Source\PalControl\Private\Bridge\NamedPipeServer.cpp"
$nativeGameAdapterPath = Join-Path $nativeRoot `
    "Source\PalControl\Private\GameAdapter\PalworldGameAdapter.cpp"
$nativeBuildScriptPath = Join-Path $nativeRoot `
    "scripts\Build-PalControlNative.ps1"
$nativePrepareScriptPath = Join-Path $nativeRoot `
    "scripts\Prepare-Ue4ssSource.ps1"
$nativeProbeScriptPath = Join-Path $repositoryRoot "tools\native-bridge-probe.ps1"
$nativeDeploymentScriptPath = Join-Path $repositoryRoot `
    "deploy\windows\activate-client-overlay.ps1"
$nativeProbeStartScriptPath = Join-Path $repositoryRoot `
    "deploy\windows\start-native-probe-palserver.ps1"
$nativeProbeStopScriptPath = Join-Path $repositoryRoot `
    "deploy\windows\stop-native-probe-palserver.ps1"
$nativeProbeSuiteScriptPath = Join-Path $repositoryRoot `
    "tools\run-native-bridge-probe-suite.ps1"
$nativeClientPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\NativeBridgeClient.cs"
$bridgeSmokePath = Join-Path $repositoryRoot "tools\bridge-smoke\Program.cs"
$testApiEnvironmentPath = Join-Path $repositoryRoot `
    "tests\integration\helpers\test-api-environment.ps1"

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Native settlement contract failed: $message"
    }
}

$utf8 = [Text.UTF8Encoding]::new($false)
$schema = [IO.File]::ReadAllText($schemaPath, $utf8) | ConvertFrom-Json
$hello = $schema.'$defs'.hello.allOf[1]
foreach ($field in @(
    "steamBuild",
    "runtimeExecutableSha256",
    "runtimeExecutableSize",
    "runtimeNativeDllSha256",
    "runtimeNativeDllSize",
    "runtimeUe4ssDllSha256",
    "runtimeUe4ssDllSize",
    "runtimeIdentityVerified",
    "writeEnabled"
)) {
    Assert-Contract ($hello.required -contains $field) `
        "Bridge hello does not require runtime identity field '$field'."
}
Assert-Contract ($schema.'$defs'.base.properties.protocolVersion.const -eq "1.1") `
    "Bridge protocol is not pinned to runtime-bound version 1.1."
$payload = $schema.'$defs'.inventoryConsumePayload
Assert-Contract ($payload.required -contains "snapshotVersion") `
    "inventory.consume does not require snapshotVersion."
Assert-Contract ($payload.properties.snapshotVersion.const -eq 1) `
    "inventory.consume snapshotVersion is not pinned to 1."

$exactSlotFields = @(
    "slotIndex",
    "itemId",
    "quantity",
    "dynamicCreatedWorldId",
    "dynamicLocalIdInCreatedWorld",
    "hasDynamicItemData",
    "corruptionProgress",
    "corruptionProgressBits"
)
foreach ($variant in $schema.'$defs'.inventoryExpectedSlot.oneOf) {
    foreach ($field in $exactSlotFields) {
        Assert-Contract ($variant.required -contains $field) `
            "slot variant does not require '$field'."
    }
}

$adapter = [IO.File]::ReadAllText($adapterPath, $utf8)
Assert-Contract ($adapter.IndexOf(
        'public const string StableConsumeCapability = "inventory.consume";',
        [StringComparison]::Ordinal) -ge 0) `
    "the Control adapter does not pin the stable capability name."
Assert-Contract ($adapter.IndexOf(
        'actual.ActualConsumed == item.Quantity',
        [StringComparison]::Ordinal) -ge 0) `
    "per-line actualConsumed equality is not enforced."
Assert-Contract ($adapter.IndexOf(
        'persistenceVerified && exactEvidence',
        [StringComparison]::Ordinal) -ge 0) `
    "Native success can bypass persistence and exact evidence."

$settlement = [IO.File]::ReadAllText($settlementPath, $utf8)
Assert-Contract ($settlement.IndexOf(
        '_useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(',
        [StringComparison]::Ordinal) -ge 0) `
    "the settlement service bypasses the shared Development RCON policy."
$safetyGate = [IO.File]::ReadAllText($safetyGatePath, $utf8)
Assert-Contract ($safetyGate.IndexOf(
        '_useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(',
        [StringComparison]::Ordinal) -ge 0) `
    "the economy safety probe bypasses the shared Development RCON policy."
foreach ($runtimeBinding in @(
    "ApprovedNativeSteamBuild",
    "ApprovedNativeExecutableSha256",
    "ApprovedNativeExecutableSize",
    "ApprovedPalServerExecutablePath",
    "ApprovedPalServerProcessSid",
    "NATIVE_RUNTIME_IDENTITY_UNVERIFIED",
    "NATIVE_WRITE_CAPABILITIES_QUARANTINED"
)) {
    Assert-Contract ($safetyGate.IndexOf(
            $runtimeBinding,
            [StringComparison]::Ordinal) -ge 0) `
        "the economy safety gate omits runtime binding '$runtimeBinding'."
}
$rconModels = [IO.File]::ReadAllText($rconModelsPath, $utf8)
Assert-Contract ($rconModels.IndexOf(
        'public bool AllowDevelopmentSettlement { get; init; }',
        [StringComparison]::Ordinal) -ge 0) `
    "the explicit Development settlement diagnostic switch is missing."
$developmentRconPolicy = [IO.File]::ReadAllText($developmentRconPolicyPath, $utf8)
foreach ($requiredPolicyText in @(
        'environment?.IsDevelopment() != true',
        'configuration?.GetValue<bool>("Security:DevelopmentMode") != true',
        'configuration?.GetValue<bool>("PlayerPortal:PublicSteam") == true',
        '!rcon.Enabled',
        '!rcon.AllowDevelopmentSettlement',
        'safety is null || safety.RequireNativeForResourceExchange'
    )) {
    Assert-Contract ($developmentRconPolicy.IndexOf(
            $requiredPolicyText,
            [StringComparison]::Ordinal) -ge 0) `
        "the shared Development RCON policy is missing '$requiredPolicyText'."
}
Assert-Contract ($settlement.IndexOf(
        'TryRecordNativeConsumeReceiptAsync(',
        [StringComparison]::Ordinal) -ge 0) `
    "Native success is not durably recorded before settlement transitions."
Assert-Contract ($settlement.IndexOf(
        'public string SettlementAdapter',
        [StringComparison]::Ordinal) -ge 0) `
    "the active settlement adapter is not exposed generically."

$endpoints = [IO.File]::ReadAllText($endpointsPath, $utf8)
Assert-Contract ($endpoints.IndexOf(
        'group.MapGet("/admin/settlement/status", GetSettlementStatusAsync);',
        [StringComparison]::Ordinal) -ge 0) `
    "the adapter-neutral settlement status endpoint is missing."
Assert-Contract ($endpoints.IndexOf(
        'group.MapGet("/admin/rcon/status", GetSettlementStatusAsync);',
        [StringComparison]::Ordinal) -ge 0) `
    "the deprecated RCON status alias does not share the generic schema."
Assert-Contract ($endpoints.IndexOf(
        'adapter = settlement.SettlementAdapter',
        [StringComparison]::Ordinal) -ge 0) `
    "the status response does not identify the selected settlement adapter."
$statusHandlerStart = $endpoints.IndexOf(
    'private static async Task<IResult> GetSettlementStatusAsync(',
    [StringComparison]::Ordinal)
Assert-Contract ($statusHandlerStart -ge 0) `
    "the generic settlement status handler is missing."
$statusHandlerEnd = $endpoints.IndexOf(
    'private static async Task<RolloverBlockers> GetRolloverBlockersAsync(',
    $statusHandlerStart,
    [StringComparison]::Ordinal)
Assert-Contract ($statusHandlerEnd -gt $statusHandlerStart) `
    "the generic settlement status handler block is malformed."
$statusHandler = $endpoints.Substring(
    $statusHandlerStart,
    $statusHandlerEnd - $statusHandlerStart)
foreach ($genericError in @(
    'SETTLEMENT_PROBE_FAILED',
    'SETTLEMENT_PROBE_UNCERTAIN'
)) {
    Assert-Contract ($statusHandler.IndexOf(
            $genericError,
            [StringComparison]::Ordinal) -ge 0) `
        "the generic status response omits '$genericError'."
}
Assert-Contract ($statusHandler.IndexOf(
        'result.ErrorMessage ??',
        [StringComparison]::Ordinal) -lt 0) `
    "the generic settlement status still passes through adapter-specific errors."

$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
Assert-Contract ($openApi.IndexOf(
        '  /extraction/admin/settlement/status:',
        [StringComparison]::Ordinal) -ge 0) `
    "OpenAPI omits the adapter-neutral settlement status path."
$legacyStatusStart = $openApi.IndexOf(
    '  /extraction/admin/rcon/status:',
    [StringComparison]::Ordinal)
Assert-Contract ($legacyStatusStart -ge 0) `
    "OpenAPI omits the legacy settlement-status alias."
$rolloverStart = $openApi.IndexOf(
    '  /extraction/admin/rollover/maintenance:',
    $legacyStatusStart,
    [StringComparison]::Ordinal)
Assert-Contract ($rolloverStart -gt $legacyStatusStart) `
    "OpenAPI legacy settlement-status alias block is malformed."
$legacyStatus = $openApi.Substring(
    $legacyStatusStart,
    $rolloverStart - $legacyStatusStart)
Assert-Contract ($legacyStatus.IndexOf(
        'deprecated: true',
        [StringComparison]::Ordinal) -ge 0) `
    "OpenAPI does not mark the RCON status alias deprecated."
Assert-Contract ($legacyStatus.IndexOf(
        '#/components/schemas/ExtractionSettlementStatus',
        [StringComparison]::Ordinal) -ge 0) `
    "the legacy status alias diverges from the generic response schema."
Assert-Contract ($openApi.IndexOf(
        'required: [adapter, enabled, connected, outcome, error]',
        [StringComparison]::Ordinal) -ge 0) `
    "the generic settlement status schema is incomplete."
Assert-Contract ($openApi.IndexOf(
        'enum: [native, development-rcon]',
        [StringComparison]::Ordinal) -ge 0) `
    "the generic settlement status adapter enum is missing."

$nativeInventoryProbeStart = $openApi.IndexOf(
    '    NativeInventoryProbe:',
    [StringComparison]::Ordinal)
$nativeInventoryObjectStart = $openApi.IndexOf(
    '    NativeInventoryObject:',
    $nativeInventoryProbeStart,
    [StringComparison]::Ordinal)
$nativeInventoryContainerStart = $openApi.IndexOf(
    '    NativeInventoryContainer:',
    $nativeInventoryObjectStart,
    [StringComparison]::Ordinal)
Assert-Contract ($nativeInventoryProbeStart -ge 0 -and
        $nativeInventoryObjectStart -gt $nativeInventoryProbeStart -and
        $nativeInventoryContainerStart -gt $nativeInventoryObjectStart) `
    "OpenAPI Native inventory probe/object schema blocks are malformed."
$nativeInventoryProbe = $openApi.Substring(
    $nativeInventoryProbeStart,
    $nativeInventoryObjectStart - $nativeInventoryProbeStart)
foreach ($field in @(
    'slotMetadataReady',
    'onlinePlayerCount',
    'onlineInventoryCount'
)) {
    Assert-Contract (@([regex]::Matches(
                $nativeInventoryProbe,
                "(?m)^\s+-?\s*$field\s*:?[\r\n]"
            )).Count -ge 2) `
        "OpenAPI NativeInventoryProbe does not require and define '$field'."
}
$nativeInventoryObject = $openApi.Substring(
    $nativeInventoryObjectStart,
    $nativeInventoryContainerStart - $nativeInventoryObjectStart)
Assert-Contract ($nativeInventoryObject.IndexOf(
        'required: [ownerPlayerUId, ownerOnline, objectName, containers]',
        [StringComparison]::Ordinal) -ge 0 -and
    $nativeInventoryObject.IndexOf(
        '        ownerOnline:',
        [StringComparison]::Ordinal) -ge 0) `
    "OpenAPI NativeInventoryObject does not require and define ownerOnline."

$store = [IO.File]::ReadAllText($storePath, $utf8)
foreach ($field in @(
    "NativeInventorySnapshot",
    "SettlementRequestHash",
    "NativeConsumeReceipt"
)) {
    Assert-Contract ($store.IndexOf($field, [StringComparison]::Ordinal) -ge 0) `
        "settlement persistence does not retain '$field'."
}

$production = [IO.File]::ReadAllText($productionConfigPath, $utf8) | ConvertFrom-Json
$productionSafety = $production.ExtractionMode.Safety
Assert-Contract ($productionSafety.RequireNativeForResourceExchange -eq $true) `
    "the production example does not require Native resource settlement."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -contains "inventory.probe") `
    "the production example does not require inventory.probe."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -contains "inventory.consume") `
    "the production example does not require stable inventory.consume."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -notcontains `
        "inventory.consume.experimental") `
    "the production example accepts the experimental consume capability."
Assert-Contract ($productionSafety.ApprovedNativeProtocolVersion -eq "1.1") `
    "the production example does not require runtime-bound Bridge protocol 1.1."
foreach ($field in @(
    "ApprovedNativeSteamBuild",
    "ApprovedNativeExecutableSha256",
    "ApprovedNativeExecutableSize",
    "ApprovedPalServerExecutablePath",
    "ApprovedPalServerProcessSid"
)) {
    Assert-Contract ($null -ne $productionSafety.$field) `
        "the production example omits '$field'."
}

$nativeLock = [IO.File]::ReadAllText($nativeLockPath, $utf8) | ConvertFrom-Json
Assert-Contract ($nativeLock.palworldTarget -eq "v1.0.1.100619") `
    "the Native lock does not target the current Palworld version."
Assert-Contract ($nativeLock.steamBuild -eq "24181105") `
    "the Native lock does not pin the current Steam build."
Assert-Contract ($nativeLock.palServerExecutable.sha256 -match '^[a-f0-9]{64}$' -and
                 $nativeLock.palServerExecutable.size -gt 0) `
    "the Native lock does not pin the reviewed PalServer executable."
Assert-Contract ($nativeLock.ue4ss.proxyDllSha256 -match '^[a-f0-9]{64}$' -and
                 $nativeLock.ue4ss.proxyDllSize -gt 0) `
    "the Native lock does not pin the reviewed UE4SS proxy DLL."
Assert-Contract ($nativeLock.native.protocolVersion -eq "1.1" -and
                 $nativeLock.native.modVersion -eq "0.3.0-dev.39-ro" -and
                 $nativeLock.native.capabilityStatus -eq `
                    "read-only-candidate-unverified" -and
                 $nativeLock.native.writeCapabilities -eq $false) `
    "the current Native candidate is not explicitly locked read-only and unverified."

$nativeCMake = [IO.File]::ReadAllText($nativeCMakePath, $utf8)
foreach ($token in @(
    "PALCONTROL_TARGET_EXECUTABLE_SHA256",
    "PALCONTROL_TARGET_EXECUTABLE_SIZE",
    "PALCONTROL_ENABLE_WRITE_CAPABILITIES",
    "PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256",
    "PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE",
    "PALCONTROL_PIPE_NAME",
    "PALCONTROL_CONTROL_API_SERVICE_SID",
    "Source/PalControl/Private/Runtime/ExecutableIdentity.cpp",
    "Bcrypt",
    "/Brepro",
    "/PDBALTPATH:PalControlNative.pdb"
)) {
    Assert-Contract ($nativeCMake.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "Native CMake omits '$token'."
}
Assert-Contract ($nativeCMake -match `
        '(?s)PALCONTROL_ENABLE_WRITE_CAPABILITIES.*?operations"\s+OFF\)') `
    "Native write capabilities are not disabled by default."

$nativeDllMain = [IO.File]::ReadAllText($nativeDllMainPath, $utf8)
$identityCheck = $nativeDllMain.IndexOf(
    'Runtime::ReadCurrentExecutableIdentity()',
    [StringComparison]::Ordinal)
$hookRegistration = $nativeDllMain.IndexOf(
    'Unreal::Hook::RegisterEngineTickPreCallback(',
    [StringComparison]::Ordinal)
Assert-Contract ($identityCheck -ge 0 -and $hookRegistration -gt $identityCheck) `
    "Native registers an Unreal hook before verifying the runtime executable."
foreach ($token in @(
    "executable->Sha256 != PALCONTROL_TARGET_EXECUTABLE_SHA256",
    "executable->Size != PALCONTROL_TARGET_EXECUTABLE_SIZE",
    "ue4ssModule->Sha256 != PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256",
    "ue4ssModule->Size != PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE",
    ".WriteEnabled = PALCONTROL_ENABLE_WRITE_CAPABILITIES != 0"
)) {
    Assert-Contract ($nativeDllMain.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "Native runtime identity enforcement omits '$token'."
}

$nativeBridge = [IO.File]::ReadAllText($nativeBridgePath, $utf8)
foreach ($token in @(
    'runtimeExecutableSha256',
    'runtimeExecutableSize',
    'runtimeNativeDllSha256',
    'runtimeNativeDllSize',
    'runtimeUe4ssDllSha256',
    'runtimeUe4ssDllSize',
    'runtimeIdentityVerified',
    'if (identity.WriteEnabled)'
)) {
    Assert-Contract ($nativeBridge.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "Native Bridge hello omits '$token'."
}
foreach ($token in @(
    'available < sizeof(length) + length',
    'CancelIoEx(activePipe, nullptr)',
    'pipeHandleMutex_',
    'const auto wakeClient = CreateFileW(',
    'MaxCommandDeadlineTicks',
    'IsCanonicalUuid',
    'wire.messageId',
    'wire.sentAt',
    'ComputeSha256Hex(wire.payload.str)',
    'DeadlineUtcFileTimeTicks = *deadlineTicks',
    'RequestHashVerified = true',
    'SessionGeneration',
    'BeginSession()',
    'EndSession(sessionGeneration)',
    'TryExecuteNextCommand('
)) {
    Assert-Contract ($nativeBridge.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "Native pipe bounded-read/shutdown safety omits '$token'."
}
$nativeGameAdapter = [IO.File]::ReadAllText($nativeGameAdapterPath, $utf8)
Assert-Contract ($nativeGameAdapter.IndexOf(
        "NATIVE_WRITE_CAPABILITIES_QUARANTINED",
        [StringComparison]::Ordinal) -ge 0) `
    "the game-thread adapter has no independent read-only quarantine."
foreach ($token in @(
    'command.DeadlineUtcFileTimeTicks == 0',
    'COMMAND_DEADLINE_EXPIRED',
    'const bool writeOperation = IsWriteOperation(command.Operation);',
    '!command.RequestHashVerified',
    'INVALID_NATIVE_WRITE_ENVELOPE'
    'ownerOnline',
    'onlinePlayerCount',
    'onlineInventoryCount',
    'ResolveLivePlayers()',
    'NO_ONLINE_PLAYER',
    'NATIVE_LIVE_PLAYER_SCHEMA_UNAVAILABLE',
    'NATIVE_LIVE_PLAYER_IDENTITY_INVALID'
)) {
    Assert-Contract ($nativeGameAdapter.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the game-thread deadline/write-envelope guard omits '$token'."
}
$nativeBuildScript = [IO.File]::ReadAllText($nativeBuildScriptPath, $utf8)
foreach ($token in @(
    'read-only-candidate-unverified',
    '-DPALCONTROL_ENABLE_WRITE_CAPABILITIES=OFF',
    '-DPALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=OFF',
    '-DPALCONTROL_TARGET_UE4SS_RUNTIME_SHA256=',
    'Resolve-MsvcCompiler',
    'UE4SS_SOURCE_CHANGED_DURING_BUILD',
    'patternsleuthCargoLockSha256',
    'CARGO_LOCKED_IMPORT_DRIFT',
    'FETCHED_DEPENDENCY_HEAD_MISMATCH',
    'GIT_SHALLOW FALSE',
    'palControlNativeDllSize',
    'UE4SS_UNTRACKED_SOURCE_DRIFT',
    'UE4SS_SUBMODULE_SOURCE_DIRTY'
)) {
    Assert-Contract ($nativeBuildScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the guarded Native build omits '$token'."
}
$nativePrepareScript = [IO.File]::ReadAllText($nativePrepareScriptPath, $utf8)
foreach ($token in @(
    'UE4SS_WORKTREE_NOT_PRISTINE_OR_PREPARED',
    'patternsleuth_bind.Cargo.lock',
    'GIT_SHALLOW FALSE',
    'LOCKED',
    'Build-PalControlNative.ps1',
    'GuardOnly',
    '--ignored'
)) {
    Assert-Contract ($nativePrepareScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the guarded UE4SS preparation entry omits '$token'."
}
$nativeProbeScript = [IO.File]::ReadAllText($nativeProbeScriptPath, $utf8)
foreach ($token in @(
    'expectedProtocol -ne "1.1"',
    'runtimeExecutableSha256',
    'runtimeExecutableSize',
    'runtimeNativeDllSha256',
    'runtimeNativeDllSize',
    'runtimeUe4ssDllSha256',
    'runtimeUe4ssDllSize',
    'runtimeIdentityVerified',
    'writeEnabled',
    '$writeCapabilities',
    'NativePipeProcessIdentity',
    'GetServerIdentity',
    'GetCanonicalFilePath',
    'OpenProcessToken',
    'ConvertSidToStringSid',
    'ExpectedPalServerExecutablePath',
    'ExpectedPalServerProcessSid',
    'ExpectedPalServerProcessId',
    'ExpectedPalServerProcessCreationTimeUtcFileTime',
    'CreationTimeUtcFileTime',
    'GetProcessTimes',
    'AllowNoOnlinePlayer',
    'NO_ONLINE_PLAYER',
    'NativeProbeDispatchState',
    'dispatch-ambiguous',
    'not-dispatched',
    'Security.Principal.SecurityIdentifier',
    'ExpectedCanonicalPath',
    'Get-FileSha256Hex',
    'expectedNativeDllSize',
    'expectedPipeName',
    '[long]$hello.runtimeNativeDllSize -ne $expectedNativeDllSize',
    'state -cne "succeeded"',
    'Assert-NativeProbeData',
    'Native bridge sent more than one hello on the same connection.',
    'Native bridge returned a result for another command/session.',
    'frame read exceeded the absolute probe deadline'
    'onlinePlayerCount',
    'onlineInventoryCount',
    '$inventory.ownerOnline -eq $true'
)) {
    Assert-Contract ($nativeProbeScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the live read-only probe omits '$token'."
}

$probeTokens = $null
$probeParseErrors = $null
$probeAst = [Management.Automation.Language.Parser]::ParseFile(
    $nativeProbeScriptPath,
    [ref]$probeTokens,
    [ref]$probeParseErrors)
Assert-Contract (@($probeParseErrors).Count -eq 0) `
    "the Native probe script does not parse cleanly."
$probeBehaviorFunctionNames = @(
    "Get-Sha256Hex",
    "Test-JsonInteger",
    "Get-LivePlayerSetEvidence"
)
$probeBehaviorDefinitions = @($probeAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $probeBehaviorFunctionNames -ccontains $node.Name
}, $true))
Assert-Contract ($probeBehaviorDefinitions.Count -eq
    $probeBehaviorFunctionNames.Count) `
    "the Native probe live-player evidence functions are incomplete."
Invoke-Expression (($probeBehaviorDefinitions | ForEach-Object {
    $_.Extent.Text
}) -join [Environment]::NewLine)

function New-ProbeContainer(
    [string] $kind,
    [string] $containerId,
    [bool] $resolved = $true
) {
    return [pscustomobject]@{
        kind = $kind
        containerId = $containerId
        resolved = $resolved
        slots = @()
    }
}

function New-ProbeInventoryData([object[]] $containers) {
    return [pscustomobject]@{
        truncated = $false
        inventoryObjectCount = 1
        onlinePlayerCount = 1
        onlineInventoryCount = 1
        inventories = @([pscustomobject]@{
            ownerOnline = $true
            ownerPlayerUId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            containers = $containers
        })
    }
}

function Assert-ProbeInventoryRejected(
    [object[]] $containers,
    [string] $scenario,
    [string] $expectedErrorPattern
) {
    $rejected = $false
    $message = $null
    try {
        Get-LivePlayerSetEvidence `
            -ProbeOperation "inventory.probe" `
            -Data (New-ProbeInventoryData $containers) | Out-Null
    }
    catch {
        $rejected = $true
        $message = $_.Exception.Message
    }
    Assert-Contract ($rejected -and $message -match $expectedErrorPattern) `
        "the Native inventory probe accepted $scenario."
}

$validProbeContainers = @(
    (New-ProbeContainer "common" "11111111-1111-1111-1111-111111111111"),
    (New-ProbeContainer "dropSlot" "22222222-2222-2222-2222-222222222222"),
    (New-ProbeContainer "food" "33333333-3333-3333-3333-333333333333")
)
$validLivePlayerSet = Get-LivePlayerSetEvidence `
    -ProbeOperation "inventory.probe" `
    -Data (New-ProbeInventoryData $validProbeContainers)
Assert-Contract ($validLivePlayerSet.count -eq 1 -and
    $validLivePlayerSet.sha256 -match '^[0-9a-f]{64}$') `
    "the Native inventory probe rejected a valid unique container set."

Assert-ProbeInventoryRejected `
    -Containers @($validProbeContainers + @(
        New-ProbeContainer `
            "common" `
            "44444444-4444-4444-4444-444444444444" `
            $false
    )) `
    -Scenario "one resolved and one unresolved entry for the same required kind" `
    -ExpectedErrorPattern "exactly one 'common' container entry"
Assert-ProbeInventoryRejected `
    -Containers @(
        (New-ProbeContainer "common" "11111111111111111111111111111111"),
        $validProbeContainers[1],
        $validProbeContainers[2]
    ) `
    -Scenario "a non-canonical container GUID" `
    -ExpectedErrorPattern "non-canonical containerId"
Assert-ProbeInventoryRejected `
    -Containers @(
        $validProbeContainers[0],
        (New-ProbeContainer "dropSlot" "11111111-1111-1111-1111-111111111111"),
        $validProbeContainers[2]
    ) `
    -Scenario "duplicate container IDs across required kinds" `
    -ExpectedErrorPattern "containerIds must be distinct"

$nativeDeploymentScript = [IO.File]::ReadAllText($nativeDeploymentScriptPath, $utf8)
foreach ($token in @(
    'read-only-candidate-unverified',
    '0.3.0-dev.39-ro',
    'Ue4ssReleaseArchivePath',
    'proxyDllSha256',
    'PalDefender d3d9 loader',
    'QuarantineLegacyWorkshopPackages',
    'IncludeSavedStateBackup',
    'BackupRoot must be outside the public Git repository.',
    'Set-PrivateAcl',
    'Protect-PrivateTree',
    'Get-SafeTreeItems',
    'Get-ServerRelativePath',
    'schemaVersion = 2',
    'operatorSid = $operatorSid',
    'aclPolicy = "system-administrators-operator-full-control-protected"',
    'if (-not $Execute)',
    'ReadOnly = $true'
)) {
    Assert-Contract ($nativeDeploymentScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the dev read-only Native deployment guard omits '$token'."
}
Assert-Contract ($nativeDeploymentScript.IndexOf(
        'C216CDFDF57D5ED5C5E609FCB2F59818F3C9397001286B27878E626046EC4E5C',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    "the Native deployment guard still pins the obsolete overlay artifact."

$nativeProbeStartScript = [IO.File]::ReadAllText($nativeProbeStartScriptPath, $utf8)
foreach ($token in @(
    'DeploymentResultPath',
    '0.3.0-dev.39-ro',
    'dev39-ro-readonly-native-maintenance-backup',
    'Assert-PrivateAcl',
    'Assert-BackupTreeMatchesManifest',
    'Backup file identity does not match the manifest',
    '$deployment.schemaVersion -ne 2',
    '$backupManifest.schemaVersion -ne 2',
    'Assert-SavedStateMatchesBackup',
    'PalDefender d3d9 loader must remain quarantined',
    'Legacy PalControl Workshop package remains',
    'NetworkExposureAcknowledged',
    '$ServerArguments.Count -ne 1 -or $ServerArguments[0] -cne "-log"',
    'WindowStyle Hidden',
    'GetOwnerSid',
    'ParentProcessId',
    'ShippingProcessCreationTimeUtcFileTime',
    'ToFileTimeUtc()'
)) {
    Assert-Contract ($nativeProbeStartScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the controlled Native probe startup guard omits '$token'."
}

$nativeProbeStopScript = [IO.File]::ReadAllText($nativeProbeStopScriptPath, $utf8)
foreach ($token in @(
    'ResultPath must be outside both PalServer and the public repository.',
    'playerPolicy = "must-be-zero"',
    'if (-not $Execute)',
    'MaintenanceWindowAcknowledged',
    'ExpectedShippingProcessId',
    'ExpectedShippingProcessCreationTimeUtcFileTime',
    'Assert-PinnedPalServerSession @PinnedSession',
    'Assert-OfficialRestListenerOwnedByProcess',
    'OwningProcess',
    '$httpHandler.AllowAutoRedirect = $false',
    '$httpHandler.UseProxy = $false',
    '-RelativePath "save"',
    'Save outcome is failed or uncertain; shutdown was not attempted.',
    '-RelativePath "shutdown"',
    'the command will not be retried',
    'no force-stop was used',
    'reviewedPortsReleased = $true',
    'forceStopUsed = $false',
    'Set-PrivateAcl -Path $resultFullPath',
    '[Array]::Clear($credentialBytes'
)) {
    Assert-Contract ($nativeProbeStopScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the controlled Native probe shutdown guard omits '$token'."
}
Assert-Contract ($nativeProbeStopScript.IndexOf(
        'Stop-Process',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    "the controlled Native probe shutdown path contains a force-stop primitive."
Assert-Contract ($nativeProbeStopScript.IndexOf(
        'Invoke-WebRequest',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    "the controlled Native probe shutdown path can use ambient proxy/redirect behavior."

$nativeProbeSuiteScript = [IO.File]::ReadAllText($nativeProbeSuiteScriptPath, $utf8)
foreach ($token in @(
    'ExpectedPalServerExecutablePath',
    'ExpectedPalServerProcessSid',
    'ExpectedPalServerProcessId',
    'ExpectedPalServerProcessCreationTimeUtcFileTime',
    'Repository-local probe output is allowed only below ignored .agent-build.',
    'players.schema',
    'players.probe',
    'players.progression.schema',
    'players.progression.probe',
    'inventory.schema',
    'inventory.probe',
    'pals.schema',
    'pals.probe',
    'pals.skills.catalog',
    'announcements.overlay.probe',
    'announcements.banner.probe',
    'ui.notifications.probe',
    '-RawJson',
    '-AllowNoOnlinePlayer',
    'NativeProbeDispatchState',
    'Test-RetryablePreDispatchFailure',
    '$errorCode -ceq "NO_ONLINE_PLAYER"',
    'expected-no-online-player',
    'runtimeIdentityVerified',
    'writeEnabled',
    'executionComplete',
    'allOperationsSucceeded',
    'livePlayerCoverageComplete',
    'independentReviewComplete = $false',
    'acceptanceEligible = $false',
    'Raw payloads may contain player or Pal identifiers'
)) {
    Assert-Contract ($nativeProbeSuiteScript.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the bounded Native probe suite omits '$token'."
}
foreach ($writeOperation in @(
    'inventory.consume',
    'players.kick',
    'players.ban',
    'pals.spawn',
    'world.save'
)) {
    Assert-Contract ($nativeProbeSuiteScript.IndexOf(
            $writeOperation,
            [StringComparison]::Ordinal) -lt 0) `
        "the bounded Native probe suite includes write operation '$writeOperation'."
}
foreach ($unsafeClassificationToken in @(
    'Test-NoOnlinePlayerRejection',
    'positive integer|identified live player',
    'onlinePlayerCount|onlineInventoryCount|No live'
)) {
    Assert-Contract ($nativeProbeSuiteScript.IndexOf(
            $unsafeClassificationToken,
            [StringComparison]::Ordinal) -lt 0) `
        "the bounded Native probe suite still classifies no-player results from fuzzy error text '$unsafeClassificationToken'."
}

$nativeClient = [IO.File]::ReadAllText($nativeClientPath, $utf8)
foreach ($token in @(
    'PAL_MOD_BRIDGE_AWAITING_HELLO',
    'ref helloReceived, serverIdentity',
    'heartbeat arrived before a valid hello',
    'result arrived before a valid hello',
    'MatchesApprovedWriteIdentity',
    'RuntimeNativeDllSha256',
    'RuntimeUe4ssDllSha256',
    'GetNamedPipeServerProcessId',
    'ReadServerProcessIdentityAsync(',
    'GetFinalPathNameByHandle',
    'OpenProcessToken',
    'ConvertSidToStringSid',
    'MatchesApprovedLocalIdentity',
    'ValidateBaseEnvelope(document.RootElement)',
    'messageId must be a non-empty canonical UUID',
    'sentAt is outside the allowed freshness window',
    'ValidateResult(result)',
    'result.State is "succeeded" or "failed" or "uncertain"',
    'Native Bridge messageType is not supported.',
    'ApprovedPalServerExecutablePath',
    'ApprovedPalServerProcessSid',
    'var payloadUtf8 = JsonSerializer.SerializeToUtf8Bytes(payload);',
    'SHA256.HashData(payloadUtf8)',
    'writer.WriteRawValue(payloadUtf8, skipInputValidation: false)',
    'command exceeded its absolute deadline',
    'frame exceeded its 30-second absolute deadline'
)) {
    Assert-Contract ($nativeClient.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the Control API Native session guard omits '$token'."
}
Assert-Contract ($nativeClient.IndexOf(
        'JsonSerializer.Serialize(payload)',
        [StringComparison]::Ordinal) -lt 0) `
    "the Control API hashes a separately serialized mutable payload."

$bridgeSmoke = [IO.File]::ReadAllText($bridgeSmokePath, $utf8)
foreach ($token in @(
    'ValidateCommandEnvelope(root)',
    'deadline - sentAt > TimeSpan.FromSeconds(30)',
    'payload.GetRawText()',
    'SHA256.HashData(payloadUtf8)',
    'CryptographicOperations.FixedTimeEquals('
)) {
    Assert-Contract ($bridgeSmoke.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the fake bridge command-envelope oracle omits '$token'."
}

$testApiEnvironment = [IO.File]::ReadAllText($testApiEnvironmentPath, $utf8)
foreach ($token in @(
    'function Get-TestFileSha256',
    '[Security.Cryptography.SHA256]::Create()',
    '$sha256.ComputeHash($stream)',
    'Get-TestFileSha256 -Path $executable.FullName'
)) {
    Assert-Contract ($testApiEnvironment.IndexOf($token, [StringComparison]::Ordinal) -ge 0) `
        "the cross-runner fake bridge identity helper omits '$token'."
}
Assert-Contract ($testApiEnvironment.IndexOf(
        'Get-FileHash -LiteralPath',
        [StringComparison]::Ordinal) -lt 0) `
    "the fake bridge identity helper depends on Get-FileHash, which is unavailable on some Windows runners."

Write-Host "PASS: runtime-bound protocol 1.1/read-only Native candidate, stable consume, full snapshot, evidence, and durable idempotency contract."
