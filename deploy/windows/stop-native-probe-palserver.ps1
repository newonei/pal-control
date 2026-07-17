[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PalServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ExpectedShippingProcessId,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 9223372036854775807)]
    [long]$ExpectedShippingProcessCreationTimeUtcFileTime,

    [ValidateRange(5, 60)]
    [int]$ShutdownDelaySeconds = 10,

    [ValidateRange(30, 180)]
    [int]$ShutdownTimeoutSeconds = 90,

    [switch]$MaintenanceWindowAcknowledged,

    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

function Assert-NoReparseAncestor {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $cursor)) {
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            throw "$Label has no existing filesystem ancestor."
        }
        $cursor = $parent.FullName
    }
    while ($true) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse-point ancestor: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
}

function Get-CurrentOperatorSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw "The current maintenance identity has no Windows SID."
    }
    return $identity.User.Value
}

function Get-PrivateAclSidValues {
    param([Parameter(Mandatory = $true)][string]$OperatorSid)

    return @("S-1-5-18", "S-1-5-32-544", $OperatorSid) | Select-Object -Unique
}

function Assert-PrivateAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OperatorSid,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is a reparse point: $Path"
    }
    $allowed = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($sidValue in (Get-PrivateAclSidValues -OperatorSid $OperatorSid)) {
        [void]$allowed.Add($sidValue)
    }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "$Label inherits filesystem permissions: $Path"
    }
    try {
        $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        $ownerSid = ([Security.Principal.SecurityIdentifier]$acl.Owner).Value
    }
    if (-not $allowed.Contains($ownerSid)) {
        throw "$Label has an unapproved owner SID: $Path"
    }
    $present = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($rule in @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))) {
        $sidValue = $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            -not $allowed.Contains($sidValue) -or
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl) {
            throw "$Label has an unapproved filesystem rule: $Path"
        }
        [void]$present.Add($sidValue)
    }
    foreach ($sidValue in $allowed) {
        if (-not $present.Contains($sidValue)) {
            throw "$Label is missing a required private ACL principal: $Path"
        }
    }
}

function Set-PrivateAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OperatorSid
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to secure a reparse point: $Path"
    }
    if ($item.PSIsContainer) {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $inheritance = (
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit)
    }
    else {
        $security = [Security.AccessControl.FileSecurity]::new()
        $inheritance = [Security.AccessControl.InheritanceFlags]::None
    }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new($OperatorSid))
    foreach ($sidValue in (Get-PrivateAclSidValues -OperatorSid $OperatorSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sidValue),
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    Assert-PrivateAcl -Path $Path -OperatorSid $OperatorSid -Label "Private maintenance item"
}

function Enter-RootMaintenanceMutex {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ([IO.Path]::GetFullPath($Root)).TrimEnd('\').ToLowerInvariant())
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '') }
    finally {
        $sha.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    $mutex = [Threading.Mutex]::new($false, "Global\PalControlNativeMaintenance-$suffix")
    try { $acquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) {
        $mutex.Dispose()
        throw "Another maintenance operation is active for this PalServer root."
    }
    return $mutex
}

function Exit-RootMaintenanceMutex {
    param([AllowNull()][Threading.Mutex]$Mutex)

    if ($null -ne $Mutex) {
        $Mutex.ReleaseMutex()
        $Mutex.Dispose()
    }
}

function ConvertFrom-UnrealQuotedValue {
    param([Parameter(Mandatory = $true)][string]$Value)

    $builder = [Text.StringBuilder]::new($Value.Length)
    for ($index = 0; $index -lt $Value.Length; $index++) {
        $character = $Value[$index]
        if ($character -ne '\' -or $index + 1 -ge $Value.Length) {
            [void]$builder.Append($character)
            continue
        }

        $index++
        $escaped = $Value[$index]
        switch ($escaped) {
            '\' { [void]$builder.Append('\') }
            '"' { [void]$builder.Append('"') }
            'n' { [void]$builder.Append("`n") }
            'r' { [void]$builder.Append("`r") }
            't' { [void]$builder.Append("`t") }
            default { [void]$builder.Append($escaped) }
        }
    }
    return $builder.ToString()
}

function Get-PalServerProcesses {
    return @(Get-Process `
        -Name "PalServer", "PalServer-Win64-Shipping-Cmd" `
        -ErrorAction SilentlyContinue)
}

function Get-ProcessOwnerSid {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId"
    if ($null -eq $process) {
        throw "The pinned PalServer process no longer exists."
    }
    $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
    if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) {
        throw "The pinned PalServer process owner SID could not be verified."
    }
    return ([Security.Principal.SecurityIdentifier]::new($owner.Sid)).Value
}

function Assert-PinnedPalServerSession {
    param(
        [Parameter(Mandatory = $true)][int]$ShippingProcessId,
        [Parameter(Mandatory = $true)][long]$ShippingProcessCreationTimeUtcFileTime,
        [Parameter(Mandatory = $true)][string]$ShippingPath,
        [Parameter(Mandatory = $true)][string]$ShippingOwnerSid,
        [Parameter(Mandatory = $true)][int]$LauncherProcessId,
        [Parameter(Mandatory = $true)][long]$LauncherStartTicks,
        [Parameter(Mandatory = $true)][string]$LauncherPath
    )

    $shipping = Get-Process -Id $ShippingProcessId -ErrorAction Stop
    $launcher = Get-Process -Id $LauncherProcessId -ErrorAction Stop
    if (-not ([IO.Path]::GetFullPath($shipping.Path)).Equals(
            [IO.Path]::GetFullPath($ShippingPath),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFullPath($launcher.Path)).Equals(
            [IO.Path]::GetFullPath($LauncherPath),
            [StringComparison]::OrdinalIgnoreCase) -or
        $shipping.StartTime.ToUniversalTime().ToFileTimeUtc() -ne
            $ShippingProcessCreationTimeUtcFileTime -or
        $launcher.StartTime.ToUniversalTime().Ticks -ne $LauncherStartTicks) {
        throw "The pinned PalServer process identity changed during maintenance."
    }
    $shippingCim = Get-CimInstance `
        Win32_Process `
        -Filter "ProcessId = $ShippingProcessId"
    if ($null -eq $shippingCim -or
        [int]$shippingCim.ParentProcessId -ne $LauncherProcessId -or
        (Get-ProcessOwnerSid -ProcessId $ShippingProcessId) -cne $ShippingOwnerSid) {
        throw "The pinned PalServer launcher/shipping session binding is no longer valid."
    }
}

function Assert-OfficialRestListenerOwnedByProcess {
    param([Parameter(Mandatory = $true)][int]$ShippingProcessId)

    $listeners = @(Get-NetTCPConnection `
        -LocalPort 8212 `
        -State Listen `
        -ErrorAction Stop)
    if ($listeners.Count -eq 0 -or
        @($listeners | Where-Object {
            [int]$_.OwningProcess -ne $ShippingProcessId
        }).Count -ne 0) {
        throw "Every TCP 8212 listener must belong to the pinned Shipping PID."
    }
}

function Get-ExpectedActiveLevelSave {
    param([Parameter(Mandatory = $true)][string]$Root)

    $saveRoot = Join-Path $Root "Pal\Saved\SaveGames\0"
    if (-not (Test-Path -LiteralPath $saveRoot -PathType Container)) {
        throw "Palworld save root is missing: $saveRoot"
    }
    $candidates = @(Get-ChildItem `
        -LiteralPath $saveRoot `
        -Filter "Level.sav" `
        -File `
        -Recurse | Where-Object {
            $_.FullName -notmatch '[\\/]backup[\\/]'
        })
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one active Level.sav; found $($candidates.Count)."
    }
    return $candidates[0]
}

function Wait-FileStable {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(3, 120)][int]$TimeoutSeconds = 45
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $previousFingerprint = $null
    $stableSamples = 0
    do {
        $file = Get-Item -LiteralPath $Path
        $fingerprint = "$($file.Length):$($file.LastWriteTimeUtc.Ticks)"
        if ($fingerprint -ceq $previousFingerprint) {
            $stableSamples++
        }
        else {
            $previousFingerprint = $fingerprint
            $stableSamples = 0
        }
        if ($stableSamples -ge 3) {
            return $file
        }
        Start-Sleep -Milliseconds 750
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Level.sav did not become stable before the safety deadline."
}

function Invoke-OfficialRequest {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("GET", "POST")][string]$Method,
        [Parameter(Mandatory = $true)]
        [ValidateSet("info", "players", "save", "shutdown")]
        [string]$RelativePath,
        [Parameter(Mandatory = $true)][Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)][string]$Authorization,
        [AllowNull()][string]$Body,
        [Parameter(Mandatory = $true)][hashtable]$PinnedSession
    )

    Assert-PinnedPalServerSession @PinnedSession
    Assert-OfficialRestListenerOwnedByProcess `
        -ShippingProcessId $PinnedSession.ShippingProcessId

    $uri = [Uri]::new([Uri]"http://127.0.0.1:8212/v1/api/", $RelativePath)
    if ($uri.Scheme -cne "http" -or $uri.Host -cne "127.0.0.1" -or
        $uri.Port -ne 8212) {
        throw "Official REST URI escaped the fixed loopback endpoint."
    }

    $httpMethod = if ($Method -ceq "POST") {
        [Net.Http.HttpMethod]::Post
    }
    else {
        [Net.Http.HttpMethod]::Get
    }
    $request = [Net.Http.HttpRequestMessage]::new($httpMethod, $uri)
    $response = $null
    try {
        if (-not $request.Headers.TryAddWithoutValidation(
                "Authorization",
                $Authorization)) {
            throw "Official REST Authorization header could not be created."
        }
        [void]$request.Headers.TryAddWithoutValidation("Accept", "application/json")
        if ($Method -ceq "POST") {
            $request.Content = [Net.Http.StringContent]::new(
                $(if ($null -eq $Body) { "{}" } else { $Body }),
                [Text.Encoding]::UTF8,
                "application/json")
        }
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $statusCode = [int]$response.StatusCode
        if ($statusCode -ge 300 -and $statusCode -lt 400) {
            throw "Official REST redirects are forbidden."
        }
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            StatusCode = $statusCode
            Content = $content
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}

$root = (Resolve-Path -LiteralPath $PalServerRoot).Path
$resultFullPath = [IO.Path]::GetFullPath($ResultPath)
Assert-NoReparseAncestor -Path $root -Label "PalServer root"
Assert-NoReparseAncestor -Path $resultFullPath -Label "Result path"

$rootPrefix = $root.TrimEnd('\') + '\'
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if ($resultFullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $resultFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ResultPath must be outside both PalServer and the public repository."
}
if (Test-Path -LiteralPath $resultFullPath) {
    throw "ResultPath already exists: $resultFullPath"
}
$resultDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $resultFullPath))
if ($resultDirectory.TrimEnd('\').Equals(
        ([IO.Path]::GetPathRoot($resultDirectory)).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "ResultPath must be inside a dedicated maintenance directory, not a volume root."
}

$settingsPath = Join-Path $root "Pal\Saved\Config\WindowsServer\PalWorldSettings.ini"
$launcherPath = Join-Path $root "PalServer.exe"
$shippingPath = Join-Path $root "Pal\Binaries\Win64\PalServer-Win64-Shipping-Cmd.exe"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $launcherPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $shippingPath -PathType Leaf)) {
    throw "PalServer settings or executable layout is incomplete."
}

$settingsText = [IO.File]::ReadAllText($settingsPath)
if ($settingsText -notmatch '(?:^|,)RESTAPIEnabled=True(?:,|\))' -or
    $settingsText -notmatch '(?:^|,)RESTAPIPort=8212(?:,|\))') {
    throw "The official REST API is not enabled on the reviewed loopback port 8212."
}
$passwordMatch = [regex]::Match(
    $settingsText,
    '(?:^|,)AdminPassword="((?:\\.|[^"\\])*)"(?=,|\))',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $passwordMatch.Success) {
    throw "AdminPassword could not be parsed from PalWorldSettings.ini."
}

$processes = @(Get-PalServerProcesses)
if ($processes.Count -eq 0) {
    throw "PalServer is already stopped; no save/shutdown evidence was produced."
}
$shippingProcesses = @($processes | Where-Object ProcessName -ceq "PalServer-Win64-Shipping-Cmd")
$launcherProcesses = @($processes | Where-Object ProcessName -ceq "PalServer")
if ($shippingProcesses.Count -ne 1 -or $launcherProcesses.Count -ne 1 -or
    -not $shippingProcesses[0].Path.Equals($shippingPath, [StringComparison]::OrdinalIgnoreCase) -or
    -not $launcherProcesses[0].Path.Equals($launcherPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The running PalServer process pair does not match the requested installation root."
}

$shippingProcessId = [int]$shippingProcesses[0].Id
$launcherProcessId = [int]$launcherProcesses[0].Id
$shippingProcessCreationTimeUtcFileTime = `
    $shippingProcesses[0].StartTime.ToUniversalTime().ToFileTimeUtc()
$launcherStartTicks = $launcherProcesses[0].StartTime.ToUniversalTime().Ticks
$shippingOwnerSid = Get-ProcessOwnerSid -ProcessId $shippingProcessId
$pinnedSession = @{
    ShippingProcessId = $shippingProcessId
    ShippingProcessCreationTimeUtcFileTime = $shippingProcessCreationTimeUtcFileTime
    ShippingPath = $shippingPath
    ShippingOwnerSid = $shippingOwnerSid
    LauncherProcessId = $launcherProcessId
    LauncherStartTicks = $launcherStartTicks
    LauncherPath = $launcherPath
}
if ($shippingProcessId -ne $ExpectedShippingProcessId -or
    $shippingProcessCreationTimeUtcFileTime -ne
        $ExpectedShippingProcessCreationTimeUtcFileTime) {
    throw "The running Shipping process does not match the approved startup session."
}
Assert-PinnedPalServerSession @pinnedSession
Assert-OfficialRestListenerOwnedByProcess -ShippingProcessId $shippingProcessId

$levelSave = Get-ExpectedActiveLevelSave -Root $root
$plan = [ordered]@{
    execute = [bool]$Execute
    loopbackRest = "http://127.0.0.1:8212/v1/api/"
    playerPolicy = "must-be-zero"
    saveFirst = $true
    gracefulShutdown = $true
    shutdownDelaySeconds = $ShutdownDelaySeconds
    shippingProcessId = $shippingProcessId
    shippingProcessCreationTimeUtcFileTime = $shippingProcessCreationTimeUtcFileTime
    launcherProcessId = $launcherProcessId
    resultPath = $resultFullPath
}
if (-not $Execute) {
    $plan | ConvertTo-Json -Depth 4
    return
}
if (-not $MaintenanceWindowAcknowledged) {
    throw "-MaintenanceWindowAcknowledged is required with -Execute."
}

$maintenanceMutex = Enter-RootMaintenanceMutex -Root $root
$startedAt = [DateTimeOffset]::UtcNow
$operatorSid = Get-CurrentOperatorSid
$password = $null
$credentialBytes = $null
$authorization = $null
$httpHandler = $null
$httpClient = $null
$playersDocument = $null
$shutdownAcknowledged = $false
try {
    Assert-PinnedPalServerSession @pinnedSession
    Assert-OfficialRestListenerOwnedByProcess -ShippingProcessId $shippingProcessId
    if (-not (Test-Path -LiteralPath $resultDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $resultDirectory | Out-Null
    }
    Assert-NoReparseAncestor -Path $resultDirectory -Label "Result directory"
    Set-PrivateAcl -Path $resultDirectory -OperatorSid $operatorSid
    if (Test-Path -LiteralPath $resultFullPath) {
        throw "ResultPath appeared during maintenance setup: $resultFullPath"
    }

    $httpHandler = [Net.Http.HttpClientHandler]::new()
    $httpHandler.AllowAutoRedirect = $false
    $httpHandler.UseProxy = $false
    $httpHandler.UseCookies = $false
    $httpHandler.PreAuthenticate = $false
    $httpHandler.UseDefaultCredentials = $false
    $httpClient = [Net.Http.HttpClient]::new($httpHandler, $true)
    $httpClient.Timeout = [TimeSpan]::FromSeconds(30)
    $httpClient.MaxResponseContentBufferSize = 1048576

    $password = ConvertFrom-UnrealQuotedValue $passwordMatch.Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw "AdminPassword is empty; refusing unauthenticated maintenance."
    }
    $credentialBytes = [Text.Encoding]::UTF8.GetBytes("admin:$password")
    $authorization = "Basic " + [Convert]::ToBase64String($credentialBytes)

    $infoResponse = Invoke-OfficialRequest `
        -Method GET `
        -RelativePath "info" `
        -Client $httpClient `
        -Authorization $authorization `
        -Body $null `
        -PinnedSession $pinnedSession
    if ($infoResponse.StatusCode -ne 200) {
        throw "Official REST credential verification failed."
    }

    $playersResponse = Invoke-OfficialRequest `
        -Method GET `
        -RelativePath "players" `
        -Client $httpClient `
        -Authorization $authorization `
        -Body $null `
        -PinnedSession $pinnedSession
    if ($playersResponse.StatusCode -ne 200) {
        throw "Official REST player preflight failed."
    }
    $playersDocument = $playersResponse.Content | ConvertFrom-Json
    if ($null -eq $playersDocument.PSObject.Properties["players"]) {
        throw "Official REST player response did not contain the players array."
    }
    $onlinePlayerCount = @($playersDocument.players).Count
    if ($onlinePlayerCount -ne 0) {
        throw "Maintenance refused because $onlinePlayerCount player(s) are online."
    }

    $beforeSaveLength = $levelSave.Length
    $beforeSaveWriteTimeUtc = $levelSave.LastWriteTimeUtc
    try {
        $saveResponse = Invoke-OfficialRequest `
            -Method POST `
            -RelativePath "save" `
            -Client $httpClient `
            -Authorization $authorization `
            -Body "{}" `
            -PinnedSession $pinnedSession
        if ($saveResponse.StatusCode -ne 200) {
            throw "Official save returned HTTP $($saveResponse.StatusCode)."
        }
    }
    catch {
        throw "Save outcome is failed or uncertain; shutdown was not attempted. $($_.Exception.Message)"
    }

    $stableSave = Wait-FileStable -Path $levelSave.FullName
    $saveSha256 = (Get-FileHash `
        -LiteralPath $stableSave.FullName `
        -Algorithm SHA256).Hash.ToLowerInvariant()

    $shutdownBody = [ordered]@{
        waittime = $ShutdownDelaySeconds
        message = "Server maintenance: controlled Native read-only update."
    } | ConvertTo-Json -Compress
    try {
        $shutdownResponse = Invoke-OfficialRequest `
            -Method POST `
            -RelativePath "shutdown" `
            -Client $httpClient `
            -Authorization $authorization `
            -Body $shutdownBody `
            -PinnedSession $pinnedSession
        $shutdownAcknowledged = $shutdownResponse.StatusCode -eq 200
    }
    catch {
        Write-Warning "Shutdown response was lost; the command will not be retried. Process exit remains authoritative."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ShutdownTimeoutSeconds)
    do {
        $remaining = @(
            Get-Process `
                -Id $shippingProcessId, $launcherProcessId `
                -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) { break }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($remaining.Count -ne 0) {
        throw "PalServer did not exit by the graceful shutdown deadline; no force-stop was used."
    }

    $tcpListeners = @(Get-NetTCPConnection `
        -LocalPort 8212, 25575 `
        -State Listen `
        -ErrorAction SilentlyContinue)
    $udpListeners = @(Get-NetUDPEndpoint `
        -LocalPort 8211, 27015 `
        -ErrorAction SilentlyContinue)
    if ($tcpListeners.Count -ne 0 -or $udpListeners.Count -ne 0) {
        throw "PalServer exited but one or more reviewed game/admin ports remain bound."
    }

    $result = [ordered]@{
        schemaVersion = 1
        completed = $true
        startedAt = $startedAt.ToString("O")
        completedAt = [DateTimeOffset]::UtcNow.ToString("O")
        onlinePlayerCount = 0
        saveHttpStatus = 200
        saveMetadataChanged = (
            $stableSave.Length -ne $beforeSaveLength -or
            $stableSave.LastWriteTimeUtc -ne $beforeSaveWriteTimeUtc)
        saveLength = $stableSave.Length
        saveSha256 = $saveSha256
        shutdownAcknowledged = $shutdownAcknowledged
        processExitVerified = $true
        reviewedPortsReleased = $true
        forceStopUsed = $false
        shippingProcessId = $shippingProcessId
        shippingProcessCreationTimeUtcFileTime = $shippingProcessCreationTimeUtcFileTime
        launcherProcessId = $launcherProcessId
    }
    $json = $result | ConvertTo-Json -Depth 5
    Assert-NoReparseAncestor -Path $resultFullPath -Label "Result path"
    $resultStream = [IO.FileStream]::new(
        $resultFullPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $resultBytes = [Text.UTF8Encoding]::new($false).GetBytes(
            $json + [Environment]::NewLine)
        try {
            $resultStream.Write($resultBytes, 0, $resultBytes.Length)
            $resultStream.Flush($true)
        }
        finally {
            [Array]::Clear($resultBytes, 0, $resultBytes.Length)
        }
    }
    finally {
        $resultStream.Dispose()
    }
    Set-PrivateAcl -Path $resultFullPath -OperatorSid $operatorSid
    $result | ConvertTo-Json -Depth 5
}
finally {
    if ($null -ne $httpClient) { $httpClient.Dispose() }
    elseif ($null -ne $httpHandler) { $httpHandler.Dispose() }
    if ($credentialBytes) {
        [Array]::Clear($credentialBytes, 0, $credentialBytes.Length)
    }
    $password = $null
    $authorization = $null
    $credentialBytes = $null
    $httpClient = $null
    $httpHandler = $null
    $playersDocument = $null
    $settingsText = $null
    Exit-RootMaintenanceMutex -Mutex $maintenanceMutex
}
