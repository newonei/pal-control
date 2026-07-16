[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-RepositoryFile([string] $relativePath) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Team economy contract failed: missing '$relativePath'."
    }
    return [IO.File]::ReadAllText($path, $utf8)
}

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Team economy contract failed: $message"
    }
}

function Require-Ordinal([string] $text, [string] $value, [string] $message) {
    Assert-Contract ($text.IndexOf($value, [StringComparison]::Ordinal) -ge 0) $message
}

function Get-Block(
    [string] $document,
    [string] $marker,
    [string] $nextPattern,
    [string] $description) {
    $start = $document.IndexOf($marker, [StringComparison]::Ordinal)
    Assert-Contract ($start -ge 0) "$description is missing."
    $bodyStart = $start + $marker.Length
    $next = [regex]::Match($document.Substring($bodyStart), $nextPattern)
    $end = if ($next.Success) { $bodyStart + $next.Index } else { $document.Length }
    return $document.Substring($start, $end - $start)
}

$program = Read-RepositoryFile "services\control-api\Program.cs"
$endpoints = Read-RepositoryFile `
    "services\control-api\Infrastructure\TeamEconomyEndpoints.cs"
$store = Read-RepositoryFile `
    "services\control-api\Infrastructure\TeamEconomyStore.cs"
$projection = Read-RepositoryFile `
    "services\control-api\Infrastructure\TeamEconomyProjection.cs"
$models = Read-RepositoryFile `
    "services\control-api\Infrastructure\TeamEconomyModels.cs"
$portal = Read-RepositoryFile "apps\player-web\src\TeamEconomyPanel.tsx"
$openApi = Read-RepositoryFile "packages\contracts\openapi\control-api.yaml"

foreach ($fragment in @(
        'AddSingleton<TeamEconomyStore>()',
        'AddHostedService<TeamEconomyProjectionWorker>()',
        'MapTeamEconomyEndpoints()')) {
    Require-Ordinal $program $fragment "Program registration omits '$fragment'."
}

foreach ($route in @(
        'MapGet(""',
        'MapGet("/leaderboards/{metric}"',
        'MapPost("/teams"',
        'MapPost("/invite/rotate"',
        'MapPost("/join"',
        'MapPost("/leave"',
        'MapPost("/owner/transfer"',
        'MapPost("/dissolve"')) {
    Require-Ordinal $endpoints $route "HTTP endpoint '$route' is missing."
}
foreach ($security in @(
        '.AllowAnonymous()',
        'authentication.RequireSession(httpContext)',
        'authentication.RequireAllowedOrigin(httpContext)',
        'authentication.RequireCsrf(httpContext, session)',
        'RequireIdempotencyKey(httpContext.Request)',
        'RejectIdentityOverride(httpContext')) {
    Require-Ordinal $endpoints $security "HTTP boundary omits '$security'."
}
Assert-Contract (-not [regex]::IsMatch(
        $endpoints,
        'GetAccountContextAsync\(\s*request\.',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)) `
    "an HTTP request body can select the authoritative account context."

foreach ($schemaFragment in @(
        'team_economy_teams',
        'team_economy_memberships',
        'team_economy_invites',
        'token_digest TEXT NOT NULL',
        'team_economy_idempotency',
        'team_economy_projection_events',
        'team_economy_projection_exclusions',
        'team_economy_goal_progress',
        'CREATE UNIQUE INDEX IF NOT EXISTS ux_team_active_membership')) {
    Require-Ordinal $store $schemaFragment "durable store omits '$schemaFragment'."
}
foreach ($securityFragment in @(
        'new HMACSHA256(_pepper)',
        'CryptographicOperations.FixedTimeEquals',
        'BeginTransaction(deferred: false)',
        'TEAM_IDEMPOTENCY_CONFLICT')) {
    Require-Ordinal $store $securityFragment "store safety control '$securityFragment' is missing."
}
Assert-Contract ($store.IndexOf(
        'token TEXT',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    "the invitation token appears to be stored in plaintext."

foreach ($authoritativeSource in @(
        'extraction_settlement_runs',
        'extraction_events',
        'reliable_task_ranking_rewards',
        'season_leaderboard_exclusions',
        'ShopDelivery',
        'Delivered',
        'Settled',
        'TeamEconomyLimits.WebSafeInteger')) {
    Require-Ordinal $projection $authoritativeSource `
        "projection omits authoritative source/state '$authoritativeSource'."
}
Assert-Contract (-not [regex]::IsMatch(
        $projection,
        '(?i)(client|browser|frontend)[-_ ]?(harvest|kill|death|pvp|event|fact)[-_ ]?(payload|upload|report)')) `
    "projection appears to accept a client-reported gameplay fact."

foreach ($goal in @(
        'ResourceItemsGoal',
        'ResourceValueGoal',
        'ReliableTaskPointsGoal',
        'DeliveredOrdersGoal')) {
    Require-Ordinal $models $goal "the frozen four-goal template omits '$goal'."
}

Require-Ordinal $portal 'token intentionally lives only in React memory' `
    "portal does not document the invitation token memory-only boundary."
foreach ($storage in @('localStorage.setItem', 'sessionStorage.setItem')) {
    Assert-Contract ($portal.IndexOf(
            $storage,
            [StringComparison]::Ordinal) -lt 0) `
        "portal persists team invitation material through $storage."
}

foreach ($path in @(
        '/player/me/team-economy:',
        '/player/me/team-economy/leaderboards/{metric}:',
        '/player/me/team-economy/teams:',
        '/player/me/team-economy/invite/rotate:',
        '/player/me/team-economy/join:',
        '/player/me/team-economy/leave:',
        '/player/me/team-economy/owner/transfer:',
        '/player/me/team-economy/dissolve:')) {
    Require-Ordinal $openApi $path "OpenAPI is missing '$path'."
}
$dashboardSchema = Get-Block $openApi "    TeamEconomyDashboard:" `
    '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$' "team dashboard schema"
$leaderboardSchema = Get-Block $openApi "    TeamEconomyLeaderboardEntry:" `
    '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$' "team leaderboard entry schema"
foreach ($publicSchema in @($dashboardSchema, $leaderboardSchema)) {
    foreach ($forbidden in @(
            'accountId', 'userId', 'playerUid', 'steamId', 'displayName')) {
        Assert-Contract ($publicSchema.IndexOf(
                $forbidden,
                [StringComparison]::OrdinalIgnoreCase) -lt 0) `
            "a public team schema exposes '$forbidden'."
    }
}

Write-Host (
    "PASS: team economy session/CSRF/idempotency boundary, HMAC invitations, " +
    "authoritative projection, four goals, privacy, portal storage, and OpenAPI contract.")
