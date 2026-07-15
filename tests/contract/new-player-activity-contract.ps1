[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repository = Get-Content -Raw (Join-Path $root `
    "services\control-api\Extraction\NewPlayerActivityRepository.cs")
$playerEndpoints = Get-Content -Raw (Join-Path $root `
    "services\control-api\Infrastructure\PlayerPortalEndpoints.cs")
$adminEndpoints = Get-Content -Raw (Join-Path $root `
    "services\control-api\Infrastructure\NewPlayerActivityEndpoints.cs")
$openApi = Get-Content -Raw (Join-Path $root `
    "packages\contracts\openapi\control-api.yaml")

$checks = @(
    @{ Name = "activity table"; Text = $repository; Pattern = "CREATE TABLE IF NOT EXISTS new_player_activities" },
    @{ Name = "grant table"; Text = $repository; Pattern = "CREATE TABLE IF NOT EXISTS new_player_activity_grants" },
    @{ Name = "account+version uniqueness"; Text = $repository; Pattern = "UNIQUE (account_id, activity_key, activity_version)" },
    @{ Name = "published immutability trigger"; Text = $repository; Pattern = "immutable_after_publish" },
    @{ Name = "single SQLite transaction"; Text = $repository; Pattern = "InsertNewPlayerActivityGrantAsync" },
    @{ Name = "session enforcement"; Text = $playerEndpoints; Pattern = "authentication.RequireSession(httpContext)" },
    @{ Name = "CSRF enforcement"; Text = $playerEndpoints; Pattern = "authentication.RequireCsrf(httpContext, session)" },
    @{ Name = "origin enforcement filter"; Text = $playerEndpoints; Pattern = "authentication.RequireAllowedOrigin" },
    @{ Name = "world binding"; Text = $playerEndpoints; Pattern = "context.IdentityBinding" },
    @{ Name = "safety gate"; Text = $playerEndpoints; Pattern = "safetyGate.AcquireAsync" },
    @{ Name = "high-risk publish RBAC"; Text = $adminEndpoints; Pattern = "publish" },
    @{ Name = "high-risk policy"; Text = $adminEndpoints; Pattern = "AdminPolicies.EconomyHighRisk" },
    @{ Name = "player claim OpenAPI"; Text = $openApi; Pattern = "/player/me/new-player-activities/{activityKey}/versions/{version}/claim:" },
    @{ Name = "admin publish OpenAPI"; Text = $openApi; Pattern = "/extraction/admin/new-player-activities/{activityKey}/versions/{version}/publish:" },
    @{ Name = "OpenAPI Origin"; Text = $openApi; Pattern = '#/components/parameters/PlayerPortalOrigin' },
    @{ Name = "OpenAPI CSRF"; Text = $openApi; Pattern = '#/components/parameters/PlayerPortalCsrfToken' },
    @{ Name = "OpenAPI idempotency"; Text = $openApi; Pattern = '#/components/parameters/IdempotencyKey' }
)

foreach ($check in $checks) {
    if ($check.Text.IndexOf($check.Pattern, [StringComparison]::Ordinal) -lt 0) {
        throw "New-player activity contract missing $($check.Name): $($check.Pattern)"
    }
}

Write-Host "PASS: new-player activity persistence, security boundary, RBAC, idempotency, and OpenAPI contracts."
