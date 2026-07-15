using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Single policy boundary for the legacy RCON resource-settlement diagnostic.
/// Production and public player portals always use Native settlement and fail
/// closed when stable Native capabilities are unavailable.
/// </summary>
internal static class DevelopmentRconSettlementPolicy
{
    public static bool IsAllowed(
        IWebHostEnvironment? environment,
        IConfiguration? configuration,
        ExtractionRconOptions rcon,
        EconomySafetyOptions? safety) =>
        GetViolations(environment, configuration, rcon, safety).Count == 0;

    public static IReadOnlyList<string> GetViolations(
        IWebHostEnvironment? environment,
        IConfiguration? configuration,
        ExtractionRconOptions rcon,
        EconomySafetyOptions? safety)
    {
        ArgumentNullException.ThrowIfNull(rcon);

        List<string> violations = [];
        if (environment?.IsDevelopment() != true)
        {
            violations.Add("the host environment must be Development");
        }
        if (configuration?.GetValue<bool>("Security:DevelopmentMode") != true)
        {
            violations.Add("Security:DevelopmentMode must be true");
        }
        if (configuration?.GetValue<bool>("PlayerPortal:PublicSteam") == true)
        {
            violations.Add("PlayerPortal:PublicSteam must be false");
        }
        if (!rcon.Enabled)
        {
            violations.Add("ExtractionMode:Rcon:Enabled must be true");
        }
        if (!rcon.AllowDevelopmentSettlement)
        {
            violations.Add(
                "ExtractionMode:Rcon:AllowDevelopmentSettlement must be true");
        }
        if (safety is null || safety.RequireNativeForResourceExchange)
        {
            violations.Add(
                "ExtractionMode:Safety:RequireNativeForResourceExchange must be false");
        }
        return violations;
    }
}
