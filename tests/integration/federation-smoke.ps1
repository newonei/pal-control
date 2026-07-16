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

function Get-FederationToken([string]$key, [string]$provider, [string]$userId) {
    $hmac = [Security.Cryptography.HMACSHA256]::new(
        [Text.Encoding]::UTF8.GetBytes($key))
    try {
        $digest = $hmac.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes(
                "$($provider.ToLowerInvariant())`n$($userId.ToLowerInvariant())"))
        return "fed1_" + [Convert]::ToBase64String($digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        $hmac.Dispose()
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "pal-control-federation-http-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$identityKey = "http-identity-" + ("i" * 48)
$nodeKey = "http-node-" + ("n" * 48)
$identityKeyFile = Join-Path $tempRoot "identity.key"
$nodeKeyFile = Join-Path $tempRoot "node.key"
[IO.File]::WriteAllText($identityKeyFile, $identityKey, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($nodeKeyFile, $nodeKey, [Text.Encoding]::UTF8)
$matrixPath = Join-Path $repositoryRoot `
    "services\control-api\Compatibility\compatibility-matrix.v1.json"
$controlDll = Join-Path $repositoryRoot `
    "services\control-api\bin\Release\net10.0\PalControl.ControlApi.dll"
if (-not (Test-Path -LiteralPath $controlDll -PathType Leaf)) {
    throw "Built Control API DLL is missing: $controlDll"
}
$port = Get-FreeTcpPort
$baseUri = "http://127.0.0.1:$port"

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
    "Federation__IdentityHmacKey" = ""
    "Federation__IdentityHmacKeyFile" = $identityKeyFile
    "Federation__InboundNodeKey" = ""
    "Federation__InboundNodeKeyFile" = $nodeKeyFile
    "Federation__MaximumRequestBodyBytes" = "2048"
    "Federation__InternalRequestsPerMinute" = "100"
    "Federation__Nodes__0__ServerId" = "alpha"
    "Federation__Nodes__0__DisplayName" = "Alpha"
    "Federation__Nodes__0__Local" = "true"
    "Federation__Nodes__0__BaseUri" = "$baseUri/"
    "Federation__Nodes__0__PortalUrl" = "$baseUri/player/"
    "Federation__Nodes__0__ExpectedCombinationId" = "pal-1.0.0.100427-native-dev36"
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
    Assert-Status $missingKey 401 "missing node key"

    $wrongKey = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        @{ "X-Pal-Control-Node-Key" = ("wrong-" + ("x" * 40)) }
    Assert-Status $wrongKey 401 "wrong node key"

    $health = Invoke-Request $client "GET" `
        "$baseUri/api/v1/internal/federation/health" $null `
        @{ "X-Pal-Control-Node-Key" = $nodeKey }
    Assert-Status $health 200 "authenticated node health"
    $healthBody = Convert-JsonResponse $health "authenticated node health"
    if ($healthBody.serverId -ne "alpha" -or
        $healthBody.compatibility.status -ne "experimental") {
        throw "Internal health returned inaccurate node compatibility: $($health.Text)"
    }

    $invalidToken = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        '{"subjectToken":"steam_raw_identity"}' `
        @{ "X-Pal-Control-Node-Key" = $nodeKey }
    Assert-Status $invalidToken 400 "raw identity rejection"

    $subjectToken = Get-FederationToken $identityKey "steam" "steam_76561198000000001"
    $profileJson = @{ subjectToken = $subjectToken } | ConvertTo-Json -Compress
    for ($index = 0; $index -lt 20; $index++) {
        $profile = Invoke-Request $client "POST" `
            "$baseUri/api/v1/internal/federation/profile" `
            $profileJson `
            @{ "X-Pal-Control-Node-Key" = $nodeKey }
        Assert-Status $profile 200 "read-only profile replay $index"
        $profileBody = Convert-JsonResponse $profile "read-only profile replay $index"
        if ($profileBody.accountExists -ne $false -or
            $null -ne $profileBody.balances -or
            $profileBody.balancesAvailable -ne $false) {
            throw "Missing account was presented as a zero or available wallet: $($profile.Text)"
        }
    }

    $oversizedJson = '{"subjectToken":"' + ("a" * 3000) + '"}'
    $oversized = Invoke-Request $client "POST" `
        "$baseUri/api/v1/internal/federation/profile" `
        $oversizedJson `
        @{ "X-Pal-Control-Node-Key" = $nodeKey }
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
        "PASS: real HTTP internal key boundary, token validation, 20x side-effect-free " +
        "profile replay, oversize rejection, player override rejection, and Viewer health.")
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
