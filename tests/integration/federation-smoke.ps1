$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\federation\PalControl.Federation.Harness.csproj"

& dotnet run --project $project --configuration Release --no-restore -- $repositoryRoot
if ($LASTEXITCODE -ne 0) {
    throw "Federation harness failed with exit code $LASTEXITCODE."
}

Add-Type -AssemblyName System.Net.Http

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

function Invoke-Request(
    [Net.Http.HttpClient]$client,
    [string]$method,
    [string]$uri,
    [AllowNull()][string]$json,
    [hashtable]$headers = @{}) {
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::new($method),
        $uri)
    if (-not [string]::IsNullOrEmpty($json)) {
        $request.Content = [Net.Http.StringContent]::new(
            $json,
            [Text.Encoding]::UTF8,
            "application/json")
    }
    try {
        foreach ($entry in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$entry.Key,
                    [string]$entry.Value)) {
                throw "Could not add request header $($entry.Key)."
            }
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                Status = [int]$response.StatusCode
                Text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Assert-Status([object]$response, [int]$expected, [string]$context) {
    if ($response.Status -ne $expected) {
        throw "$context expected HTTP $expected, received $($response.Status): $($response.Text)"
    }
}

function Convert-JsonResponse([object]$response, [string]$context) {
    try {
        return $response.Text | ConvertFrom-Json
    }
    catch {
        throw "$context returned invalid JSON: $($response.Text)"
    }
}

function Convert-ToBase64Url([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-FederationToken(
    [string]$key,
    [string]$keyId,
    [string]$caller,
    [string]$target,
    [string]$provider,
    [string]$userId) {
    $hmac = [Security.Cryptography.HMACSHA256]::new(
        [Text.Encoding]::UTF8.GetBytes($key))
    try {
        $canonical = (
            "pal-control-federation-subject-v2`n{0}`n{1}`n{2}`n{3}`n{4}" -f `
                $keyId,
                $caller.ToLowerInvariant(),
                $target.ToLowerInvariant(),
                $provider.ToLowerInvariant(),
                $userId.ToLowerInvariant())
        $digest = $hmac.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($canonical))
        return "fed2_" + (Convert-ToBase64Url $digest)
    }
    finally {
        $hmac.Dispose()
    }
}

function Get-SignedFederationHeaders(
    [string]$method,
    [string]$path,
    [AllowNull()][string]$json,
    [string]$caller,
    [string]$target,
    [string]$signingKeyId,
    [string]$signingKey,
    [string]$identityKeyId,
    [string]$expectedCombination,
    [long]$timestamp = 0,
    [string]$nonce = "") {
    if ($timestamp -eq 0) {
        $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    }
    if ([string]::IsNullOrWhiteSpace($nonce)) {
        $nonceBytes = [byte[]]::new(16)
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $generator.GetBytes($nonceBytes)
        }
        finally {
            $generator.Dispose()
        }
        $nonce = Convert-ToBase64Url $nonceBytes
    }
    [byte[]]$body = @()
    if ($null -ne $json) {
        $body = [Text.Encoding]::UTF8.GetBytes($json)
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $contentDigest = $sha256.ComputeHash([byte[]]$body)
    }
    finally {
        $sha256.Dispose()
    }
    $contentHash = [BitConverter]::ToString($contentDigest).Replace('-', '').ToLowerInvariant()
    $canonical = @(
        "pal-control-federation-request-v2",
        $method.ToUpperInvariant(),
        $path,
        $caller,
        $target,
        $signingKeyId,
        $identityKeyId,
        $expectedCombination,
        [string]$timestamp,
        $nonce,
        $contentHash) -join "`n"
    $hmac = [Security.Cryptography.HMACSHA256]::new(
        [Text.Encoding]::UTF8.GetBytes($signingKey))
    try {
        $signature = Convert-ToBase64Url (
            $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))
    }
    finally {
        $hmac.Dispose()
    }
    return @{
        "X-Pal-Control-Federation-Version" = "2"
        "X-Pal-Control-Caller" = $caller
        "X-Pal-Control-Target" = $target
        "X-Pal-Control-Signing-Key-Id" = $signingKeyId
        "X-Pal-Control-Identity-Key-Id" = $identityKeyId
        "X-Pal-Control-Expected-Combination" = $expectedCombination
        "X-Pal-Control-Timestamp" = [string]$timestamp
        "X-Pal-Control-Nonce" = $nonce
        "X-Pal-Control-Content-SHA256" = $contentHash
        "X-Pal-Control-Signature" = $signature
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "pal-control-federation-http-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$identityKey = "http-identity-" + ("i" * 48)
$identityKeyV2 = "http-identity-v2-" + ("j" * 48)
$nodeKeyV1 = "http-node-v1-" + ("o" * 48)
$nodeKeyV2 = "http-node-v2-" + ("n" * 48)
$outboundNodeKey = "http-outbound-" + ("z" * 48)
$identityKeyFile = Join-Path $tempRoot "identity-v1.key"
$identityKeyV2File = Join-Path $tempRoot "identity-v2.key"
$nodeKeyV1File = Join-Path $tempRoot "node-v1.key"
$nodeKeyV2File = Join-Path $tempRoot "node-v2.key"
$outboundNodeKeyFile = Join-Path $tempRoot "outbound-node.key"
[IO.File]::WriteAllText($identityKeyFile, $identityKey, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($identityKeyV2File, $identityKeyV2, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($nodeKeyV1File, $nodeKeyV1, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($nodeKeyV2File, $nodeKeyV2, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($outboundNodeKeyFile, $outboundNodeKey, [Text.Encoding]::UTF8)
$matrixPath = Join-Path $repositoryRoot `
    "services\control-api\Compatibility\compatibility-matrix.v1.json"
$controlDll = Join-Path $repositoryRoot `
    "services\control-api\bin\Release\net10.0\PalControl.ControlApi.dll"
if (-not (Test-Path -LiteralPath $controlDll -PathType Leaf)) {
    throw "Built Control API DLL is missing: $controlDll"
}
$port = Get-FreeTcpPort
$baseUri = "http://127.0.0.1:$port"
$remotePort = Get-FreeTcpPort
$combinationId = "pal-1.0.0.100427-native-dev36"

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = "dotnet"
$startInfo.Arguments = (
    '"{0}" --urls "{1}"' -f `
        $controlDll,
        $baseUri)
$startInfo.WorkingDirectory = Split-Path -Parent $controlDll
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$settings = [ordered]@{
    "ASPNETCORE_ENVIRONMENT" = "Development"
    "ExtractionMode__Enabled" = "false"
    "ExtractionMode__ServerId" = "alpha"
    "ExtractionMode__Persistence__DataDirectory" = (Join-Path $tempRoot "data")
    "CommandPersistence__DataDirectory" = (Join-Path $tempRoot "commands")
    "PlayerPortal__Enabled" = "false"
    "Federation__Enabled" = "true"
    "Federation__LocalServerId" = "alpha"
    "Federation__MatrixPath" = $matrixPath
    "Federation__AllowExperimentalInDevelopment" = "true"
    "Federation__ProtocolVersion" = "2"
    "Federation__IdentityKeys__0__KeyId" = "identity-v1"
    "Federation__IdentityKeys__0__KeyFile" = $identityKeyFile
    "Federation__IdentityKeys__0__Revoked" = "false"
    "Federation__IdentityKeys__1__KeyId" = "identity-v2"
    "Federation__IdentityKeys__1__KeyFile" = $identityKeyV2File
    "Federation__IdentityKeys__1__Revoked" = "false"
    "Federation__InboundPeers__0__ServerId" = "beta"
    "Federation__InboundPeers__0__Revoked" = "false"
    "Federation__InboundPeers__0__SigningKeys__0__KeyId" = "beta-to-alpha-v1"
    "Federation__InboundPeers__0__SigningKeys__0__KeyFile" = $nodeKeyV1File
    "Federation__InboundPeers__0__SigningKeys__0__Revoked" = "true"
    "Federation__InboundPeers__0__SigningKeys__1__KeyId" = "beta-to-alpha-v2"
    "Federation__InboundPeers__0__SigningKeys__1__KeyFile" = $nodeKeyV2File
    "Federation__InboundPeers__0__SigningKeys__1__Revoked" = "false"
    "Federation__MaximumRequestBodyBytes" = "2048"
    "Federation__InternalRequestsPerMinute" = "100"
    "Federation__MaximumClockSkewSeconds" = "120"
    "Federation__Nodes__0__ServerId" = "alpha"
    "Federation__Nodes__0__DisplayName" = "Alpha"
    "Federation__Nodes__0__Local" = "true"
    "Federation__Nodes__0__BaseUri" = "$baseUri/"
    "Federation__Nodes__0__PortalUrl" = "$baseUri/player/"
    "Federation__Nodes__0__ExpectedCombinationId" = $combinationId
    "Federation__Nodes__1__ServerId" = "beta"
    "Federation__Nodes__1__DisplayName" = "Beta"
    "Federation__Nodes__1__Local" = "false"
    "Federation__Nodes__1__BaseUri" = "http://127.0.0.1:$remotePort/"
    "Federation__Nodes__1__PortalUrl" = "http://127.0.0.1:$remotePort/player/"
    "Federation__Nodes__1__ExpectedCombinationId" = $combinationId
    "Federation__Nodes__1__SigningKeyId" = "alpha-to-beta-v1"
    "Federation__Nodes__1__IdentityKeyId" = "identity-v1"
    "Federation__Nodes__1__NodeKeyFile" = $outboundNodeKeyFile
    "Security__AdminAuthentication__Enabled" = "true"
    "Security__AdminAuthentication__EnableLoopbackDevelopmentPrincipal" = "true"
    "Security__AdminAuthentication__DevelopmentPrincipalSubject" = "federation-http-test"
    "Security__AdminAuthentication__Principals__0__Subject" = "federation-http-test"
    "Security__AdminAuthentication__Principals__0__ApiKeySha256" = "87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060"
    "Security__AdminAuthentication__Principals__0__Roles__0" = "Owner"
    "Security__AdminAuthentication__Principals__0__TotpSecretBase32" = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
}
foreach ($entry in $settings.GetEnumerator()) {
    $startInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)
$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo

try {
    if (-not $process.Start()) {
        throw "Could not start Control API HTTP smoke process."
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $probe = $null
    $lastProbeFailure = "no request attempted"
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $stdout = $process.StandardOutput.ReadToEnd()
            $stderr = $process.StandardError.ReadToEnd()
            throw "Control API exited early ($($process.ExitCode)).`n$stdout`n$stderr"
        }
        try {
            $probe = Invoke-Request $client "GET" "$baseUri/health/live" $null
            if ($probe.Status -eq 200) { break }
        }
        catch {
            $lastProbeFailure = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $probe -or $probe.Status -ne 200) {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        throw "Timed out waiting for Control API federation HTTP smoke. Last probe: $lastProbeFailure`n$stdout`n$stderr"
    }

    $missingKey = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null
    Assert-Status $missingKey 401 "missing request signature"

    $revokedHeaders = Get-SignedFederationHeaders `
        "GET" "/api/v1/internal/federation/health" $null `
        "beta" "alpha" "beta-to-alpha-v1" $nodeKeyV1 "-" $combinationId
    $revokedKey = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        $revokedHeaders
    Assert-Status $revokedKey 401 "separately revoked peer signing key"

    $healthHeaders = Get-SignedFederationHeaders `
        "GET" "/api/v1/internal/federation/health" $null `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "-" $combinationId
    $health = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        $healthHeaders
    Assert-Status $health 200 "signed peer node health"
    $healthBody = Convert-JsonResponse $health "signed peer node health"
    if ($healthBody.serverId -ne "alpha" -or
        $healthBody.compatibility.status -ne "experimental") {
        throw "Internal health returned inaccurate node compatibility: $($health.Text)"
    }
    $replayedHealth = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        $healthHeaders
    Assert-Status $replayedHealth 401 "replayed signed health request"

    $wrongTargetHeaders = Get-SignedFederationHeaders `
        "GET" "/api/v1/internal/federation/health" $null `
        "beta" "gamma" "beta-to-alpha-v2" $nodeKeyV2 "-" $combinationId
    $wrongTarget = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        $wrongTargetHeaders
    Assert-Status $wrongTarget 401 "signature bound to another target"

    $staleHeaders = Get-SignedFederationHeaders `
        "GET" "/api/v1/internal/federation/health" $null `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "-" $combinationId `
        ([DateTimeOffset]::UtcNow.AddMinutes(-10).ToUnixTimeSeconds())
    $stale = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        $staleHeaders
    Assert-Status $stale 401 "stale signed request"

    $invalidTokenJson = [ordered]@{
        protocolVersion = 2
        callerServerId = "beta"
        targetServerId = "alpha"
        identityKeyId = "identity-v1"
        subjectToken = "steam_raw_identity"
    } | ConvertTo-Json -Compress
    $invalidTokenHeaders = Get-SignedFederationHeaders `
        "POST" "/api/v1/internal/federation/profile" $invalidTokenJson `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "identity-v1" $combinationId
    $invalidToken = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $invalidTokenJson `
        $invalidTokenHeaders
    Assert-Status $invalidToken 400 "raw identity rejection"

    $subjectToken = Get-FederationToken `
        $identityKey "identity-v1" "beta" "alpha" "steam" "steam_76561198000000001"
    $profileJson = [ordered]@{
        protocolVersion = 2
        callerServerId = "beta"
        targetServerId = "alpha"
        identityKeyId = "identity-v1"
        subjectToken = $subjectToken
    } | ConvertTo-Json -Compress
    for ($index = 0; $index -lt 20; $index++) {
        $profileHeaders = Get-SignedFederationHeaders `
            "POST" "/api/v1/internal/federation/profile" $profileJson `
            "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "identity-v1" $combinationId
        $profile = Invoke-Request $client "POST" `
            "$baseUri/api/v1/internal/federation/profile" `
            $profileJson `
            $profileHeaders
        Assert-Status $profile 200 "read-only profile replay $index"
        $profileBody = Convert-JsonResponse $profile "read-only profile replay $index"
        if ($profileBody.accountExists -ne $false -or
            $null -ne $profileBody.balances -or
            $profileBody.balancesAvailable -ne $false) {
            throw "Missing account was presented as a zero or available wallet: $($profile.Text)"
        }
    }

    $subjectTokenV2 = Get-FederationToken `
        $identityKeyV2 "identity-v2" "beta" "alpha" "steam" "steam_76561198000000001"
    $profileV2Json = [ordered]@{
        protocolVersion = 2
        callerServerId = "beta"
        targetServerId = "alpha"
        identityKeyId = "identity-v2"
        subjectToken = $subjectTokenV2
    } | ConvertTo-Json -Compress
    $profileV2Headers = Get-SignedFederationHeaders `
        "POST" "/api/v1/internal/federation/profile" $profileV2Json `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "identity-v2" $combinationId
    $profileV2 = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $profileV2Json `
        $profileV2Headers
    Assert-Status $profileV2 200 "rotated identity key handshake"

    $tamperedJson = $profileJson.Replace($subjectToken, $subjectTokenV2)
    $tamperedHeaders = Get-SignedFederationHeaders `
        "POST" "/api/v1/internal/federation/profile" $profileJson `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "identity-v1" $combinationId
    $tampered = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $tamperedJson `
        $tamperedHeaders
    Assert-Status $tampered 401 "tampered signed subject body"

    $callerMismatchJson = $profileJson.Replace('"callerServerId":"beta"', '"callerServerId":"gamma"')
    $callerMismatchHeaders = Get-SignedFederationHeaders `
        "POST" "/api/v1/internal/federation/profile" $callerMismatchJson `
        "beta" "alpha" "beta-to-alpha-v2" $nodeKeyV2 "identity-v1" $combinationId
    $callerMismatch = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $callerMismatchJson `
        $callerMismatchHeaders
    Assert-Status $callerMismatch 400 "authenticated caller/body caller mismatch"

    $oversizedJson = '{"subjectToken":"' + ("a" * 3000) + '"}'
    $oversized = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $oversizedJson
    Assert-Status $oversized 413 "oversized internal profile request"

    $override = Invoke-Request $client "GET" `
        "$baseUri/api/v1/player/me/federation?userId=steam_forged" $null
    Assert-Status $override 400 "player identity override"

    $adminHealth = Invoke-Request $client "GET" `
        "$baseUri/api/v1/admin/federation/health" $null
    Assert-Status $adminHealth 200 "viewer federation health"
    $adminMatrix = Invoke-Request $client "GET" `
        "$baseUri/api/v1/admin/federation/compatibility-matrix" $null
    Assert-Status $adminMatrix 200 "viewer compatibility matrix"
    $matrixBody = Convert-JsonResponse $adminMatrix "viewer compatibility matrix"
    if ($matrixBody.combinations.Count -ne 2 -or
        $matrixBody.combinations[1].status -ne "quarantined") {
        throw "Viewer matrix did not preserve quarantine evidence."
    }

    Write-Host (
        "PASS: real HTTP peer-scoped v2 signatures, caller/target/subject binding, nonce replay/stale/tamper rejection, " +
        "signing-key revocation, identity-key rotation, 20x side-effect-free profile replay, oversize rejection, " +
        "player override rejection, and Viewer health.")
}
finally {
    $client.Dispose()
    $handler.Dispose()
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(10000) | Out-Null
    }
    $process.Dispose()
    try {
        [IO.Directory]::Delete($tempRoot, $true)
    }
    catch {
        # Cleanup failure must not hide the HTTP boundary result.
    }
}
