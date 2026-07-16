[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$utf8 = [Text.UTF8Encoding]::new($false)
$openApiPath = Join-Path $repositoryRoot `
    "packages\contracts\openapi\control-api.yaml"
$endpointPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\EconomyOperationsEndpoints.cs"
$runnerPath = Join-Path $repositoryRoot "tools\soak\SoakRunner.cs"
$programPath = Join-Path $repositoryRoot "services\control-api\Program.cs"
$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
$endpoint = [IO.File]::ReadAllText($endpointPath, $utf8)
$runner = [IO.File]::ReadAllText($runnerPath, $utf8)
$program = [IO.File]::ReadAllText($programPath, $utf8)

foreach ($fragment in @(
        "runtime:",
        "required: [instance, sessions, gc]",
        "dataDirectoryFingerprint:",
        "logDirectoryFingerprint:",
        "active-session count and aggregate GC counters",
        "heapSizeBytes:",
        "totalAllocatedBytes:",
        "gen0Collections:")) {
    if ($openApi.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "OpenAPI is missing soak-observability fragment '$fragment'."
    }
}
foreach ($fragment in @(
        "PlayerPortalSessionRegistry playerSessions",
        "processId = Environment.ProcessId",
        "RuntimePathFingerprint(dataDirectory)",
        "active = playerSessions.ActiveCount(generatedAt)",
        "heapSizeBytes = gc.HeapSizeBytes",
        "totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false)")) {
    if ($endpoint.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "The runtime operations projection is missing '$fragment'."
    }
}
foreach ($fragment in @(
        'HttpMethod.Get',
        'X-Pal-Admin-Key',
        'MaximumOperationsResponseBytes',
        '"/health/instance"',
        '"/health/live"',
        '"/health/ready"')) {
    if ($runner.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "The soak runner lost its read-only/protected probe boundary '$fragment'."
    }
}
foreach ($fragment in @(
        'app.MapGet("/health/instance"',
        'processStartedAtUtc = process.StartTime.ToUniversalTime()',
        'dataDirectoryFingerprint = EconomyOperationsEndpoints.RuntimePathFingerprint(dataDirectory)')) {
    if ($program.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "The unauthenticated pre-key instance binding is missing '$fragment'."
    }
}
foreach ($forbidden in @(
        "session.UserId",
        "session.PlayerUid",
        "SessionToken",
        "CsrfToken",
        "Environment.CommandLine")) {
    $runtimeStart = $endpoint.IndexOf("runtime = new", [StringComparison]::Ordinal)
    $worldStart = $endpoint.IndexOf("world = new", $runtimeStart, [StringComparison]::Ordinal)
    if ($runtimeStart -lt 0 -or $worldStart -lt 0) {
        throw "Could not isolate the runtime projection for privacy checks."
    }
    $runtimeProjection = $endpoint.Substring($runtimeStart, $worldStart - $runtimeStart)
    if ($runtimeProjection.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "Runtime observability leaked prohibited session/process material '$forbidden'."
    }
}

Write-Host "PASS: soak runtime metrics are aggregate-only and documented as a Viewer read model."
